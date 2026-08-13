using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KadrStudio.Application.Rendering;
using KadrStudio.Models;
using KadrStudio.Services;

namespace KadrStudio.Playback;

public sealed class PreviewProxyStore : IAsyncDisposable
{
    private const long DefaultDiskLimit = 4L * 1024 * 1024 * 1024;
    private readonly FfmpegLocator _locator;
    private readonly ProcessRunner _runner = new();
    private readonly ConcurrentDictionary<Guid, string> _validated = [];
    private readonly ConcurrentDictionary<Guid, Task> _jobs = [];
    private readonly ConcurrentBag<Task> _allJobs = [];
    private readonly ConcurrentBag<CancellationTokenSource> _retiredGenerations = [];
    private readonly SemaphoreSlim _encoderGate = new(1, 1);
    private CancellationTokenSource _generation = new();
    private string _root = string.Empty;
    private Guid _projectId;
    private bool _disposed;

    public PreviewProxyStore(FfmpegLocator locator) => _locator = locator;

    public event EventHandler<Guid>? ProxyReady;

    public void Configure(EditorProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var desiredRoot = Path.GetFullPath(ResolveRoot(project));
        if (_projectId == project.Id && string.Equals(_root, desiredRoot, StringComparison.OrdinalIgnoreCase)) return;
        _generation.Cancel();
        _retiredGenerations.Add(_generation);
        _generation = new CancellationTokenSource();
        _validated.Clear();
        _jobs.Clear();
        _projectId = project.Id;
        _root = desiredRoot;
        Directory.CreateDirectory(_root);
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
                var job = BuildOrValidateAsync(asset, project.FrameRateValue, _projectId, _root, _generation.Token);
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
    }

    private async Task BuildOrValidateAsync(MediaAsset asset, KadrStudio.Core.Domain.FrameRate frameRate, Guid projectId, string root, CancellationToken token)
    {
        var lockTaken = false;
        try
        {
            await _encoderGate.WaitAsync(token).ConfigureAwait(false);
            lockTaken = true;
            var fingerprint = Fingerprint(asset);
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{fingerprint}|{frameRate}|proxy-v1")));
            var path = ResolveInsideRoot(root, Path.Combine(root, $"{asset.Id:N}-{hash}.mp4"));
            var metadataPath = path + ".json";
            if (!await IsValidAsync(path, metadataPath, fingerprint, token).ConfigureAwait(false))
            {
                TryDelete(path);
                TryDelete(metadataPath);
                var temporary = path + $".{Guid.NewGuid():N}.tmp.mp4";
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
                    File.Move(temporary, path, overwrite: true);
                    var metadata = new ProxyMetadata(1, fingerprint, new FileInfo(path).Length);
                    await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(metadata), token).ConfigureAwait(false);
                }
                finally { TryDelete(temporary); }
            }
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            if (_projectId == projectId)
            {
                _validated[asset.Id] = path;
                await TrimAsync(root, DefaultDiskLimit, token).ConfigureAwait(false);
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

    private async Task<bool> IsValidAsync(string path, string metadataPath, string fingerprint, CancellationToken token)
    {
        if (!File.Exists(path) || !File.Exists(metadataPath) || new FileInfo(path).Length < 1024) return false;
        try
        {
            var metadata = JsonSerializer.Deserialize<ProxyMetadata>(await File.ReadAllTextAsync(metadataPath, token).ConfigureAwait(false));
            return metadata is { Version: 1 } && metadata.SourceFingerprint == fingerprint &&
                   metadata.Length == new FileInfo(path).Length && await ProbeVideoAsync(path, token).ConfigureAwait(false);
        }
        catch { return false; }
    }

    private async Task<bool> ProbeVideoAsync(string path, CancellationToken token)
    {
        var result = await _runner.RunAsync(_locator.FfprobePath,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_type,width,height", "-of", "csv=p=0", path],
            cancellationToken: token).ConfigureAwait(false);
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput) && result.StandardOutput.Contains("video", StringComparison.OrdinalIgnoreCase);
    }

    private async Task TrimAsync(string root, long limit, CancellationToken token)
    {
        var files = Directory.EnumerateFiles(root, "*.mp4").Select(path => new FileInfo(path))
            .OrderBy(file => file.LastAccessTimeUtc).ToArray();
        var total = files.Sum(file => file.Length);
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            if (total <= limit) break;
            total -= file.Length;
            TryDelete(file.FullName);
            TryDelete(file.FullName + ".json");
            foreach (var entry in _validated.Where(entry =>
                         string.Equals(entry.Value, file.FullName, StringComparison.OrdinalIgnoreCase)).ToArray())
                _validated.TryRemove(entry.Key, out _);
            await Task.Yield();
        }
    }

    private static string ResolveRoot(EditorProject project)
    {
        if (!string.IsNullOrWhiteSpace(project.FilePath))
            return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(project.FilePath))!, ".kadr-cache", "proxies");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kadr Studio", "Cache", "Projects", project.Id.ToString("N"), "proxies");
    }

    private static string ResolveInsideRoot(string configuredRoot, string path)
    {
        var root = Path.GetFullPath(configuredRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Proxy path escaped the configured cache root.");
        return full;
    }

    private static string Fingerprint(MediaAsset asset)
    {
        long ticks;
        try { ticks = File.GetLastWriteTimeUtc(asset.Path).Ticks; } catch { ticks = 0; }
        return $"{asset.FileSizeBytes:x}-{ticks:x}-{asset.Width}x{asset.Height}-{asset.Duration:0.###}";
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    private sealed record ProxyMetadata(int Version, string SourceFingerprint, long Length);
}
