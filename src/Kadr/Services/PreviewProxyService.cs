using KadrStudio.Models;

namespace KadrStudio.Services;

public sealed class PreviewProxyService(FfmpegLocator locator, ProcessRunner processRunner)
{
    private readonly string _cacheDirectory = ThumbnailService.CreateCacheDirectory("Proxies");
    private readonly string _segmentDirectory = ThumbnailService.CreateCacheDirectory("PreviewSegments");

    public async Task<PreviewSegment> EnsureSegmentAsync(
        MediaAsset asset,
        double sourcePosition,
        CancellationToken cancellationToken = default)
    {
        if (asset.Kind == MediaKind.Image)
        {
            return new PreviewSegment(asset.Id, asset.Path, 0, asset.Duration);
        }

        locator.EnsureAvailable();
        var segmentStart = Math.Floor(Math.Clamp(sourcePosition, 0, Math.Max(0, asset.Duration)) / 15) * 15;
        var segmentDuration = Math.Max(0.1, Math.Min(20, Math.Max(0.1, asset.Duration - segmentStart)));
        var extension = asset.Kind == MediaKind.Audio ? ".m4a" : ".mp4";
        var outputPath = Path.Combine(_segmentDirectory,
            $"{ThumbnailService.BuildCacheKey(asset.Path)}-{segmentStart:000000}-v3{extension}");
        if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 1024)
        {
            return new PreviewSegment(asset.Id, outputPath, segmentStart, segmentDuration);
        }

        var temporaryPath = outputPath + $"-{Guid.NewGuid():N}.tmp{extension}";
        var useNvenc = asset.Kind == MediaKind.Video && await CanUseNvencAsync(cancellationToken);
        IReadOnlyList<string> arguments = asset.Kind == MediaKind.Audio
            ? [
                "-hide_banner", "-loglevel", "error", "-y", "-ss", Format(segmentStart), "-t", Format(segmentDuration),
                "-i", asset.Path, "-vn", "-c:a", "aac", "-b:a", "160k", "-ar", "48000", "-ac", "2",
                "-movflags", "+faststart", temporaryPath
            ]
            : BuildVideoSegmentArguments(asset.Path, temporaryPath, segmentStart, segmentDuration, useNvenc);

        try
        {
            var result = await processRunner.RunAsync(locator.FfmpegPath, arguments, cancellationToken: cancellationToken);
            if (result.ExitCode != 0 || !File.Exists(temporaryPath))
            {
                throw new InvalidOperationException($"Не удалось подготовить быстрый фрагмент предпросмотра.\n{LastMeaningfulLine(result.StandardError)}");
            }
            File.Move(temporaryPath, outputPath, overwrite: true);
            return new PreviewSegment(asset.Id, outputPath, segmentStart, segmentDuration);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch
            {
                // Временный файл будет очищен Windows позднее.
            }
        }
    }

    private static IReadOnlyList<string> BuildVideoSegmentArguments(
        string sourcePath,
        string outputPath,
        double segmentStart,
        double segmentDuration,
        bool useNvenc)
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y", "-hwaccel", "auto",
            "-ss", Format(segmentStart), "-t", Format(segmentDuration), "-i", sourcePath,
            "-map", "0:v:0", "-map", "0:a:0?",
            "-vf", "scale=640:360:force_original_aspect_ratio=decrease,pad=640:360:(ow-iw)/2:(oh-ih)/2:black,setsar=1,fps=15,format=yuv420p"
        };
        arguments.AddRange(useNvenc
            ? ["-c:v", "h264_nvenc", "-preset", "p1", "-cq", "31", "-g", "15"]
            : ["-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-crf", "31", "-g", "15"]);
        arguments.AddRange(["-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", outputPath]);
        return arguments;
    }

    private async Task<bool> CanUseNvencAsync(CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(locator.FfmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=black:size=64x64:duration=0.05",
                "-c:v", "h264_nvenc", "-f", "null", "-"
            ], cancellationToken: cancellationToken);
        return result.ExitCode == 0;
    }

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

    private static string Format(double value)
        => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

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

public sealed record PreviewSegment(Guid AssetId, string Path, double SourceStart, double Duration)
{
    public double SourceEnd => SourceStart + Duration;
    public bool Contains(Guid assetId, double sourcePosition)
        => AssetId == assetId && sourcePosition >= SourceStart && sourcePosition <= SourceEnd + 0.001;
}
