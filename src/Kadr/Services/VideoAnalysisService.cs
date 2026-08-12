using System.Globalization;
using System.Text.RegularExpressions;
using KadrStudio.Models;

namespace KadrStudio.Services;

public sealed class VideoAnalysisService(FfmpegLocator locator, ProcessRunner processRunner)
{
    private static readonly Regex SceneTimeRegex = new(@"showinfo.*pts_time:(?<time>-?\d+(?:\.\d+)?)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BlackRegex = new(@"black_start:(?<start>\d+(?:\.\d+)?)\s+black_end:(?<end>\d+(?:\.\d+)?)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SilenceStartRegex = new(@"silence_start:\s*(?<time>\d+(?:\.\d+)?)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SilenceEndRegex = new(@"silence_end:\s*(?<time>\d+(?:\.\d+)?)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FreezeStartRegex = new(@"freeze_start:\s*(?<time>\d+(?:\.\d+)?)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FreezeEndRegex = new(@"freeze_end:\s*(?<time>\d+(?:\.\d+)?)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PromptRangeRegex = new(
        @"(?<start>\d{1,2}(?::\d{2}){1,2})\s*[-–—]\s*(?<end>\d{1,2}(?::\d{2}){1,2})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<VideoAnalysisResult> AnalyzeAsync(
        VideoAnalysisRequest request,
        IProgress<VideoAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        locator.EnsureAvailable();
        if (request.Asset.Kind != MediaKind.Video)
        {
            throw new InvalidOperationException("Автоматический анализ сцен доступен для видеофайлов.");
        }

        var sourceDuration = Math.Max(0.1, request.Asset.Duration);
        var requestedRange = TryParseRange(request.Query, out var promptStart, out var promptEnd)
            ? (Start: promptStart, End: promptEnd)
            : (Start: request.SourceStart, End: request.SourceEnd);
        var rangeStart = Math.Clamp(requestedRange.Start, 0, Math.Max(0, sourceDuration - 0.1));
        var rangeEnd = Math.Clamp(requestedRange.End, rangeStart + 0.1, sourceDuration);
        var rangeDuration = rangeEnd - rangeStart;

        progress?.Report(new VideoAnalysisProgress(3, "Шаг 1/5: общий проход по всему диапазону"));
        var detectorLines = new List<string>();
        var videoResult = await processRunner.RunAsync(
            locator.FfmpegPath,
            [
                "-hide_banner", "-ss", Format(rangeStart), "-t", Format(rangeDuration), "-i", request.Asset.Path,
                "-filter_complex",
                "[0:v]scale=320:-2,fps=3,split=3[scenein][blackin][freezein];" +
                "[scenein]select='gt(scene,0.28)',showinfo[sceneout];" +
                "[blackin]blackdetect=d=0.35:pix_th=0.10[blackout];" +
                "[freezein]freezedetect=n=-50dB:d=2[freezeout]",
                "-map", "[sceneout]", "-map", "[blackout]", "-map", "[freezeout]",
                "-an", "-f", "null", "-"
            ],
            line => detectorLines.Add(line),
            cancellationToken);
        EnsureSuccess(videoResult, "Не удалось выполнить анализ изображения");

        progress?.Report(new VideoAnalysisProgress(55, "Шаг 2/5: поиск пауз и тишины"));
        var audioLines = new List<string>();
        if (request.Asset.HasAudio)
        {
            var audioResult = await processRunner.RunAsync(
                locator.FfmpegPath,
                [
                    "-hide_banner", "-ss", Format(rangeStart), "-t", Format(rangeDuration), "-i", request.Asset.Path,
                    "-vn", "-af", "silencedetect=noise=-38dB:d=0.6", "-f", "null", "-"
                ],
                line => audioLines.Add(line),
                cancellationToken);
            EnsureSuccess(audioResult, "Не удалось выполнить анализ звука");
        }

        progress?.Report(new VideoAnalysisProgress(72, "Шаг 3/5: уточнение зон по склейкам и затемнениям"));
        var sceneCuts = ParseTimes(detectorLines, SceneTimeRegex, rangeStart, rangeDuration);
        var blackRanges = ParsePairedRanges(detectorLines, BlackRegex, rangeStart, rangeDuration);
        var silenceRanges = ParseSplitRanges(audioLines, SilenceStartRegex, SilenceEndRegex, rangeStart, rangeDuration);
        var freezeRanges = ParseSplitRanges(detectorLines, FreezeStartRegex, FreezeEndRegex, rangeStart, rangeDuration);
        var detected = BuildMarkers(request.Query, rangeStart, rangeEnd, sceneCuts, blackRanges, silenceRanges, freezeRanges);

        var summary =
            $"Диапазон {FormatTime(rangeStart)}–{FormatTime(rangeEnd)}: " +
            $"границ сцен — {sceneCuts.Count}, затемнений — {blackRanges.Count}, " +
            $"пауз — {silenceRanges.Count}, стоп-кадров — {freezeRanges.Count}. " +
            "Метки опенинга, эндинга и превью являются вероятностной оценкой по структуре монтажа.";
        progress?.Report(new VideoAnalysisProgress(84, "Шаг 4/5: грубые смысловые зоны готовы для проверки ИИ"));
        return new VideoAnalysisResult(summary, rangeStart, rangeEnd, detected);
    }

    public async Task<VideoAnalysisResult> RefineSemanticBoundariesAsync(
        MediaAsset asset,
        VideoAnalysisResult result,
        IProgress<VideoAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var semanticKinds = new HashSet<MarkerKind>
        {
            MarkerKind.Opening, MarkerKind.Ending, MarkerKind.PostCredits,
            MarkerKind.Preview, MarkerKind.Recap, MarkerKind.Note
        };
        var semantic = result.Ranges.Where(range => semanticKinds.Contains(range.Kind)).ToList();
        if (semantic.Count == 0)
        {
            return result;
        }

        var refined = new List<DetectedVideoRange>();
        for (var index = 0; index < semantic.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var range = semantic[index];
            progress?.Report(new VideoAnalysisProgress(
                84 + 15d * index / Math.Max(1, semantic.Count),
                $"Шаг 5/5: покадровая проверка — {range.Title}"));
            var coarseStart = range.SourceStart;
            var coarseEnd = range.SourceStart + range.Duration;
            var start = await FindExactCutAsync(asset.Path, coarseStart, result.SourceStart, result.SourceEnd, cancellationToken);
            var end = await FindExactCutAsync(asset.Path, coarseEnd, result.SourceStart, result.SourceEnd, cancellationToken);
            if (end <= start + 0.1)
            {
                start = coarseStart;
                end = coarseEnd;
            }
            var fps = asset.FrameRate > 0 ? asset.FrameRate : 25;
            start = Math.Round(start * fps) / fps;
            end = Math.Round(end * fps) / fps;
            var startDelta = start - coarseStart;
            var endDelta = end - coarseEnd;
            var startFrames = (int)Math.Round(startDelta * fps);
            var endFrames = (int)Math.Round(endDelta * fps);
            var precision = 1d / fps;
            var description = string.Join(" ", new[]
            {
                range.Description,
                $"Каскад: общий обзор → точная зона → проверка соседних кадров.",
                $"Начало сдвинуто на {Signed(startDelta)} с ({Signed(startFrames)} кадр.), конец — на {Signed(endDelta)} с ({Signed(endFrames)} кадр.).",
                $"Расчётная точность ±{precision:0.###} с (1 кадр при {fps:0.###} fps)."
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
            refined.Add(range with
            {
                SourceStart = start,
                Duration = Math.Max(0.1, end - start),
                Description = description,
                Confidence = Math.Clamp(range.Confidence + 0.07, 0.05, 0.99)
            });
        }

        var refinedIds = semantic.Select(range => (range.Kind, range.SourceStart, range.Duration)).ToHashSet();
        var merged = result.Ranges
            .Where(range => !refinedIds.Contains((range.Kind, range.SourceStart, range.Duration)))
            .Concat(refined)
            .OrderBy(range => range.SourceStart)
            .ThenBy(range => range.Kind)
            .ToList();
        progress?.Report(new VideoAnalysisProgress(100, "Покадровое уточнение границ завершено"));
        return result with
        {
            Summary = result.Summary + " Смысловые границы уточнены до ближайшей подтверждённой склейки и округлены к кадру.",
            Ranges = merged
        };
    }

    private async Task<double> FindExactCutAsync(
        string path,
        double target,
        double minimum,
        double maximum,
        CancellationToken cancellationToken)
    {
        if (target <= minimum + 0.05) return minimum;
        if (target >= maximum - 0.05) return maximum;
        var coarseCandidates = await ScanSceneCutsAsync(path, target, minimum, maximum, 12, 0.18, cancellationToken);
        var coarse = coarseCandidates.OrderBy(time => Math.Abs(time - target)).FirstOrDefault(target);
        var fineCandidates = await ScanSceneCutsAsync(path, coarse, minimum, maximum, 3, 0.07, cancellationToken);
        return fineCandidates.OrderBy(time => Math.Abs(time - coarse)).FirstOrDefault(coarse);
    }

    private async Task<IReadOnlyList<double>> ScanSceneCutsAsync(
        string path,
        double center,
        double minimum,
        double maximum,
        double radius,
        double threshold,
        CancellationToken cancellationToken)
    {
        var windowStart = Math.Max(minimum, center - radius);
        var windowEnd = Math.Min(maximum, center + radius);
        if (windowEnd <= windowStart + 0.1) return Array.Empty<double>();
        var lines = new List<string>();
        var scan = await processRunner.RunAsync(locator.FfmpegPath,
            [
                "-hide_banner", "-ss", Format(windowStart), "-t", Format(windowEnd - windowStart), "-i", path,
                "-vf", $"scale=640:-2,select='gt(scene,{Format(threshold)})',showinfo", "-an", "-f", "null", "-"
            ], line => lines.Add(line), cancellationToken);
        if (scan.ExitCode != 0) return Array.Empty<double>();
        return ParseTimes(lines, SceneTimeRegex, windowStart, windowEnd - windowStart)
            .Where(time => Math.Abs(time - center) <= radius)
            .ToList();
    }

    private static string Signed(double value) => value.ToString("+0.###;-0.###;0", CultureInfo.InvariantCulture);
    private static string Signed(int value) => value.ToString("+0;-0;0", CultureInfo.InvariantCulture);

    public static bool TryParseRange(string prompt, out double start, out double end)
    {
        start = 0;
        end = 0;
        var match = PromptRangeRegex.Match(prompt ?? string.Empty);
        if (!match.Success ||
            !TryParseTime(match.Groups["start"].Value, out start) ||
            !TryParseTime(match.Groups["end"].Value, out end) ||
            end <= start)
        {
            start = 0;
            end = 0;
            return false;
        }

        return true;
    }

    private static List<DetectedVideoRange> BuildMarkers(
        string prompt,
        double rangeStart,
        double rangeEnd,
        IReadOnlyList<double> sceneCuts,
        IReadOnlyList<TimeRange> blackRanges,
        IReadOnlyList<TimeRange> silenceRanges,
        IReadOnlyList<TimeRange> freezeRanges)
    {
        var query = (prompt ?? string.Empty).ToLowerInvariant();
        var genericAnalysis = string.IsNullOrWhiteSpace(query) || query.Contains("анализ") || query.Contains("всё") || query.Contains("все");
        var wantsScenes = genericAnalysis || query.Contains("сцен") || query.Contains("монтаж") || query.Contains("разбей");
        var wantsTechnical = genericAnalysis || query.Contains("тиш") || query.Contains("пауз") || query.Contains("затем") || query.Contains("стоп") || query.Contains("повтор");
        var wantsAnime = genericAnalysis || query.Contains("аним") || query.Contains("опен") || query.Contains("эндинг") || query.Contains("титр") || query.Contains("следующ");
        var markers = new List<DetectedVideoRange>();

        if (wantsScenes)
        {
            var boundaries = new List<double> { rangeStart };
            boundaries.AddRange(sceneCuts.Where(time => time > rangeStart + 0.8 && time < rangeEnd - 0.8));
            boundaries.Add(rangeEnd);
            boundaries = boundaries.DistinctBy(value => Math.Round(value, 1)).OrderBy(value => value).ToList();
            var sceneIndex = 1;
            for (var index = 0; index < boundaries.Count - 1 && sceneIndex <= 120; index++)
            {
                var duration = boundaries[index + 1] - boundaries[index];
                if (duration < 1.2)
                {
                    continue;
                }

                markers.Add(new DetectedVideoRange(
                    MarkerKind.Scene,
                    boundaries[index],
                    duration,
                    $"Сцена {sceneIndex++}",
                    "Диапазон между заметными монтажными склейками.",
                    0.78));
            }
        }

        if (wantsTechnical)
        {
            markers.AddRange(blackRanges.Where(range => range.Duration >= 0.35).Select((range, index) =>
                new DetectedVideoRange(MarkerKind.BlackFrame, range.Start, range.Duration, $"Затемнение {index + 1}", "Тёмный переход или чёрный кадр.", 0.9)));
            markers.AddRange(silenceRanges.Where(range => range.Duration >= 0.6).Select((range, index) =>
                new DetectedVideoRange(MarkerKind.Silence, range.Start, range.Duration, $"Пауза {index + 1}", "Участок с очень тихим звуком.", 0.86)));
            markers.AddRange(freezeRanges.Where(range => range.Duration >= 2).Select((range, index) =>
                new DetectedVideoRange(MarkerKind.Freeze, range.Start, range.Duration, $"Стоп-кадр {index + 1}", "Продолжительный почти неизменный кадр.", 0.82)));
        }

        if (wantsAnime && rangeEnd - rangeStart >= 240)
        {
            AddAnimeStructure(markers, rangeStart, rangeEnd, sceneCuts, blackRanges);
        }

        return markers
            .Where(marker => marker.Duration >= 0.1)
            .OrderBy(marker => marker.SourceStart)
            .ThenBy(marker => marker.Kind)
            .ToList();
    }

    private static void AddAnimeStructure(
        ICollection<DetectedVideoRange> markers,
        double rangeStart,
        double rangeEnd,
        IReadOnlyList<double> sceneCuts,
        IReadOnlyList<TimeRange> blackRanges)
    {
        var duration = rangeEnd - rangeStart;
        var openingSearchEnd = Math.Min(rangeEnd, rangeStart + Math.Min(300, duration * 0.3));
        var coarseOpeningStart = FindDensestWindowStart(sceneCuts, rangeStart, openingSearchEnd, 90);
        var openingStart = SnapToTechnicalBoundary(coarseOpeningStart, sceneCuts, blackRanges, 12);
        var openingEnd = SnapToTechnicalBoundary(Math.Min(rangeEnd, openingStart + 90), sceneCuts, blackRanges, 12);
        if (openingEnd - openingStart is < 55 or > 135)
        {
            openingEnd = Math.Min(rangeEnd, openingStart + 90);
        }
        markers.Add(new DetectedVideoRange(
            MarkerKind.Opening,
            openingStart,
            openingEnd - openingStart,
            "Вероятный опенинг",
            "90-секундный участок с высокой плотностью монтажных склеек в начале видео.",
            SceneConfidence(sceneCuts, openingStart, openingEnd, 0.52)));

        if (openingStart - rangeStart >= 20)
        {
            markers.Add(new DetectedVideoRange(
                MarkerKind.Recap,
                rangeStart,
                openingStart - rangeStart,
                "Вероятный рекап / вступление",
                "Материал перед предполагаемым опенингом.",
                0.44));
        }

        var lateBlack = blackRanges
            .Where(range => range.Start >= rangeEnd - Math.Min(360, duration * 0.35))
            .OrderBy(range => range.Start)
            .ToList();
        var previewBoundary = lateBlack
            .Where(range => rangeEnd - range.End is >= 8 and <= 55)
            .LastOrDefault();
        var previewStart = previewBoundary.Duration > 0 ? previewBoundary.End : rangeEnd;
        if (rangeEnd - previewStart is >= 8 and <= 55)
        {
            markers.Add(new DetectedVideoRange(
                MarkerKind.Preview,
                previewStart,
                rangeEnd - previewStart,
                "Вероятное превью следующей серии",
                "Короткий финальный блок после позднего затемнения.",
                0.58));
        }

        var endingEnd = previewStart < rangeEnd ? previewStart : rangeEnd;
        endingEnd = SnapToTechnicalBoundary(endingEnd, sceneCuts, blackRanges, 8);
        var endingStart = SnapToTechnicalBoundary(Math.Max(rangeStart, endingEnd - 90), sceneCuts, blackRanges, 12);
        if (endingEnd - endingStart is < 55 or > 135)
        {
            endingStart = Math.Max(rangeStart, endingEnd - 90);
        }
        markers.Add(new DetectedVideoRange(
            MarkerKind.Ending,
            endingStart,
            endingEnd - endingStart,
            "Вероятный эндинг",
            "Финальный музыкальный блок перед превью или концом файла.",
            SceneConfidence(sceneCuts, endingStart, endingEnd, 0.48)));

        // Посттитровую сцену нельзя надёжно вывести только из затемнения перед эндингом.
        // Её добавляет vision-проход, если он действительно видит отдельную сцену после титров.
    }

    private static double FindDensestWindowStart(IReadOnlyList<double> cuts, double start, double end, double window)
    {
        var lastStart = Math.Max(start, end - window);
        var bestStart = start;
        var bestCount = -1;
        var candidates = cuts
            .Where(time => time >= start && time <= lastStart)
            .Prepend(start)
            .Distinct()
            .OrderBy(time => time);
        foreach (var candidate in candidates)
        {
            var count = cuts.Count(time => time >= candidate && time <= candidate + window);
            if (count > bestCount)
            {
                bestCount = count;
                bestStart = candidate;
            }
        }
        return bestStart;
    }

    private static double SnapToTechnicalBoundary(
        double target,
        IReadOnlyList<double> sceneCuts,
        IReadOnlyList<TimeRange> blackRanges,
        double radius)
    {
        var blackBoundary = blackRanges
            .SelectMany(range => new[] { range.Start, range.End })
            .Where(time => Math.Abs(time - target) <= Math.Min(radius, 6))
            .OrderBy(time => Math.Abs(time - target))
            .Cast<double?>()
            .FirstOrDefault();
        if (blackBoundary.HasValue)
        {
            return blackBoundary.Value;
        }

        var sceneBoundary = sceneCuts
            .Where(time => Math.Abs(time - target) <= radius)
            .OrderBy(time => Math.Abs(time - target))
            .Cast<double?>()
            .FirstOrDefault();
        return sceneBoundary ?? target;
    }

    private static double SceneConfidence(IReadOnlyList<double> cuts, double start, double end, double baseline)
    {
        var perMinute = cuts.Count(time => time >= start && time <= end) / Math.Max(0.1, (end - start) / 60);
        return Math.Clamp(baseline + perMinute / 120, baseline, 0.82);
    }

    private static List<double> ParseTimes(IEnumerable<string> lines, Regex regex, double rangeStart, double rangeDuration)
        => lines.Select(line => regex.Match(line))
            .Where(match => match.Success)
            .Select(match => ParseDouble(match.Groups["time"].Value))
            .Where(value => value >= 0)
            .Select(value => ToSourceTime(value, rangeStart, rangeDuration))
            .Where(value => value >= rangeStart && value <= rangeStart + rangeDuration)
            .DistinctBy(value => Math.Round(value, 2))
            .OrderBy(value => value)
            .ToList();

    private static List<TimeRange> ParsePairedRanges(IEnumerable<string> lines, Regex regex, double rangeStart, double rangeDuration)
    {
        var ranges = new List<TimeRange>();
        foreach (var line in lines)
        {
            var match = regex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var start = ToSourceTime(ParseDouble(match.Groups["start"].Value), rangeStart, rangeDuration);
            var end = ToSourceTime(ParseDouble(match.Groups["end"].Value), rangeStart, rangeDuration);
            AddRange(ranges, start, end, rangeStart, rangeStart + rangeDuration);
        }
        return ranges;
    }

    private static List<TimeRange> ParseSplitRanges(
        IEnumerable<string> lines,
        Regex startRegex,
        Regex endRegex,
        double rangeStart,
        double rangeDuration)
    {
        var ranges = new List<TimeRange>();
        double? currentStart = null;
        foreach (var line in lines)
        {
            var startMatch = startRegex.Match(line);
            if (startMatch.Success)
            {
                currentStart = ToSourceTime(ParseDouble(startMatch.Groups["time"].Value), rangeStart, rangeDuration);
            }

            var endMatch = endRegex.Match(line);
            if (!endMatch.Success || currentStart is null)
            {
                continue;
            }

            var end = ToSourceTime(ParseDouble(endMatch.Groups["time"].Value), rangeStart, rangeDuration);
            AddRange(ranges, currentStart.Value, end, rangeStart, rangeStart + rangeDuration);
            currentStart = null;
        }

        if (currentStart is { } openStart)
        {
            AddRange(ranges, openStart, rangeStart + rangeDuration, rangeStart, rangeStart + rangeDuration);
        }
        return ranges;
    }

    private static void AddRange(ICollection<TimeRange> ranges, double start, double end, double minimum, double maximum)
    {
        start = Math.Clamp(start, minimum, maximum);
        end = Math.Clamp(end, minimum, maximum);
        if (end > start + 0.05)
        {
            ranges.Add(new TimeRange(start, end));
        }
    }

    private static double ToSourceTime(double detectorTime, double rangeStart, double rangeDuration)
        => rangeStart > 0.01 && detectorTime > rangeDuration + 0.5
            ? detectorTime
            : rangeStart + detectorTime;

    private static bool TryParseTime(string value, out double seconds)
    {
        seconds = 0;
        var parts = value.Split(':');
        if (parts.Length is < 2 or > 3 || parts.Any(part => !double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            return false;
        }

        var parsed = parts.Select(part => double.Parse(part, CultureInfo.InvariantCulture)).ToArray();
        seconds = parsed.Length == 2
            ? parsed[0] * 60 + parsed[1]
            : parsed[0] * 3600 + parsed[1] * 60 + parsed[2];
        return true;
    }

    private static double ParseDouble(string value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : -1;

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string FormatTime(double seconds)
        => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");

    private static void EnsureSuccess(ProcessResult result, string message)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{message}.\n{PreviewProxyService.LastMeaningfulLine(result.StandardError)}");
        }
    }

    private readonly record struct TimeRange(double Start, double End)
    {
        public double Duration => End - Start;
    }
}

public sealed record VideoAnalysisRequest(
    MediaAsset Asset,
    double SourceStart,
    double SourceEnd,
    string Query);

public sealed record VideoAnalysisResult(
    string Summary,
    double SourceStart,
    double SourceEnd,
    IReadOnlyList<DetectedVideoRange> Ranges);

public sealed record DetectedVideoRange(
    MarkerKind Kind,
    double SourceStart,
    double Duration,
    string Title,
    string Description,
    double Confidence);

public sealed record VideoAnalysisProgress(double Percent, string Stage);
