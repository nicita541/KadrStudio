using System.Globalization;
using System.Text.RegularExpressions;
using KadrStudio.Models;

namespace KadrStudio.Services;

public static partial class EditingCommandPlanner
{
    public static bool TryCreateDeterministic(
        EditorProject project,
        string prompt,
        TimelineClip? selectedClip,
        out EditCommandPlan plan)
    {
        plan = new EditCommandPlan(string.Empty, Array.Empty<EditCommand>());
        var query = (prompt ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        var deleteIntent = query.Contains("удал") || query.Contains("выреж") || query.Contains("убер");
        if (deleteIntent && TryFindMarkerKind(query, out var markerKind))
        {
            var markers = project.Markers
                .Where(marker => marker.Kind == markerKind)
                .OrderByDescending(marker => marker.Confidence)
                .ThenBy(marker => marker.Start)
                .ToList();
            if (markers.Count > 0)
            {
                var marker = markers[0];
                plan = new EditCommandPlan(
                    $"Удалить «{marker.Title}» ({FormatTime(marker.Start)}–{FormatTime(marker.End)}) и сдвинуть последующий монтаж влево.",
                    [new EditCommand(EditCommandType.DeleteRange, marker.Start, marker.End, marker.Title)]);
                return true;
            }
        }

        if (deleteIntent && TryParseRange(query, out var rangeStart, out var rangeEnd))
        {
            rangeStart = Math.Clamp(rangeStart, 0, project.Duration);
            rangeEnd = Math.Clamp(rangeEnd, rangeStart, project.Duration);
            plan = new EditCommandPlan(
                $"Удалить диапазон {FormatTime(rangeStart)}–{FormatTime(rangeEnd)} на всех дорожках.",
                [new EditCommand(EditCommandType.DeleteRange, rangeStart, rangeEnd, "Диапазон из запроса")]);
            return rangeEnd > rangeStart + 0.05;
        }

        if (query.Contains("разреж") || query.Contains("раздел"))
        {
            var splitTime = TryParseSingleTime(query, out var parsedTime)
                ? parsedTime
                : selectedClip is not null
                    ? Math.Clamp(selectedClip.Start + selectedClip.Duration / 2, selectedClip.Start, selectedClip.End)
                    : 0;
            if (splitTime > 0)
            {
                plan = new EditCommandPlan(
                    $"Разрезать активные клипы в позиции {FormatTime(splitTime)}.",
                    [new EditCommand(EditCommandType.SplitAt, splitTime, splitTime, "Разрез из запроса")]);
                return true;
            }
        }

        if (deleteIntent && selectedClip is not null && (query.Contains("клип") || query.Contains("выбран")))
        {
            plan = new EditCommandPlan(
                "Удалить выбранный клип.",
                [new EditCommand(EditCommandType.DeleteSelected, selectedClip.Start, selectedClip.End, "Выбранный клип")]);
            return true;
        }

        return false;
    }

    private static bool TryFindMarkerKind(string query, out MarkerKind kind)
    {
        var mappings = new (string[] Terms, MarkerKind Kind)[]
        {
            (["опенинг", "opening"], MarkerKind.Opening),
            (["после титров", "postcredits", "post-credits"], MarkerKind.PostCredits),
            (["эндинг", "ending", "титры"], MarkerKind.Ending),
            (["превью", "следующей серии", "preview"], MarkerKind.Preview),
            (["рекап", "повтор", "recap"], MarkerKind.Recap)
        };
        foreach (var mapping in mappings)
        {
            if (mapping.Terms.Any(query.Contains))
            {
                kind = mapping.Kind;
                return true;
            }
        }
        kind = default;
        return false;
    }

    private static bool TryParseRange(string value, out double start, out double end)
    {
        start = 0;
        end = 0;
        var match = RangeRegex().Match(value);
        return match.Success &&
               TryParseTime(match.Groups["start"].Value, out start) &&
               TryParseTime(match.Groups["end"].Value, out end) &&
               end > start;
    }

    private static bool TryParseSingleTime(string value, out double seconds)
    {
        seconds = 0;
        var match = TimeRegex().Match(value);
        return match.Success && TryParseTime(match.Value, out seconds);
    }

    private static bool TryParseTime(string value, out double seconds)
    {
        seconds = 0;
        var parts = value.Split(':');
        if (parts.Length is < 2 or > 3 || parts.Any(part => !double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            return false;
        }
        var values = parts.Select(part => double.Parse(part, CultureInfo.InvariantCulture)).ToArray();
        seconds = values.Length == 2 ? values[0] * 60 + values[1] : values[0] * 3600 + values[1] * 60 + values[2];
        return true;
    }

    private static string FormatTime(double seconds)
        => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss\.f" : @"m\:ss\.f");

    [GeneratedRegex(@"(?:с\s*)?(?<start>\d{1,2}(?::\d{2}){1,2})\s*(?:-|–|—|до)\s*(?<end>\d{1,2}(?::\d{2}){1,2})", RegexOptions.IgnoreCase)]
    private static partial Regex RangeRegex();

    [GeneratedRegex(@"\d{1,2}(?::\d{2}){1,2}", RegexOptions.IgnoreCase)]
    private static partial Regex TimeRegex();
}

public enum EditCommandType
{
    DeleteRange,
    SplitAt,
    DeleteSelected
}

public sealed record EditCommand(EditCommandType Type, double Start, double End, string Reason);

public sealed record EditCommandPlan(string Summary, IReadOnlyList<EditCommand> Commands);
