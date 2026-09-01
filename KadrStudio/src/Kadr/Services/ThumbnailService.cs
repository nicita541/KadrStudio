using KadrStudio.Application.Caching;
using KadrStudio.Infrastructure.Caching;
using KadrStudio.Models;
using System.Security.Cryptography;
using System.Text;

namespace KadrStudio.Services;

public sealed class ThumbnailService : IAsyncDisposable
{
    private readonly FfmpegLocator _locator;
    private readonly ProcessRunner _processRunner;
    private readonly IArtifactStore _artifacts;
    private readonly bool _ownsArtifacts;

    public ThumbnailService(FfmpegLocator locator, ProcessRunner processRunner, IArtifactStore? artifacts = null)
    {
        _locator = locator;
        _processRunner = processRunner;
        _ownsArtifacts = artifacts is null;
        _artifacts = artifacts ?? new DiskMediaArtifactCache(DefaultArtifactRoot());
    }

    public async Task<string?> CreateAsync(MediaAsset asset, CancellationToken cancellationToken = default)
    {
        if (asset.Kind == MediaKind.Image)
        {
            return asset.Path;
        }

        if (asset.Kind == MediaKind.Audio)
        {
            return null;
        }

        _locator.EnsureAvailable();
        var key = Key(asset, MediaArtifactKind.Thumbnail, 0, 0, 2);
        var cached = await _artifacts.TryGetPayloadPathAsync(key, ".jpg", cancellationToken);
        if (cached is not null) return cached;

        var seek = Math.Min(5, Math.Max(0, asset.Duration * 0.12));
        var temporary = Path.Combine(KadrLocalDataPaths.TempRoot, "artifacts", $"{Guid.NewGuid():N}.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
        try
        {
            var result = await _processRunner.RunAsync(
            _locator.FfmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-ss", FormattableString.Invariant($"{seek:0.###}"),
                "-i", asset.Path,
                "-frames:v", "1",
                "-vf", "scale=360:-2:force_original_aspect_ratio=decrease",
                "-q:v", "3",
                temporary
            ],
            cancellationToken: cancellationToken);
            return result.ExitCode == 0 && File.Exists(temporary)
                ? await _artifacts.PutFileAsync(key, temporary, ".jpg", cancellationToken)
                : null;
        }
        finally { TryDelete(temporary); }
    }

    internal static string BuildCacheKey(string path)
    {
        var info = new FileInfo(path);
        var value = $"{Path.GetFullPath(path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    internal static string CreateCacheDirectory(string childName)
    {
        var path = Path.Combine(KadrLocalDataPaths.CacheRoot, childName);
        Directory.CreateDirectory(path);
        return path;
    }

    internal static MediaCacheKey Key(
        MediaAsset asset,
        MediaArtifactKind kind,
        int level,
        long segment,
        int formatVersion)
    {
        var fingerprint = asset.ProbeResult?.Fingerprint.FastHash;
        if (string.IsNullOrWhiteSpace(fingerprint)) fingerprint = BuildCacheKey(asset.Path);
        return new MediaCacheKey(asset.Id, fingerprint, kind, level, segment, formatVersion);
    }

    internal static string DefaultArtifactRoot() => KadrLocalDataPaths.ArtifactsRoot;

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    public ValueTask DisposeAsync() => _ownsArtifacts ? _artifacts.DisposeAsync() : ValueTask.CompletedTask;
}
