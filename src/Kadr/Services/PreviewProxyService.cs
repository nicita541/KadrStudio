using KadrStudio.Models;

namespace KadrStudio.Services;

public sealed class PreviewProxyService(FfmpegLocator locator, ProcessRunner processRunner)
{
    private readonly string _cacheDirectory = ThumbnailService.CreateCacheDirectory("Proxies");

    public async Task<string> EnsureProxyAsync(
        MediaAsset asset,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (asset.Kind == MediaKind.Image)
        {
            return asset.Path;
        }

        locator.EnsureAvailable();
        var extension = asset.Kind == MediaKind.Audio ? ".m4a" : ".mp4";
        var outputPath = Path.Combine(_cacheDirectory, ThumbnailService.BuildCacheKey(asset.Path) + extension);
        if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 1024)
        {
            asset.PreviewSourcePath = outputPath;
            return outputPath;
        }

        IReadOnlyList<string> arguments = asset.Kind == MediaKind.Audio
            ? [
                "-hide_banner", "-y", "-i", asset.Path,
                "-vn", "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2",
                "-movflags", "+faststart", outputPath
            ]
            : [
                "-hide_banner", "-y", "-i", asset.Path,
                "-map", "0:v:0", "-map", "0:a:0?",
                "-vf", "scale=1280:720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2:black,setsar=1",
                "-c:v", "libx264", "-preset", "ultrafast", "-crf", "26",
                "-c:a", "aac", "-b:a", "160k",
                "-movflags", "+faststart",
                outputPath
            ];

        var result = await processRunner.RunAsync(
            locator.FfmpegPath,
            arguments,
            line =>
            {
                if (TryParseTime(line, out var seconds) && asset.Duration > 0)
                {
                    progress?.Report(Math.Clamp(seconds / asset.Duration, 0, 1));
                }
            },
            cancellationToken);

        if (result.ExitCode != 0 || !File.Exists(outputPath))
        {
            throw new InvalidOperationException($"Не удалось подготовить файл для предпросмотра.\n{LastMeaningfulLine(result.StandardError)}");
        }

        asset.PreviewSourcePath = outputPath;
        return outputPath;
    }

    internal static bool TryParseTime(string line, out double seconds)
    {
        seconds = 0;
        var marker = "time=";
        var index = line.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return false;
        }

        var value = line[(index + marker.Length)..].TrimStart();
        var end = value.IndexOf(' ');
        if (end >= 0)
        {
            value = value[..end];
        }

        return TimeSpan.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var time) &&
               (seconds = time.TotalSeconds) >= 0;
    }

    internal static string LastMeaningfulLine(string text)
        => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault()
           ?? "Неизвестная ошибка FFmpeg.";
}
