using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using KadrStudio.Models;

namespace KadrStudio.Services;

public sealed partial class AutoSubtitleService(FfmpegLocator locator, ProcessRunner processRunner)
{
    public async Task<SubtitleTranscriptionResult> TranscribeLocalAsync(
        MediaAsset asset,
        double sourceStart,
        double duration,
        CancellationToken cancellationToken = default)
    {
        var embedded = await TryExtractEmbeddedSubtitlesAsync(asset, sourceStart, duration, preferSigns: false, cancellationToken);
        if (embedded.Count > 0)
        {
            return new SubtitleTranscriptionResult(embedded, "встроенная русская дорожка субтитров");
        }

        var whisper = await TryTranscribeWithWhisperAsync(asset, sourceStart, duration, cancellationToken);
        if (whisper.Count > 0)
        {
            return new SubtitleTranscriptionResult(whisper, "локальный Whisper");
        }

        var windows = await TranscribeWithWindowsAsync(asset, sourceStart, duration, cancellationToken);
        return new SubtitleTranscriptionResult(windows, "Windows Speech");
    }

    public Task<IReadOnlyList<SubtitleCue>> ExtractEmbeddedTextAsync(
        MediaAsset asset,
        double sourceStart,
        double duration,
        bool preferSigns,
        CancellationToken cancellationToken = default)
        => TryExtractEmbeddedSubtitlesAsync(asset, sourceStart, duration, preferSigns, cancellationToken);

    private async Task<IReadOnlyList<SubtitleCue>> TryExtractEmbeddedSubtitlesAsync(
        MediaAsset asset,
        double sourceStart,
        double duration,
        bool preferSigns,
        CancellationToken cancellationToken)
    {
        locator.EnsureAvailable();
        var probe = await processRunner.RunAsync(locator.FfprobePath,
            ["-v", "error", "-select_streams", "s", "-show_entries", "stream=index:stream_tags=language,title", "-of", "json", asset.Path],
            cancellationToken: cancellationToken);
        if (probe.ExitCode != 0)
        {
            return Array.Empty<SubtitleCue>();
        }

        int? selectedIndex = null;
        var selectedScore = int.MinValue;
        try
        {
            using var document = JsonDocument.Parse(probe.StandardOutput);
            foreach (var stream in document.RootElement.GetProperty("streams").EnumerateArray())
            {
                if (!stream.TryGetProperty("index", out var indexElement) || !indexElement.TryGetInt32(out var index))
                {
                    continue;
                }
                var language = string.Empty;
                var title = string.Empty;
                if (stream.TryGetProperty("tags", out var tags))
                {
                    if (tags.TryGetProperty("language", out var languageElement)) language = languageElement.GetString() ?? string.Empty;
                    if (tags.TryGetProperty("title", out var titleElement)) title = titleElement.GetString() ?? string.Empty;
                }
                var score = language.Equals("rus", StringComparison.OrdinalIgnoreCase) ? 20 : 0;
                score += preferSigns
                    ? (title.Contains("надпис", StringComparison.OrdinalIgnoreCase) ? 20 : 0) +
                      (title.Contains("субтит", StringComparison.OrdinalIgnoreCase) ? 5 : 0)
                    : (title.Contains("субтит", StringComparison.OrdinalIgnoreCase) ? 10 : 0) -
                      (title.Contains("надпис", StringComparison.OrdinalIgnoreCase) ? 5 : 0);
                if (score > selectedScore)
                {
                    selectedScore = score;
                    selectedIndex = index;
                }
            }
        }
        catch (JsonException)
        {
            return Array.Empty<SubtitleCue>();
        }
        if (selectedIndex is null || selectedScore < 10)
        {
            return Array.Empty<SubtitleCue>();
        }

        var temporaryDirectory = CreateTemporaryDirectory();
        var srtPath = Path.Combine(temporaryDirectory, "embedded.srt");
        try
        {
            var extract = await processRunner.RunAsync(locator.FfmpegPath,
                ["-hide_banner", "-loglevel", "error", "-y", "-i", asset.Path, "-map", $"0:{selectedIndex.Value}", "-c:s", "srt", srtPath],
                cancellationToken: cancellationToken);
            if (extract.ExitCode != 0 || !File.Exists(srtPath))
            {
                return Array.Empty<SubtitleCue>();
            }
            var end = sourceStart + duration;
            return ParseSrt(await File.ReadAllTextAsync(srtPath, cancellationToken))
                .Where(cue => cue.End > sourceStart && cue.Start < end)
                .Select(cue => new SubtitleCue(
                    Math.Max(0, cue.Start - sourceStart),
                    Math.Min(duration, cue.End - sourceStart),
                    cue.Text))
                .Where(cue => cue.End > cue.Start + 0.05)
                .ToList();
        }
        finally
        {
            TryDeleteDirectory(temporaryDirectory);
        }
    }

