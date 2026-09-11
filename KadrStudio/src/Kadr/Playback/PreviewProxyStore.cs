using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using KadrStudio.Application.Caching;
using KadrStudio.Application.Rendering;
using KadrStudio.Infrastructure.Caching;
using KadrStudio.Core.Domain;
using KadrStudio.Services;

namespace KadrStudio.Playback;

public sealed class PreviewProxyStore : IAsyncDisposable
{
    private readonly FfmpegLocator _locator;
    private readonly IArtifactStore _artifacts;
    private readonly bool _ownsArtifacts;
    private readonly ProcessRunner _runner = new();
    private sealed record ValidatedProxy(string SourcePath, string ProxyPath);
    private readonly ConcurrentDictionary<Guid, ValidatedProxy> _validated = [];
    private readonly object _sync = new();
    private readonly ConcurrentDictionary<Guid, Task> _jobs = [];
    private readonly HashSet<Guid> _preparing = [];
    private int _failed;
    private readonly Dictionary<Task, CancellationTokenSource> _allJobs = [];
    private readonly List<(string Path, IDisposable Lease)> _artifactPins = [];
    private readonly HashSet<CancellationTokenSource> _retiredGenerations = [];
    private readonly SemaphoreSlim _encoderGate = new(1, 1);
    private CancellationTokenSource _generation = new();
    private string? _configurationIdentity;
    private bool _disposed;
    private const int ProxyEncoderThreads = 1;

    public PreviewProxyStore(FfmpegLocator locator, IArtifactStore? artifacts = null)
    {
        _locator = locator;
        _ownsArtifacts = artifacts is null;
        _artifacts = artifacts ?? ArtifactStoreFactory.Create(new ArtifactStoreOptions(ThumbnailService.DefaultArtifactRoot()));
    }

    public event EventHandler<Guid>? ProxyReady;
    public event EventHandler? StatusChanged;
    public string StatusText
    {
        get
        {
            lock (_sync) return _preparing.Count > 0 ? $"Proxy: подготовка ({_preparing.Count})"
                : _failed > 0 ? "Proxy недоступен: оригинал"
                : _validated.Count > 0 ? $"Proxy готов (до 1080p): {_validated.Count}" : "Оригинал";
        }
    }

    /// <summary>Call only after the host has acknowledged installing this plan.</summary>
    public void AcknowledgePlan(RenderPlan plan)
    {
        lock (_sync)
        {
            var retained = plan.VisualLayers.Select(layer => layer.SourcePath)
                .Concat(_validated.Values.Select(proxy => proxy.ProxyPath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (var index = _artifactPins.Count - 1; index >= 0; index--)
            {
                if (retained.Contains(_artifactPins[index].Path)) continue;
                _artifactPins[index].Lease.Dispose();
                _artifactPins.RemoveAt(index);
            }
        }
    }

    public void Configure(ProjectState project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var identity = string.Join('|', project.Id, project.FrameRate,
            string.Join(';', project.Sources.OrderBy(item => item.Key)
                .Select(item => item.Key + ":" + SourceIdentity(item.Value))));
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_configurationIdentity == identity) return;
            _generation.Cancel();
            _retiredGenerations.Add(_generation);
            ReleaseRetiredGenerations();
            _generation = new CancellationTokenSource();
            _validated.Clear();
            _jobs.Clear();
            _preparing.Clear();
            _failed = 0;
            _configurationIdentity = identity;
        }
    }

