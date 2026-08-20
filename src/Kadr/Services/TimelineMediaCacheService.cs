using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using KadrStudio.Application.Caching;
using KadrStudio.Core.Domain;
using KadrStudio.Infrastructure.Caching;

namespace KadrStudio.Services;

public sealed class TimelineMediaCacheService : IAsyncDisposable
{
    private readonly FfmpegLocator _locator;
    private readonly ProcessRunner _processRunner;
    private readonly IArtifactStore _artifacts;
    private readonly bool _ownsArtifacts;
    private readonly SemaphoreSlim _thumbnailWorkers = new(1, 1);
    private readonly SemaphoreSlim _waveformWorker = new(1, 1);
    private readonly ConcurrentDictionary<MediaCacheKey, Lazy<Task<WaveformPyramid>>> _waveformJobs = [];
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposeState;

    // Keeps one long recording from filling the artifact memory budget. The
    // complete source is still decoded and represented; only the finest visual
    // waveform level is made coarser for very long media.
    private const int MaximumBaseWaveformPeaks = 100_000;
    private const int WaveformSampleRate = 12_000;

    public TimelineMediaCacheService(
        FfmpegLocator locator,
        ProcessRunner processRunner,
        string? cacheRoot = null,
        IArtifactStore? artifacts = null)
    {
        _locator = locator;
        _processRunner = processRunner;
        _ownsArtifacts = artifacts is null;
        _artifacts = artifacts ?? new DiskMediaArtifactCache(
            cacheRoot ?? ThumbnailService.DefaultArtifactRoot());
    }

    public async Task<TimelineMediaArtifacts> PrepareAsync(
        MediaSource source,
        CancellationToken cancellationToken = default)
    {
        if (source.Kind == MediaKind.Image)
            return new TimelineMediaArtifacts(WaveformPyramid.Empty);

        _locator.EnsureAvailable();
        var waveform = source.HasAudio
            ? await PrepareWaveformAsync(source, cancellationToken)
            : WaveformPyramid.Empty;
        return new TimelineMediaArtifacts(waveform);
    }

