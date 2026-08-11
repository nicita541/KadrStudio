using System.Globalization;
using System.Text.Json;
using KadrStudio.Models;

namespace KadrStudio.Services;

public sealed class MediaProbeService(FfmpegLocator locator, ProcessRunner processRunner)
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tif", ".tiff"
    };

    public async Task<MediaAsset> ProbeAsync(string filePath, CancellationToken cancellationToken = default)
    {
        locator.EnsureAvailable();
        var result = await processRunner.RunAsync(
            locator.FfprobePath,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", filePath],
            cancellationToken: cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidDataException($"Не удалось прочитать файл {Path.GetFileName(filePath)}.\n{LastMeaningfulLine(result.StandardError)}");
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        var streams = root.TryGetProperty("streams", out var streamsElement)
            ? streamsElement.EnumerateArray().ToArray()
            : Array.Empty<JsonElement>();

        var videoStream = streams.FirstOrDefault(IsVideoStream);
        var audioStream = streams.FirstOrDefault(IsAudioStream);
        var extension = Path.GetExtension(filePath);
        var isImage = ImageExtensions.Contains(extension);
        var hasVideo = videoStream.ValueKind != JsonValueKind.Undefined;
        var hasAudio = audioStream.ValueKind != JsonValueKind.Undefined;

        if (!isImage && !hasVideo && !hasAudio)
        {
            throw new InvalidDataException($"Формат файла {Path.GetFileName(filePath)} не поддерживается.");
        }

        var duration = ReadDuration(root, videoStream, audioStream);
        var asset = new MediaAsset
        {
            Name = Path.GetFileName(filePath),
            Path = Path.GetFullPath(filePath),
            PreviewSourcePath = Path.GetFullPath(filePath),
            Kind = isImage ? MediaKind.Image : hasVideo ? MediaKind.Video : MediaKind.Audio,
            Duration = isImage ? 5 : Math.Max(0.1, duration),
            HasAudio = hasAudio,
            Width = ReadInt(videoStream, "width"),
            Height = ReadInt(videoStream, "height"),
            FrameRate = ReadFrameRate(videoStream),
            VideoCodec = ReadString(videoStream, "codec_name"),
            AudioCodec = ReadString(audioStream, "codec_name"),
            FileSizeBytes = new FileInfo(filePath).Length
        };

        return asset;
    }

    private static bool IsVideoStream(JsonElement stream)
        => ReadString(stream, "codec_type").Equals("video", StringComparison.OrdinalIgnoreCase);

    private static bool IsAudioStream(JsonElement stream)
        => ReadString(stream, "codec_type").Equals("audio", StringComparison.OrdinalIgnoreCase);

    private static double ReadDuration(JsonElement root, JsonElement videoStream, JsonElement audioStream)
    {
        if (root.TryGetProperty("format", out var format) && TryReadDouble(format, "duration", out var formatDuration))
        {
            return formatDuration;
        }

        if (TryReadDouble(videoStream, "duration", out var videoDuration))
        {
            return videoDuration;
        }

        return TryReadDouble(audioStream, "duration", out var audioDuration) ? audioDuration : 0;
    }

    private static bool TryReadDouble(JsonElement element, string propertyName, out double value)
    {
        value = 0;
        if (element.ValueKind == JsonValueKind.Undefined || !element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind == JsonValueKind.Number
            ? property.TryGetDouble(out value)
            : double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static int ReadInt(JsonElement element, string propertyName)
        => element.ValueKind != JsonValueKind.Undefined &&
           element.TryGetProperty(propertyName, out var property) &&
           property.TryGetInt32(out var value)
            ? value
            : 0;

    private static string ReadString(JsonElement element, string propertyName)
        => element.ValueKind != JsonValueKind.Undefined && element.TryGetProperty(propertyName, out var property)
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static double ReadFrameRate(JsonElement stream)
    {
        var ratio = ReadString(stream, "avg_frame_rate");
        if (string.IsNullOrWhiteSpace(ratio) || ratio == "0/0")
        {
            ratio = ReadString(stream, "r_frame_rate");
        }

        var parts = ratio.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) &&
            denominator > 0)
        {
            return numerator / denominator;
        }

        return 0;
    }

    private static string LastMeaningfulLine(string text)
        => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "Неизвестная ошибка FFprobe.";
}