    public RenderPlan UseAvailable(RenderPlan plan)
    {
        lock (_sync)
        {
            var layers = plan.VisualLayers.Select(layer =>
                _validated.TryGetValue(layer.SourceId, out var proxy) &&
                string.Equals(layer.SourcePath, proxy.SourcePath, StringComparison.OrdinalIgnoreCase) && File.Exists(proxy.ProxyPath)
                    ? layer with { SourcePath = proxy.ProxyPath }
                    : layer).ToImmutableArray();
            if (layers.SequenceEqual(plan.VisualLayers)) return plan;
            var proxyIdentity = string.Join('|', layers.Select(layer => layer.SourcePath));
            var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(proxyIdentity)));
            return plan with
            {
                VisualLayers = layers,
                SourceDecodeSignature = plan.SourceDecodeSignature + ":proxy:" + signature,
                VideoContentSignature = plan.VideoContentSignature + ":proxy:" + signature
            };
        }
    }

    public void Queue(ProjectState project, bool highResolutionOnly = false)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            Configure(project);
            var timelineVideoSources = project.MediaClips
                .Where(clip => project.FindTrack(clip.TrackId)?.Kind == TrackKind.Visual)
                .Select(clip => clip.SourceId)
                .ToHashSet();
            foreach (var source in project.Sources.Values.Where(source =>
                         timelineVideoSources.Contains(source.Id) &&
                         source.Kind == MediaKind.Video &&
                         (!highResolutionOnly || source.Width > 1920 || source.Height > 1080) &&
                         source.OnlineState == MediaOnlineState.Online && File.Exists(source.Path)))
            {
                _jobs.GetOrAdd(source.Id, sourceId =>
                {
                    _preparing.Add(source.Id);
                    var job = BuildOrValidateAsync(source, _generation);
                    _allJobs.Add(job, _generation);
                    _ = ObserveCompletionAsync(job);
                    return job;
                });
            }
        }
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task PrepareAsync(ProjectState project, CancellationToken cancellationToken = default,
        bool highResolutionOnly = false)
    {
        Queue(project, highResolutionOnly);
        var jobs = _jobs.Values.ToArray();
        if (jobs.Length > 0) await Task.WhenAll(jobs).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Task[] jobs;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _generation.Cancel();
            _validated.Clear();
            jobs = _allJobs.Keys.ToArray();
        }
        if (jobs.Length > 0)
            try { await Task.WhenAll(jobs).ConfigureAwait(false); } catch { }
        lock (_sync)
        {
            foreach (var pin in _artifactPins) pin.Lease.Dispose();
            _artifactPins.Clear();
            _generation.Dispose();
            foreach (var generation in _retiredGenerations) generation.Dispose();
            _retiredGenerations.Clear();
            _allJobs.Clear();
        }
        _encoderGate.Dispose();
        if (_ownsArtifacts) await _artifacts.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ObserveCompletionAsync(Task job)
    {
        try { await job.ConfigureAwait(false); }
        catch (Exception error) { Trace.TraceWarning("Preview proxy completion failed: {0}", error.GetType().Name); }
        finally
        {
            lock (_sync)
            {
                _allJobs.Remove(job);
                ReleaseRetiredGenerations();
            }
        }
    }

    // Called under _sync, only after all users of a retired token have finished.
    private void ReleaseRetiredGenerations()
    {
        foreach (var generation in _retiredGenerations.ToArray())
        {
            if (_allJobs.Values.Contains(generation)) continue;
            _retiredGenerations.Remove(generation);
            generation.Dispose();
        }
    }

    private async Task BuildOrValidateAsync(
        MediaSource source,
        CancellationTokenSource generation)
    {
        var token = generation.Token;
        var fingerprint = SourceIdentity(source);
        var lockTaken = false;
        IDisposable? pin = null;
        try
        {
            // Let the drop/edit transaction paint first. Proxy generation is an
            // optimization and must not compete with the interaction itself.
            await Task.Delay(TimeSpan.FromMilliseconds(750), token).ConfigureAwait(false);
            await _encoderGate.WaitAsync(token).ConfigureAwait(false);
            lockTaken = true;
            var key = new MediaCacheKey(source.Id, fingerprint, MediaArtifactKind.ProxyVideo, 0,
                0, 4);
            pin = await _artifacts.PinAsync(key, ".mp4", token).ConfigureAwait(false);
            var path = await _artifacts.TryGetPayloadPathAsync(key, ".mp4", token).ConfigureAwait(false);
            if (path is null || !await ProbeVideoAsync(path, token).ConfigureAwait(false))
            {
                var temporary = Path.Combine(KadrLocalDataPaths.TempRoot, "artifacts", $"{Guid.NewGuid():N}.mp4");
                Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
                try
                {
                    var result = await _runner.RunAsync(_locator.FfmpegPath,
                    [
                        "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
                        "-i", source.Path, "-map", "0:v:0", "-an",
                        "-vf", "scale=w='min(1920,iw)':h='min(1080,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2,setsar=1",
                        "-fps_mode", "passthrough", "-c:v", "libx264", "-preset", "veryfast",
                        "-threads", ProxyEncoderThreads.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "-crf", "23",
                        "-g", "12", "-keyint_min", "12", "-sc_threshold", "0", "-pix_fmt", "yuv420p",
                        "-movflags", "+faststart", temporary
                    ], cancellationToken: token).ConfigureAwait(false);
                    if (result.ExitCode != 0 || !await ProbeVideoAsync(temporary, token).ConfigureAwait(false))
                        throw new InvalidDataException($"FFmpeg proxy failed: {result.StandardError}");
                    path = await _artifacts.PutFileAsync(key, temporary, ".mp4", token).ConfigureAwait(false);
                }
                finally { TryDelete(temporary); }
            }
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            lock (_sync)
            {
                if (_disposed || generation != _generation || token.IsCancellationRequested ||
                    fingerprint != SourceIdentity(source)) return;
                _validated[source.Id] = new ValidatedProxy(source.Path, path);
                // Keep retired paths until the host acknowledges the replacement plan.
                _artifactPins.Add((path, pin));
                pin = null;
            }
            ProxyReady?.Invoke(this, source.Id);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            lock (_sync) if (generation == _generation) _failed++;
            Trace.TraceWarning("Preview proxy failed: {0}", error.GetType().Name);
        }
        finally
        {
            pin?.Dispose();
            if (lockTaken) _encoderGate.Release();
            lock (_sync) if (generation == _generation) _preparing.Remove(source.Id);
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<bool> ProbeVideoAsync(string path, CancellationToken token)
    {
        var result = await _runner.RunAsync(_locator.FfprobePath,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_type,width,height", "-of", "csv=p=0", path],
            cancellationToken: token).ConfigureAwait(false);
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput) && result.StandardOutput.Contains("video", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static string SourceIdentity(MediaSource source)
    {
        var file = new FileInfo(source.Path);
        var identity = string.Join('|', Path.GetFullPath(source.Path).ToUpperInvariant(),
            file.Exists ? file.Length : -1, file.Exists ? file.LastWriteTimeUtc.Ticks : -1,
            source.Fingerprint, source.FastFingerprint, source.VerifiedFingerprint,
            source.Kind, source.Width, source.Height, source.Duration.Ticks, source.FrameRate,
            source.IsVariableFrameRate, source.OnlineState);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
