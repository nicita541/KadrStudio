using System.Globalization;

namespace KadrStudio.Services;

public static class FfmpegOutput
{
    public static bool TryParseTime(string line, out double seconds)
    {
        seconds = 0;
        const string marker = "time=";
        var index = line.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return false;

        var value = line[(index + marker.Length)..].TrimStart();
        var end = value.IndexOf(' ');
        if (end >= 0) value = value[..end];
        return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var time) &&
               (seconds = time.TotalSeconds) >= 0;
    }

    public static string LastMeaningfulLine(string text)
        => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault()
           ?? "Неизвестная ошибка FFmpeg.";
}
