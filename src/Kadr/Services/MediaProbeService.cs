using System.Globalization;
using System.Text.Json;
using System.Collections.Immutable;
using KadrStudio.Application.Media;
using KadrStudio.Core.Domain;
using KadrStudio.Infrastructure.Media;
using KadrStudio.Models;
using CoreMediaKind = KadrStudio.Core.Domain.MediaKind;
using UiMediaKind = KadrStudio.Models.MediaKind;

namespace KadrStudio.Services;

public sealed class MediaProbeService(
    FfmpegLocator locator,
    ProcessRunner processRunner,
    IMediaFingerprintService? fingerprintService = null) : IMediaProbe
{
    private readonly IMediaFingerprintService _fingerprintService = fingerprintService ?? new FileMediaFingerprintService();
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tif", ".tiff"
    };

    public async Task<MediaAsset> ProbeAsync(string filePath, CancellationToken cancellationToken = default)
        => ToAsset(await ProbeAsync(filePath, verifyContent: false, cancellationToken).ConfigureAwait(false));

    public async Task<MediaProbeResult> ProbeAsync(
        string filePath,
        bool verifyContent,
        CancellationToken cancellationToken = default)
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
        var descriptors = streams
            .Where(stream => IsVideoStream(stream) || IsAudioStream(stream))
            .Select(ToDescriptor)
            .ToImmutableArray();
        var exactFrameRate = ReadExactFrameRate(videoStream, "avg_frame_rate") ??
                             ReadExactFrameRate(videoStream, "r_frame_rate");
        var averageRate = ReadExactFrameRate(videoStream, "avg_frame_rate");
        var realRate = ReadExactFrameRate(videoStream, "r_frame_rate");
        var isVfr = ReadString(videoStream, "avg_frame_rate") != ReadString(videoStream, "r_frame_rate") &&
                    averageRate.HasValue && realRate.HasValue;
        var fingerprint = verifyContent
            ? await _fingerprintService.ComputeVerifiedAsync(filePath, cancellationToken).ConfigureAwait(false)
            : await _fingerprintService.ComputeFastAsync(filePath, cancellationToken).ConfigureAwait(false);
        return new MediaProbeResult(
            Path.GetFullPath(filePath),
            isImage ? CoreMediaKind.Image : hasVideo ? CoreMediaKind.Video : CoreMediaKind.Audio,
            TimelineTime.FromSeconds(isImage ? 5 : Math.Max(0.1, duration)),
            descriptors,
            fingerprint,
            ReadInt(videoStream, "width"),
            ReadInt(videoStream, "height"),
            exactFrameRate,
            isVfr);
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
    {
        if (element.ValueKind == JsonValueKind.Undefined ||
            !element.TryGetProperty(propertyName, out var property)) return 0;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number)) return number;
        return property.ValueKind == JsonValueKind.String &&
               int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var text)
            ? text
            : 0;
    }

    private static string ReadString(JsonElement element, string propertyName)
        => element.ValueKind != JsonValueKind.Undefined && element.TryGetProperty(propertyName, out var property)
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static FrameRate? ReadExactFrameRate(JsonElement stream, string propertyName)
    {
        var ratio = ReadString(stream, propertyName);

        var parts = ratio.Split('/');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var numerator) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var denominator) &&
            numerator > 0 &&
            denominator > 0)
        {
            return new FrameRate(numerator, denominator);
        }

        return null;
    }

    private static MediaStreamDescriptor ToDescriptor(JsonElement stream)
    {
        var isVideo = IsVideoStream(stream);
        return new MediaStreamDescriptor(
            ReadInt(stream, "index"),
            isVideo ? MediaStreamKind.Video : MediaStreamKind.Audio,
            ReadString(stream, "codec_name"),
            isVideo ? ReadString(stream, "pix_fmt") : ReadString(stream, "sample_fmt"),
            ReadInt(stream, "width"), ReadInt(stream, "height"),
            ReadInt(stream, "sample_rate"), ReadInt(stream, "channels"),
            isVideo ? ReadExactFrameRate(stream, "avg_frame_rate") ?? ReadExactFrameRate(stream, "r_frame_rate") : null,
            isVideo && ReadString(stream, "avg_frame_rate") != ReadString(stream, "r_frame_rate"));
    }

    private static MediaAsset ToAsset(MediaProbeResult result)
    {
        var video = result.Streams.FirstOrDefault(item => item.Kind == MediaStreamKind.Video);
        var audio = result.Streams.FirstOrDefault(item => item.Kind == MediaStreamKind.Audio);
        return new MediaAsset
        {
            Name = Path.GetFileName(result.Path),
            Path = result.Path,
            Kind = (UiMediaKind)(int)result.Kind,
            Duration = result.Duration.TotalSeconds,
            HasAudio = audio is not null,
            Width = result.Width,
            Height = result.Height,
            FrameRate = result.FrameRate?.FramesPerSecond ?? 0,
            VideoCodec = video?.Codec ?? string.Empty,
            AudioCodec = audio?.Codec ?? string.Empty,
            FileSizeBytes = result.Fingerprint.Length,
            ProbeResult = result
        };
    }

    private static string LastMeaningfulLine(string text)
        => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "Неизвестная ошибка FFprobe.";
}
