using System.Buffers;
using System.Runtime.InteropServices;
using KadrStudio.Application.Caching;
using KadrStudio.Infrastructure.Caching;
using KadrStudio.Models;

namespace KadrStudio.Services;

public sealed class TimelineMediaCacheService : IAsyncDisposable
{
    private const double FrameIntervalSeconds = 15;
    private readonly FfmpegLocator _locator;
    private readonly ProcessRunner _processRunner;
    private readonly IArtifactStore _artifacts;
    private readonly bool _ownsArtifacts;

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

    public void ConfigureProject(EditorProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        // The unified store owns location and budget; project configuration is
        // retained as an API boundary until callers move to workspace services.
    }

    public async Task PrepareAsync(MediaAsset asset, CancellationToken cancellationToken = default)
    {
        if (asset.Kind == MediaKind.Image)
        {
            asset.TimelineFramePaths = [asset.Path];
            return;
        }

        _locator.EnsureAvailable();
        var framesTask = asset.Kind == MediaKind.Video
            ? PrepareFramesAsync(asset, cancellationToken)
            : Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        var waveformTask = asset.HasAudio
            ? PrepareWaveformAsync(asset, cancellationToken)
            : Task.FromResult(WaveformPyramid.Empty);
        await Task.WhenAll(framesTask, waveformTask);
        asset.TimelineFramePaths = await framesTask;
        asset.Waveform = await waveformTask;
    }

    private async Task<IReadOnlyList<string>> PrepareFramesAsync(
        MediaAsset asset, CancellationToken cancellationToken)
    {
        var frameCount = Math.Clamp((int)Math.Ceiling(asset.Duration / FrameIntervalSeconds), 12, 160);
        var paths = new string?[frameCount];
        await Parallel.ForEachAsync(Enumerable.Range(0, frameCount),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                var key = ThumbnailService.Key(asset, MediaArtifactKind.TimelineThumbnail, 0, index, 2);
                var cached = await _artifacts.TryGetPayloadPathAsync(key, ".jpg", token);
                if (cached is not null)
                {
                    paths[index] = cached;
                    return;
                }
                var position = Math.Min(Math.Max(0, asset.Duration - 0.01), index * FrameIntervalSeconds + FrameIntervalSeconds / 2);
                var temporary = Path.Combine(Path.GetTempPath(), "KadrStudio", "artifacts", $"{Guid.NewGuid():N}.jpg");
                Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
                try
                {
                    var result = await _processRunner.RunAsync(_locator.FfmpegPath,
                        ["-hide_banner", "-loglevel", "error", "-y", "-hwaccel", "auto", "-ss", Invariant(position),
                         "-i", asset.Path, "-frames:v", "1", "-vf", "scale=192:108:force_original_aspect_ratio=increase,crop=192:108",
                         "-q:v", "5", temporary], cancellationToken: token);
                    if (result.ExitCode == 0 && File.Exists(temporary))
                        paths[index] = await _artifacts.PutFileAsync(key, temporary, ".jpg", token);
                }
                finally { TryDelete(temporary); }
            });
        return paths.Where(path => path is not null).Select(path => path!).ToArray();
    }

    private async Task<WaveformPyramid> PrepareWaveformAsync(
        MediaAsset asset, CancellationToken cancellationToken)
    {
        var key = ThumbnailService.Key(asset, MediaArtifactKind.Waveform, 0, 0, 5);
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
            "-hide_banner", "-loglevel", "error", "-i", asset.Path, "-vn", "-map", "0:a:0",
            "-ac", "2", "-ar", "48000", "-c:a", "pcm_f32le", "-f", "f32le", "pipe:1"
        }) startInfo.ArgumentList.Add(argument);
        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("FFmpeg waveform decoder did not start.");
        using var registration = cancellationToken.Register(() => TryKill(process));
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var builder = new WaveformPyramidBuilder();
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

    private static string Invariant(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    private static void TryKill(System.Diagnostics.Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    public ValueTask DisposeAsync() => _ownsArtifacts ? _artifacts.DisposeAsync() : ValueTask.CompletedTask;
}
