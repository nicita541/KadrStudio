using System.Collections.Immutable;
using System.Text.Json;

namespace KadrStudio.Application.Automation.Agent.Planning;

/// <summary>
/// Extracts factual candidate coordinates for narrower boundary sensing. The
/// candidates are mechanical/OCR locations, not semantic edit decisions.
/// </summary>
public static class AgentObservedTransitionCandidateIndexer
{
    public static ImmutableArray<AgentObservedTransitionCandidate> Build(
        JsonElement? detailedObservation,
        AgentObservedTextActivityRegion region,
        double durationSeconds)
    {
        var candidates = new List<AgentObservedTransitionCandidate>();
        if (detailedObservation is { ValueKind: JsonValueKind.Object } data &&
            data.TryGetProperty("analyses", out var analyses) &&
            analyses.ValueKind == JsonValueKind.Array)
        {
            foreach (var analysis in analyses.EnumerateArray())
            {
                AddMechanicalCandidates(analysis, candidates);
            }
        }

        candidates.Add(new AgentObservedTransitionCandidate(
            region.StartSeconds,
            "observed_text_activity_start"));

        if (region.EndSeconds < durationSeconds - 90d)
        {
            var localGaps = region.Facts
                .Zip(region.Facts.Skip(1), (left, right) => right.Seconds - left.Seconds)
                .Where(gap => gap is > 0 and <= 30d)
                .OrderBy(gap => gap)
                .ToArray();
            var samplingStep = localGaps.Length == 0
                ? 6d
                : localGaps[localGaps.Length / 2];
            var tailPadding = Math.Clamp(samplingStep / 2d, 2d, 8d);
            candidates.Add(new AgentObservedTransitionCandidate(
                Math.Min(durationSeconds, region.EndSeconds + tailPadding),
                "observed_text_activity_end"));
        }

        var selected = new List<AgentObservedTransitionCandidate>();
        foreach (var candidate in candidates
                     .Where(item => double.IsFinite(item.Seconds) &&
                                    item.Seconds > 0 &&
                                    item.Seconds < durationSeconds)
                     .OrderBy(item => Priority(item.Signal))
                     .ThenBy(item => item.Seconds))
        {
            if (selected.Any(item => Math.Abs(item.Seconds - candidate.Seconds) < 3d))
            {
                continue;
            }

            selected.Add(candidate);
            if (selected.Count == 3)
            {
                break;
            }
        }

        return selected.ToImmutableArray();
    }

    private static void AddMechanicalCandidates(
        JsonElement analysis,
        ICollection<AgentObservedTransitionCandidate> candidates)
    {
        if (!TryReadDouble(analysis, "timeline_start_seconds", out var timelineStart) ||
            !TryReadDouble(analysis, "timeline_end_seconds", out var timelineEnd) ||
            !TryReadDouble(analysis, "source_start_seconds", out var sourceStart) ||
            !TryReadDouble(analysis, "source_end_seconds", out var sourceEnd) ||
            sourceEnd <= sourceStart ||
            !analysis.TryGetProperty("observation", out var observation) ||
            observation.ValueKind != JsonValueKind.Object ||
            !observation.TryGetProperty("analysis", out var technical) ||
            technical.ValueKind != JsonValueKind.Object ||
            !technical.TryGetProperty("ranges", out var ranges) ||
            ranges.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var range in ranges.EnumerateArray())
        {
            if (!range.TryGetProperty("kind", out var kindValue) ||
                !string.Equals(
                    kindValue.GetString(),
                    "blackframe",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryReadDouble(range, "start_seconds", out var blackStart))
            {
                candidates.Add(new AgentObservedTransitionCandidate(
                    MapToTimeline(
                        blackStart,
                        sourceStart,
                        sourceEnd,
                        timelineStart,
                        timelineEnd),
                    "black_frame_edge"));
            }
            if (TryReadDouble(range, "end_seconds", out var blackEnd))
            {
                candidates.Add(new AgentObservedTransitionCandidate(
                    MapToTimeline(
                        blackEnd,
                        sourceStart,
                        sourceEnd,
                        timelineStart,
                        timelineEnd),
                    "black_frame_edge"));
            }
        }
    }

    private static double MapToTimeline(
        double sourceSeconds,
        double sourceStart,
        double sourceEnd,
        double timelineStart,
        double timelineEnd)
    {
        var progress = Math.Clamp(
            (sourceSeconds - sourceStart) / (sourceEnd - sourceStart),
            0,
            1);
        return timelineStart + progress * (timelineEnd - timelineStart);
    }

    private static int Priority(string signal)
        => signal switch
        {
            "black_frame_edge" => 0,
            "observed_text_activity_end" => 1,
            _ => 2
        };

    private static bool TryReadDouble(
        JsonElement value,
        string propertyName,
        out double result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Object &&
               value.TryGetProperty(propertyName, out var property) &&
               property.TryGetDouble(out result) &&
               double.IsFinite(result);
    }
}

public sealed record AgentObservedTransitionCandidate(
    double Seconds,
    string Signal);
