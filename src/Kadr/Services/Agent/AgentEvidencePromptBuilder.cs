using System.Collections.Immutable;
using System.Text.Json;
using KadrStudio.Application.Automation.Agent;
using KadrStudio.Application.Automation.Agent.Runtime;

namespace KadrStudio.Services.Agent;

/// <summary>
/// Builds a bounded, evenly sampled evidence context for model phases. Raw tool
/// payloads remain in the audit log; the model receives factual summaries without
/// duplicating the same observation as ledger text and large JSON.
/// </summary>
internal static class AgentEvidencePromptBuilder
{
    private const int PlanningBudgetCharacters = 24_000;
    private const int ReviewBudgetCharacters = 32_000;

    public static ImmutableArray<AgentEvidencePromptEntry> ForPlanning(
        AgentTaskState task)
    {
        var candidates = CurrentSourceEvidence(task)
            .OrderBy(Priority)
            .ThenBy(item => item.Sequence);
        return SelectWithinBudget(
            candidates.Select(item => (Evidence: item, Reason: "planning_context", Required: false)),
            PlanningBudgetCharacters);
    }

    public static ImmutableArray<AgentEvidencePromptEntry> ForReview(
        AgentPlanReviewRequest request)
    {
        var referenced = request.Plan.Steps
            .SelectMany(step => step.EvidenceObservationSequences.IsDefault
                ? []
                : step.EvidenceObservationSequences)
            .ToHashSet();
        var editedRanges = ReadEditedRanges(request.Plan);
        var candidates = CurrentSourceEvidence(request.Task)
            .Select(evidence =>
            {
                var isReferenced = referenced.Contains(evidence.Sequence);
                var overlapsEdit = editedRanges.Any(range => OverlapsOrTouches(
                    evidence,
                    range.Start,
                    range.End,
                    contextSeconds: 20));
                var isBoundary = evidence.ToolName.Equals(
                    "inspect_boundary",
                    StringComparison.OrdinalIgnoreCase);
                var reason = isReferenced
                    ? "plan_reference"
                    : overlapsEdit
                        ? "overlapping_or_adjacent_counterevidence"
                        : isBoundary
                            ? "measured_boundary_context"
                            : "global_context";
                return (Evidence: evidence, Reason: reason, Required: isReferenced);
            })
            .Where(item =>
                item.Required ||
                item.Reason != "global_context" ||
                item.Evidence.ToolName.Equals(
                    "inspect_content_overview",
                    StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Required)
            .ThenBy(item => item.Reason == "overlapping_or_adjacent_counterevidence" ? 0 : 1)
            .ThenBy(item => Priority(item.Evidence))
            .ThenBy(item => item.Evidence.Sequence);
        return SelectWithinBudget(candidates, ReviewBudgetCharacters);
    }

    private static ImmutableArray<AgentEvidencePromptEntry> SelectWithinBudget(
        IEnumerable<(AgentEvidenceRecord Evidence, string Reason, bool Required)> candidates,
        int budgetCharacters)
    {
        var selected = ImmutableArray.CreateBuilder<AgentEvidencePromptEntry>();
        var remaining = budgetCharacters;
        foreach (var candidate in candidates)
        {
            var perEntryLimit = PerEntryLimit(candidate.Evidence);
            if (!candidate.Required && remaining < 512)
            {
                continue;
            }

            var allowed = candidate.Required
                ? perEntryLimit
                : Math.Min(perEntryLimit, remaining);
            var summary = Summarize(candidate.Evidence, Math.Max(512, allowed));
            selected.Add(new AgentEvidencePromptEntry(
                candidate.Evidence,
                summary,
                candidate.Reason));
            remaining -= summary.Length;
        }
        return selected.ToImmutable();
    }

    private static IEnumerable<AgentEvidenceRecord> CurrentSourceEvidence(
        AgentTaskState task)
        => task.Evidence.Where(evidence =>
            evidence.TargetId == task.SourceSequenceId &&
            evidence.SourceRevision == task.SourceSequenceRevision &&
            evidence.Capabilities != AgentEvidenceCapabilities.None);

    private static int Priority(AgentEvidenceRecord evidence)
        => evidence.ToolName switch
        {
            "inspect_content_overview" => 0,
            "inspect_content_sample" => 0,
            "inspect_range" => 1,
            "inspect_boundary" => 2,
            "inspect_timeline" or "inspect_sequence_overview" => 3,
            _ => 4
        };

    private static int PerEntryLimit(AgentEvidenceRecord evidence)
        => evidence.ToolName switch
        {
            "inspect_content_overview" => 8_000,
            "inspect_content_sample" => 1_200,
            "inspect_range" => 4_500,
            "inspect_boundary" => 800,
            _ => 2_500
        };

    private static string Summarize(
        AgentEvidenceRecord evidence,
        int maximumCharacters)
    {
        var value = evidence.Summary.Trim();
        if (value.Length <= maximumCharacters)
        {
            return value;
        }

        if (evidence.ToolName.Equals(
                "inspect_content_sample",
                StringComparison.OrdinalIgnoreCase))
        {
            return SummarizeContentSample(value, maximumCharacters);
        }

        if (!evidence.ToolName.Equals(
                "inspect_content_overview",
                StringComparison.OrdinalIgnoreCase))
        {
            return Compact(value, maximumCharacters);
        }

        // Preserve every coarse window instead of keeping only the beginning of
        // a long video. Each line represents an independently measured range.
        var lines = value
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length <= 1)
        {
            return Compact(value, maximumCharacters);
        }

        var header = Compact(lines[0], Math.Min(700, maximumCharacters / 4));
        var lineBudget = Math.Max(
            220,
            (maximumCharacters - header.Length - lines.Length) / (lines.Length - 1));
        return Compact(
            header + "\n" + string.Join('\n', lines.Skip(1).Select(line => Compact(line, lineBudget))),
            maximumCharacters);
    }