    /// <summary>
    /// Materializes one exact timeline tile on demand. Callers request only the
    /// source times currently visible in the viewport; the artifact store keeps
    /// completed tiles across scroll and application sessions.
    /// </summary>
    public async Task<string?> GetThumbnailAsync(
        MediaSource source,
        TimelineTime sourceTime,
        CancellationToken cancellationToken = default)
    {
        if (source.Kind == MediaKind.Image) return source.Path;
        if (source.Kind != MediaKind.Video || source.OnlineState != MediaOnlineState.Online || !File.Exists(source.Path))
            return null;

        _locator.EnsureAvailable();
        var maximumTicks = Math.Max(0, source.Duration.Ticks - 1);
        var bounded = new TimelineTime(Math.Clamp(sourceTime.Ticks, 0, maximumTicks));
        var exact = bounded.SnapToFrame(source.FrameRate ?? FrameRate.Fps30);
        var key = Key(source, MediaArtifactKind.TimelineThumbnail, 192, exact.Ticks, 3);
        var cached = await _artifacts.TryGetPayloadPathAsync(key, ".jpg", cancellationToken);
        if (cached is not null) return cached;

        await _thumbnailWorkers.WaitAsync(cancellationToken);
        try
        {
            // A queued request may have been completed by another viewport while
            // this request waited for one of the bounded extraction workers.
            cached = await _artifacts.TryGetPayloadPathAsync(key, ".jpg", cancellationToken);
            if (cached is not null) return cached;

            var temporary = Path.Combine(KadrLocalDataPaths.TempRoot, "artifacts", $"{Guid.NewGuid():N}.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
            try
            {
                var result = await _processRunner.RunAsync(_locator.FfmpegPath,
                    ["-hide_banner", "-nostdin", "-loglevel", "error", "-threads", "1", "-y",
                     "-ss", Invariant(exact.TotalSeconds),
                     "-i", source.Path, "-frames:v", "1",
                     "-vf", "scale=192:108:force_original_aspect_ratio=increase,crop=192:108",
                     "-q:v", "5", temporary], cancellationToken: cancellationToken);
                return result.ExitCode == 0 && File.Exists(temporary)
                    ? await _artifacts.PutFileAsync(key, temporary, ".jpg", cancellationToken)
                    : null;
            }
            finally { TryDelete(temporary); }
        }
        finally
        {
            _thumbnailWorkers.Release();
        }
    }

    private async Task<WaveformPyramid> PrepareWaveformAsync(
        MediaSource source, CancellationToken cancellationToken)
    {
        var key = Key(source, MediaArtifactKind.Waveform, 0, 0, 6);
        var lazy = _waveformJobs.GetOrAdd(key, _ => new Lazy<Task<WaveformPyramid>>(
            () => BuildWaveformAsync(source, key, cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication));
        var task = lazy.Value;
        _ = task.ContinueWith(
            _ => _waveformJobs.TryRemove(new KeyValuePair<MediaCacheKey, Lazy<Task<WaveformPyramid>>>(key, lazy)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<WaveformPyramid> BuildWaveformAsync(
        MediaSource source,
        MediaCacheKey key,
        CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _shutdown.Token);
        cancellationToken = linkedCancellation.Token;
        await _waveformWorker.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cached = await _artifacts.TryGetAsync(key, cancellationToken);
            if (cached is not null)
            {
                try
                {
                    return WaveformPyramidCodec.Decode(cached.Value.Span);
                }
                catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException) { }
            }

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = _locator.FfmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[]
            {
                "-hide_banner", "-nostdin", "-loglevel", "error", "-threads", "1",
                "-i", source.Path, "-vn", "-map", "0:a:0",
                "-ac", "2", "-ar", WaveformSampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-c:a", "pcm_f32le", "-f", "f32le", "pipe:1"
            }) startInfo.ArgumentList.Add(argument);
            using var process = new System.Diagnostics.Process { StartInfo = startInfo };
            if (!process.Start()) throw new InvalidOperationException("FFmpeg waveform decoder did not start.");
            using var registration = cancellationToken.Register(() => TryKill(process));
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var builder = new WaveformPyramidBuilder(
                WaveformSampleRate,
                CalculateFramesPerPeak(source.Duration.TotalSeconds));
            var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
            var carry = 0;
            try
            {
                while (true)
                {
                    var read = await process.StandardOutput.BaseStream.ReadAsync(
                        buffer.AsMemory(carry, buffer.Length - carry), cancellationToken);
                    if (read == 0) break;
                    var bytes = carry + read;
                    var complete = bytes - bytes % (sizeof(float) * 2);
                    builder.AddInterleavedStereo(MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, complete)));
                    carry = bytes - complete;
                    if (carry > 0) buffer.AsSpan(complete, carry).CopyTo(buffer);
                }
                await process.WaitForExitAsync(cancellationToken);
                var error = await errorTask;
                if (process.ExitCode != 0) throw new InvalidOperationException($"FFmpeg waveform decode failed: {error.Trim()}");
                var pyramid = builder.Build();
                if (pyramid.IsEmpty) return pyramid;
                await _artifacts.PutAsync(key, WaveformPyramidCodec.Encode(pyramid), cancellationToken);
                return pyramid;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                TryKill(process);
            }
        }
        finally
        {
            _waveformWorker.Release();
        }
    }

    internal static int CalculateFramesPerPeak(double durationSeconds)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0) return 256;
        var frames = durationSeconds * WaveformSampleRate;
        var required = Math.Ceiling(frames / MaximumBaseWaveformPeaks);
        return (int)Math.Clamp(required, 256, 1_000_000);
    }

    private static string Invariant(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    private static MediaCacheKey Key(
        MediaSource source,
        MediaArtifactKind kind,
        int level,
        long segment,
        int formatVersion)
    {
        var fingerprint = !string.IsNullOrWhiteSpace(source.VerifiedFingerprint)
            ? source.VerifiedFingerprint
            : !string.IsNullOrWhiteSpace(source.FastFingerprint)
                ? source.FastFingerprint
                : !string.IsNullOrWhiteSpace(source.Fingerprint)
                    ? source.Fingerprint
                    : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                        $"{source.Path}|{source.FileSize}|{source.LastWriteUtcTicks}")));
        return new MediaCacheKey(source.Id, fingerprint, kind, level, segment, formatVersion);
    }
    private static void TryKill(System.Diagnostics.Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0) return;
        _shutdown.Cancel();
        var pending = _waveformJobs.Values
            .Where(item => item.IsValueCreated)
            .Select(item => item.Value)
            .ToArray();
        if (pending.Length > 0)
        {
            try { await Task.WhenAll(pending).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch { }
        }
        _thumbnailWorkers.Dispose();
        _waveformWorker.Dispose();
        _shutdown.Dispose();
        if (_ownsArtifacts) await _artifacts.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed record TimelineMediaArtifacts(
    WaveformPyramid Waveform);
