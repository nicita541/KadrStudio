using System.Globalization;
using System.Text.RegularExpressions;
using KadrStudio.Models;

namespace KadrStudio.Services;

public sealed partial class AutoSubtitleService(FfmpegLocator locator, ProcessRunner processRunner)
{
    public async Task<IReadOnlyList<SubtitleCue>> TranscribeWithWindowsAsync(
        MediaAsset asset,
        double sourceStart,
        double duration,
        CancellationToken cancellationToken = default)
    {
        locator.EnsureAvailable();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "KadrStudio", "subtitles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var wavPath = Path.Combine(temporaryDirectory, "speech.wav");
        try
        {
            var extract = await processRunner.RunAsync(
                locator.FfmpegPath,
                [
                    "-hide_banner", "-y", "-ss", Format(sourceStart), "-t", Format(duration), "-i", asset.Path,
                    "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", wavPath
                ],
                cancellationToken: cancellationToken);
            if (extract.ExitCode != 0)
            {
                throw new InvalidOperationException("Не удалось извлечь речь из выбранного клипа.");
            }

            var escapedPath = wavPath.Replace("'", "''");
            var script =
                "[Console]::OutputEncoding=[Text.UTF8Encoding]::new();" +
                "Add-Type -AssemblyName System.Speech;" +
                "$info=[System.Speech.Recognition.SpeechRecognitionEngine]::InstalledRecognizers()|" +
                "Where-Object {$_.Culture.Name -like 'ru-*'}|Select-Object -First 1;" +
                "if($null -eq $info){throw 'Не установлен русский пакет распознавания речи Windows'};" +
                "$engine=[System.Speech.Recognition.SpeechRecognitionEngine]::new($info);" +
                "$engine.LoadGrammar((New-Object System.Speech.Recognition.DictationGrammar));" +
                $"$engine.SetInputToWaveFile('{escapedPath}');" +
                "while($true){$r=$engine.Recognize();if($null -eq $r){break};" +
                "$a=[long]$r.Audio.AudioPosition.TotalMilliseconds;" +
                "$b=[long]($r.Audio.AudioPosition+$r.Audio.Duration).TotalMilliseconds;" +
                "[Console]::WriteLine(($a.ToString()+'`t'+$b.ToString()+'`t'+$r.Text))};" +
                "$engine.Dispose();";
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            var recognition = await processRunner.RunAsync(
                powershell,
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script],
                cancellationToken: cancellationToken);
            if (recognition.ExitCode != 0)
            {
                var reason = PreviewProxyService.LastMeaningfulLine(recognition.StandardError);
                throw new InvalidOperationException(
                    $"Автосубтитры Windows недоступны: {reason}. Установите русский пакет речи в параметрах Windows.");
            }

            return recognition.StandardOutput
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(ParseRecognitionLine)
                .Where(cue => cue is not null && !string.IsNullOrWhiteSpace(cue.Text))
                .Cast<SubtitleCue>()
                .ToList();
        }
        finally
        {
            try
            {
                if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true);
            }
            catch
            {
                // Временный WAV будет очищен Windows позднее.
            }
        }
    }

    public static IReadOnlyList<SubtitleCue> ParseSrt(string content)
    {
        var cues = new List<SubtitleCue>();
        foreach (Match match in SrtBlockRegex().Matches(content.Replace("\r\n", "\n")))
        {
            if (!TryParseSrtTime(match.Groups["start"].Value, out var start) ||
                !TryParseSrtTime(match.Groups["end"].Value, out var end) || end <= start)
            {
                continue;
            }
            var text = Regex.Replace(match.Groups["text"].Value.Trim(), "<[^>]+>", string.Empty)
                .Replace('\n', ' ');
            cues.Add(new SubtitleCue(start, end, text));
        }
        return cues;
    }

    private static SubtitleCue? ParseRecognitionLine(string line)
    {
        var parts = line.Split('\t', 3);
        if (parts.Length != 3 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var startMs) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var endMs))
        {
            return null;
        }
        return new SubtitleCue(startMs / 1000, Math.Max(startMs + 100, endMs) / 1000, parts[2].Trim());
    }

    private static bool TryParseSrtTime(string value, out double seconds)
    {
        seconds = 0;
        return TimeSpan.TryParseExact(value.Trim(), @"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture, out var time) &&
               (seconds = time.TotalSeconds) >= 0;
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"(?ms)^\s*\d+\s*\n(?<start>\d{2}:\d{2}:\d{2},\d{3})\s*-->\s*(?<end>\d{2}:\d{2}:\d{2},\d{3})[^\n]*\n(?<text>.*?)(?=\n\s*\n|\z)")]
    private static partial Regex SrtBlockRegex();
}

public sealed record SubtitleCue(double Start, double End, string Text);