    private static string SummarizeContentSample(
        string value,
        int maximumCharacters)
    {
        var lines = value
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var selected = lines
            .Where((line, index) =>
                index == 0 ||
                line.StartsWith("Time mapping:", StringComparison.Ordinal) ||
                line.StartsWith("Measurement:", StringComparison.Ordinal) ||
                line.StartsWith("Audio sample ", StringComparison.Ordinal) ||
                line.StartsWith("Sample ", StringComparison.Ordinal))
            .ToArray();
        if (selected.Length == 0)
        {
            return Compact(value, maximumCharacters);
        }

        var fixedLines = selected
            .Where(line => !line.StartsWith("Sample ", StringComparison.Ordinal))
            .Select(line => Compact(line, 130));
        var sampleLines = selected
            .Where(line => line.StartsWith("Sample ", StringComparison.Ordinal))
            .Select(line => CompactDistributedSampleLine(line, 330));
        return Compact(
            string.Join('\n', fixedLines.Concat(sampleLines)),
            maximumCharacters);
    }

    private static string CompactDistributedSampleLine(
        string line,
        int maximumCharacters)
    {
        var parts = line.Split(" | ", StringSplitOptions.TrimEntries);
        if (parts.Length <= 1)
        {
            return CompactMiddle(line, maximumCharacters);
        }

        var perPart = Math.Max(55, maximumCharacters / parts.Length);
        return Compact(
            string.Join(" | ", parts.Select(part => CompactMiddle(part, perPart))),
            maximumCharacters);
    }

    private static string CompactMiddle(string value, int maximumCharacters)
    {
        if (value.Length <= maximumCharacters)
        {
            return value;
        }

        var tail = Math.Max(18, maximumCharacters / 3);
        var head = maximumCharacters - tail - 1;
        return value[..head].TrimEnd() + "…" + value[^tail..].TrimStart();
    }

    private static ImmutableArray<(double Start, double End)> ReadEditedRanges(
        AgentPlanDraft plan)
    {
        var ranges = ImmutableArray.CreateBuilder<(double Start, double End)>();
        foreach (var step in plan.Steps)
        {
            if (step.ExpectedEditingArguments is not { ValueKind: JsonValueKind.Object } arguments)
            {
                continue;
            }
            if (TryReadRange(arguments, out var range))
            {
                ranges.Add(range);
            }
            if (!arguments.TryGetProperty("ranges", out var array) ||
                array.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (var item in array.EnumerateArray())
            {
                if (TryReadRange(item, out range))
                {
                    ranges.Add(range);
                }
            }
        }
        return ranges.ToImmutable();
    }

    private static bool TryReadRange(
        JsonElement value,
        out (double Start, double End) range)
    {
        range = default;
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("start_seconds", out var start) ||
            !start.TryGetDouble(out var startSeconds) ||
            !value.TryGetProperty("end_seconds", out var end) ||
            !end.TryGetDouble(out var endSeconds) ||
            endSeconds <= startSeconds)
        {
            return false;
        }
        range = (startSeconds, endSeconds);
        return true;
    }

    private static bool OverlapsOrTouches(
        AgentEvidenceRecord evidence,
        double start,
        double end,
        double contextSeconds)
        => evidence.StartSeconds is { } evidenceStart &&
           evidence.EndSeconds is { } evidenceEnd &&
           evidenceEnd >= start - contextSeconds &&
           evidenceStart <= end + contextSeconds;

    private static string Compact(string value, int maximumCharacters)
        => value.Length <= maximumCharacters
            ? value
            : value[..(maximumCharacters - 1)].TrimEnd() + "…";
}

internal sealed record AgentEvidencePromptEntry(
    AgentEvidenceRecord Evidence,
    string Summary,
    string SelectionReason);