    private async Task<IReadOnlyList<SubtitleCue>> TryTranscribeWithWhisperAsync(
        MediaAsset asset,
        double sourceStart,
        double duration,
        CancellationToken cancellationToken)
    {
        var executable = ResolveWhisperExecutable();
        var model = ResolveWhisperModel();
        if (executable is null || model is null)
        {
            return Array.Empty<SubtitleCue>();
        }
        var temporaryDirectory = CreateTemporaryDirectory();
        var wavPath = Path.Combine(temporaryDirectory, "speech.wav");
        var outputBase = Path.Combine(temporaryDirectory, "whisper");
        try
        {
            var extract = await processRunner.RunAsync(locator.FfmpegPath,
                ["-hide_banner", "-loglevel", "error", "-y", "-ss", Format(sourceStart), "-t", Format(duration), "-i", asset.Path,
                 "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", wavPath], cancellationToken: cancellationToken);
            if (extract.ExitCode != 0) return Array.Empty<SubtitleCue>();
            var whisper = await processRunner.RunAsync(executable,
                ["-m", model, "-f", wavPath, "-l", "ru", "-osrt", "-of", outputBase], cancellationToken: cancellationToken);
            var srtPath = outputBase + ".srt";
            return whisper.ExitCode == 0 && File.Exists(srtPath)
                ? ParseSrt(await File.ReadAllTextAsync(srtPath, cancellationToken))
                : Array.Empty<SubtitleCue>();
        }
        finally
        {
            TryDeleteDirectory(temporaryDirectory);
        }
    }

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

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "KadrStudio", "subtitles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string? ResolveWhisperExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("KADR_STUDIO_WHISPER_EXE");
        var names = new[] { "whisper-cli.exe", "whisper.exe" };
        return new[] { configured }
            .Concat(names.Select(name => Path.Combine(AppContext.BaseDirectory, "tools", name)))
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .SelectMany(directory => names.Select(name => Path.Combine(directory.Trim(), name))))
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }

    private static string? ResolveWhisperModel()
    {
        var configured = Environment.GetEnvironmentVariable("KADR_STUDIO_WHISPER_MODEL");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var tools = Path.Combine(AppContext.BaseDirectory, "tools");
        return Directory.Exists(tools)
            ? Directory.EnumerateFiles(tools, "ggml-*.bin").OrderBy(path => path).FirstOrDefault()
            : null;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Временные файлы будут очищены системой позднее.
        }
    }

    [GeneratedRegex(@"(?ms)^\s*\d+\s*\n(?<start>\d{2}:\d{2}:\d{2},\d{3})\s*-->\s*(?<end>\d{2}:\d{2}:\d{2},\d{3})[^\n]*\n(?<text>.*?)(?=\n\s*\n|\z)")]
    private static partial Regex SrtBlockRegex();
}

public sealed record SubtitleCue(double Start, double End, string Text);
public sealed record SubtitleTranscriptionResult(IReadOnlyList<SubtitleCue> Cues, string Engine);
