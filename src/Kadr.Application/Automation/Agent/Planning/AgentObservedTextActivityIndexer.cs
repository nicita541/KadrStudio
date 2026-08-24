using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;

namespace KadrStudio.Application.Automation.Agent.Planning;

/// <summary>
/// Builds factual time regions from OCR observations produced by the overview
/// sensor. It deliberately does not label, classify, or rank semantic content.
/// </summary>
public static partial class AgentObservedTextActivityIndexer
{
    // Coarse contact-sheet tiles are normally 5–10 seconds apart. A much wider
    // gap must start a new factual region; otherwise unrelated story captions,
    // production credits and later subtitles collapse into one false block.
    private const double MaximumAdjacentFactGapSeconds = 18d;

    public static ImmutableArray<AgentObservedTextActivityRegion> Build(
        IEnumerable<AgentEvidenceRecord> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var facts = new List<AgentObservedTextFact>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in evidence.Where(item => string.Equals(
                     item.ToolName,
                     "inspect_content_overview",
                     StringComparison.OrdinalIgnoreCase)))
        {
            foreach (Match match in VisibleTextFactPattern().Matches(record.Summary))
            {
                var secondsText = match.Groups["seconds"].Value.Replace(',', '.');
                if (!double.TryParse(
                        secondsText,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var seconds))
                {
                    continue;
                }

                var text = Compact(match.Value.Trim(), 320);
                if (seen.Add(text))
                {
                    facts.Add(new AgentObservedTextFact(seconds, text));
                }
            }
        }

        facts.Sort((left, right) => left.Seconds.CompareTo(right.Seconds));
        var groups = new List<List<AgentObservedTextFact>>();
        foreach (var fact in facts)
        {
            if (groups.Count == 0 ||
                fact.Seconds - groups[^1][^1].Seconds > MaximumAdjacentFactGapSeconds)
            {
                groups.Add([fact]);
            }
            else
            {
                groups[^1].Add(fact);
            }
        }

        return groups
            .Select(group => new AgentObservedTextActivityRegion(
                group[0].Seconds,
                group[^1].Seconds,
                group.ToImmutableArray()))
            .OrderByDescending(region => region.Facts.Length)
            .ThenBy(region => region.StartSeconds)
            .ToImmutableArray();
    }

    public static ImmutableArray<string> ExtractFacts(AgentEvidenceRecord evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return Build([evidence])
            .SelectMany(region => region.Facts)
            .Select(fact => fact.Text)
            .ToImmutableArray();
    }

    private static string Compact(string value, int maximumCharacters)
        => value.Length <= maximumCharacters
            ? value
            : value[..(maximumCharacters - 1)].TrimEnd() + "…";

    [GeneratedRegex(
        @"Tile\s+\d+\s+@\s+(?<seconds>\d+(?:[.,]\d+)?)s:\s+[^|\r\n]+?\[text:\s*[^\]]+\]",
        RegexOptions.CultureInvariant)]
    private static partial Regex VisibleTextFactPattern();
}

public sealed record AgentObservedTextActivityRegion(
    double StartSeconds,
    double EndSeconds,
    ImmutableArray<AgentObservedTextFact> Facts);

public sealed record AgentObservedTextFact(
    double Seconds,
    string Text);
