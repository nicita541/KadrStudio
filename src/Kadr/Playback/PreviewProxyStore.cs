using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using KadrStudio.Application.Caching;
using KadrStudio.Application.Rendering;
using KadrStudio.Infrastructure.Caching;
using KadrStudio.Models;
using KadrStudio.Services;

namespace KadrStudio.Playback;

public sealed class PreviewProxyStore : IAsyncDisposable
{
    private readonly FfmpegLocator _locator;
    private readonly IArtifactStore _artifacts;
    private readonly bool _ownsArtifacts;
    private readonly ProcessRunner _runner = new();
    private readonly ConcurrentDictionary<Guid, string> _validated = [];
    private readonly ConcurrentDictionary<Guid, Task> _jobs = [];
    private readonly ConcurrentBag<Task> _allJobs = [];
    private readonly ConcurrentBag<CancellationTokenSource> _retiredGenerations = [];
    private readonly SemaphoreSlim _encoderGate = new(1, 1);
    private CancellationTokenSource _generation = new();
    private Guid _projectId;
    private bool _disposed;

    public PreviewProxyStore(FfmpegLocator locator, IArtifactStore? artifacts = null)
    {
        _locator = locator;
        _ownsArtifacts = artifacts is null;
        _artifacts = artifacts ?? new DiskMediaArtifactCache(ThumbnailService.DefaultArtifactRoot());
    }

    public event EventHandler<Guid>? ProxyReady;

    public void Configure(EditorProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (_projectId == project.Id) return;
        _generation.Cancel();
        _retiredGenerations.Add(_generation);
        _generation = new CancellationTokenSource();
        _validated.Clear();
        _jobs.Clear();
        _projectId = project.Id;
    }

    public RenderPlan UseAvailable(RenderPlan plan)
    {
        var layers = plan.VisualLayers.Select(layer =>
            _validated.TryGetValue(layer.SourceId, out var proxy) && File.Exists(proxy)
                ? layer with { SourcePath = proxy }
                : layer).ToImmutableArray();
        return plan with { VisualLayers = layers };
    }

    public void Queue(EditorProject project)
    {
        ThrowIfDisposed();
        Configure(project);
        foreach (var asset in project.Media.Where(asset =>
                     asset.Kind == MediaKind.Video && !asset.IsMissing && File.Exists(asset.Path)))
        {
            _jobs.GetOrAdd(asset.Id, _ =>
            {
                var job = BuildOrValidateAsync(asset, project.FrameRateValue, _projectId, _generation.Token);
                _allJobs.Add(job);
                return job;
            });
        }
    }

    public async Task PrepareAsync(EditorProject project, CancellationToken cancellationToken = default)
    {
        Queue(project);
        var jobs = _jobs.Values.ToArray();
        if (jobs.Length > 0) await Task.WhenAll(jobs).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _generation.Cancel();
        var jobs = _allJobs.ToArray();
        if (jobs.Length > 0)
            try { await Task.WhenAll(jobs).ConfigureAwait(false); } catch { }
        _generation.Dispose();
        foreach (var generation in _retiredGenerations) generation.Dispose();
        _encoderGate.Dispose();
        if (_ownsArtifacts) await _artifacts.DisposeAsync().ConfigureAwait(false);
    }

    private async Task BuildOrValidateAsync(
        MediaAsset asset,
        KadrStudio.Core.Domain.FrameRate frameRate,
        Guid projectId,
        CancellationToken token)
    {
        var lockTaken = false;
        try
        {
            await _encoderGate.WaitAsync(token).ConfigureAwait(false);
            lockTaken = true;
            var key = ThumbnailService.Key(asset, MediaArtifactKind.ProxyVideo, 0,
                ((long)frameRate.Numerator << 32) | (uint)frameRate.Denominator, 2);
            var path = await _artifacts.TryGetPayloadPathAsync(key, ".mp4", token).ConfigureAwait(false);
            if (path is null || !await ProbeVideoAsync(path, token).ConfigureAwait(false))
            {
                var temporary = Path.Combine(Path.GetTempPath(), "KadrStudio", "artifacts", $"{Guid.NewGuid():N}.mp4");
                Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
                try
                {
                    var result = await _runner.RunAsync(_locator.FfmpegPath,
                    [
                        "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
                        "-i", asset.Path, "-map", "0:v:0", "-an",
                        "-vf", "scale=960:540:force_original_aspect_ratio=decrease,pad=960:540:(ow-iw)/2:(oh-ih)/2:black,setsar=1",
                        "-r", $"{frameRate.Numerator}/{frameRate.Denominator}",
                        "-fps_mode", "cfr", "-c:v", "libx264", "-preset", "veryfast", "-crf", "23",
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
            if (_projectId == projectId)
            {
                _validated[asset.Id] = path;
                ProxyReady?.Invoke(this, asset.Id);
            }
        }
        catch (OperationCanceledException) { }
        catch
        {
            // The original remains the reliable fallback while a proxy is unavailable.
        }
        finally
        {
            if (lockTaken) _encoderGate.Release();
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
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
