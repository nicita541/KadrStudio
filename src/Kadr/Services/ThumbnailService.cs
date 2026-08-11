using System.Security.Cryptography;
using System.Text;
using KadrStudio.Models;

namespace KadrStudio.Services;

public sealed class ThumbnailService(FfmpegLocator locator, ProcessRunner processRunner)
{
    private readonly string _cacheDirectory = CreateCacheDirectory("Thumbnails");

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

        locator.EnsureAvailable();
        var outputPath = Path.Combine(_cacheDirectory, BuildCacheKey(asset.Path) + ".jpg");
        if (File.Exists(outputPath))
        {
            return outputPath;
        }

        var seek = Math.Min(5, Math.Max(0, asset.Duration * 0.12));
        var result = await processRunner.RunAsync(
            locator.FfmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-ss", FormattableString.Invariant($"{seek:0.###}"),
                "-i", asset.Path,
                "-frames:v", "1",
                "-vf", "scale=360:-2:force_original_aspect_ratio=decrease",
                "-q:v", "3",
                outputPath
            ],
            cancellationToken: cancellationToken);

        return result.ExitCode == 0 && File.Exists(outputPath) ? outputPath : null;
    }

    internal static string BuildCacheKey(string path)
    {
        var info = new FileInfo(path);
        var value = $"{Path.GetFullPath(path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    internal static string CreateCacheDirectory(string childName)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kadr Studio",
            "Cache",
            childName);
        Directory.CreateDirectory(path);
        return path;
    }
}

