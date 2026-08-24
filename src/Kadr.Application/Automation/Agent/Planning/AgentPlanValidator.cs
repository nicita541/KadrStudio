using System.Collections.Immutable;
using System.Text.Json;
using KadrStudio.Application.Automation.Agent.Runtime;
using KadrStudio.Application.Automation.Agent.Tools;

namespace KadrStudio.Application.Automation.Agent.Planning;

public sealed record AgentPlanValidationResult(
    bool IsValid,
    string Error,
    string ErrorCode)
{
    public static AgentPlanValidationResult Valid { get; } =
        new(true, string.Empty, string.Empty);

    public static AgentPlanValidationResult Invalid(
        string error,
        string errorCode = "plan_invalid")
        => new(false, error, errorCode);
}

public sealed class AgentPlanValidator(AgentToolRegistry registry)
{
    public AgentPlanValidationResult Validate(
        AgentTaskState task,
        AgentPlanDraft plan,
        ImmutableArray<AgentModelObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(plan);

        if (string.IsNullOrWhiteSpace(plan.Objective) ||
            string.IsNullOrWhiteSpace(plan.Summary) ||
            plan.Steps.IsDefaultOrEmpty ||
            plan.Steps.Any(step =>
                string.IsNullOrWhiteSpace(step.Title) ||
                string.IsNullOrWhiteSpace(step.Description)))
        {
            return AgentPlanValidationResult.Invalid(
                "A published plan needs an objective, summary and titled, described steps.");
        }

        var editingSteps = plan.Steps
            .Where(step => !string.IsNullOrWhiteSpace(step.ExpectedEditingTool))
            .ToArray();
        if (task.Brief?.Kind is AgentTaskKind.Edit or AgentTaskKind.Mixed &&
            editingSteps.Length == 0)
        {
            return AgentPlanValidationResult.Invalid(
                "An edit or mixed task must contain at least one exact editing action.");
        }
        if (task.Brief?.Kind is AgentTaskKind.Edit or AgentTaskKind.Mixed &&
            editingSteps.Length != plan.Steps.Length)
        {
            return AgentPlanValidationResult.Invalid(
                "A published edit plan may contain only editing actions. Investigation, Agent Draft creation and verification are automatic workflow phases, not plan steps.");
        }
        if (task.Brief?.Kind == AgentTaskKind.ReadOnly)
        {
            return AgentPlanValidationResult.Invalid(
                "A read-only task must complete with a proven answer instead of publishing an edit plan.");
        }

        var rippleDeleteSteps = editingSteps.Count(step =>
            step.ExpectedEditingTool is "ripple_delete_range" or "ripple_delete_ranges");
        if (rippleDeleteSteps > 1)
        {
            return AgentPlanValidationResult.Invalid(
                "Multiple ripple-delete actions would invalidate later coordinates. Use one ripple_delete_ranges action for ranges measured on the same sequence revision.");
        }

        var signatures = new HashSet<string>(StringComparer.Ordinal);
        var currentSourceCapabilities = task.Evidence
            .Where(item =>
                item.TargetId == task.SourceSequenceId &&
                item.SourceRevision == task.SourceSequenceRevision)
            .Aggregate(
                AgentEvidenceCapabilities.None,
                (current, item) => current | item.Capabilities);
        foreach (var step in editingSteps)
        {
            if (task.Brief?.InvestigationStrategy == AgentInvestigationStrategy.ContentDiscovery &&
                step.EvidenceRequirement == AgentEvidenceRequirement.Timeline)
            {
                return AgentPlanValidationResult.Invalid(
                    $"Plan step '{step.Title}' discovers content, so timeline geometry alone cannot prove the selected material. Require frames, audio, transcript or all evidence.");
            }
            if (task.Brief?.InvestigationStrategy == AgentInvestigationStrategy.ContentDiscovery &&
                Satisfies(currentSourceCapabilities, AgentEvidenceRequirement.All) &&
                step.EvidenceRequirement != AgentEvidenceRequirement.All)
            {
                return AgentPlanValidationResult.Invalid(
                    $"Plan step '{step.Title}' performs content discovery on a source where frames, audio and transcript are available, so it must require all channels instead of selectively downgrading evidence.",
                    "plan_evidence_required");
            }
            if (!registry.TryGet(step.ExpectedEditingTool!, out var tool) ||
                tool is null ||
                tool.Descriptor.Access != AgentToolAccess.Editing)
            {
                return AgentPlanValidationResult.Invalid(
                    $"Plan step '{step.Title}' names an unavailable editing action '{step.ExpectedEditingTool}'.");
            }
            if (step.ExpectedEditingArguments is not { ValueKind: JsonValueKind.Object } arguments)
            {
                return AgentPlanValidationResult.Invalid(
                    $"Plan step '{step.Title}' has no exact editing arguments.");
            }
            if (!registry.TryValidateArguments(step.ExpectedEditingTool!, arguments, out var argumentError))
            {
                return AgentPlanValidationResult.Invalid(
                    $"Plan step '{step.Title}' has invalid editing arguments: {argumentError}");
            }
            if (ValidateRippleDeleteArguments(
                    step.ExpectedEditingTool!,
                    arguments,
                    task.Evidence,
                    task.SourceSequenceId) is { } coordinateError)
            {
                return AgentPlanValidationResult.Invalid(
                    $"Plan step '{step.Title}' has unsafe editing coordinates: {coordinateError}");
            }

            var signature = AgentActionApproval.CreateSignature(
                step.ExpectedEditingTool!,
                arguments);
            if (!signatures.Add(signature))
            {
                return AgentPlanValidationResult.Invalid(
                    $"Plan step '{step.Title}' duplicates another exact editing action.");
            }
            if (step.EvidenceObservationSequences.IsDefaultOrEmpty)
            {
                return AgentPlanValidationResult.Invalid(
                    $"Plan step '{step.Title}' has no evidence observation references.");
            }

            var requestedSequences = step.EvidenceObservationSequences.Distinct().ToArray();
            var referencedObservations = observations
                .Where(item => requestedSequences.Contains(item.Sequence))
                .ToArray();
            if (referencedObservations.Any(item =>
                    item.Status != AgentToolResultStatus.Succeeded))
            {
                return AgentPlanValidationResult.Invalid(
                    $"Plan step '{step.Title}' references an unsuccessful observation.",
                    "plan_evidence_required");
            }
            var evidence = task.Evidence
                .Where(item => requestedSequences.Contains(item.Sequence))
                .ToArray();
            if (evidence.Length != requestedSequences.Length)
            {
                return AgentPlanValidationResult.Invalid(
                    $"Plan step '{step.Title}' has no typed evidence records for all referenced observations.",
                    "plan_evidence_required");
            }
            if (task.SourceSequenceRevision is { } expectedRevision &&
                evidence.Any(item => item.SourceRevision != expectedRevision))
            {
                return AgentPlanValidationResult.Invalid(
                    $"Plan step '{step.Title}' references evidence from an outdated source revision.",
                    "plan_evidence_required");
            }

            var combinedCapabilities = evidence.Aggregate(
                AgentEvidenceCapabilities.None,
                (current, item) => current | item.Capabilities);
            if (!Satisfies(combinedCapabilities, step.EvidenceRequirement))
            {
                return AgentPlanValidationResult.Invalid(
                    $"Plan step '{step.Title}' needs successful {step.EvidenceRequirement.ToString().ToLowerInvariant()} evidence. The referenced observations do not provide that capability.",
                    "plan_evidence_required");
            }

            if (task.Brief?.InvestigationStrategy == AgentInvestigationStrategy.ContentDiscovery &&
                ValidateContentDiscoveryEvidence(
                    step.ExpectedEditingTool!,
                    arguments,
                    evidence,
                    task.Evidence,
                    task.SourceSequenceId,
                    step.EvidenceRequirement) is { } boundaryError)
            {
                return AgentPlanValidationResult.Invalid(
                    $"Plan step '{step.Title}' {boundaryError}",
                    "plan_evidence_required");
            }
        }

        return AgentPlanValidationResult.Valid;
    }

    private static string? ValidateContentDiscoveryEvidence(
        string toolName,
        JsonElement arguments,
        IReadOnlyCollection<AgentEvidenceRecord> referencedEvidence,
        ImmutableArray<AgentEvidenceRecord> allEvidence,
        Guid sourceSequenceId,
        AgentEvidenceRequirement requirement)
    {
        var ranges = ReadRippleDeleteRanges(toolName, arguments);
        if (ranges.Count == 0)
        {
            return null;
        }

        var knownDuration = allEvidence
            .Where(item =>
                item.TargetId == sourceSequenceId &&
                item.ToolName.Equals(
                    "inspect_content_overview",
                    StringComparison.OrdinalIgnoreCase) &&
                item.EndSeconds is not null)
            .Select(item => item.EndSeconds!.Value)
            .DefaultIfEmpty(0)
            .Max();
        var boundaryEvidence = referencedEvidence
            .Where(item => item.TargetId == sourceSequenceId &&
                           item.ToolName.Equals("inspect_boundary", StringComparison.OrdinalIgnoreCase) &&
                           item.StartSeconds is not null &&
                           item.EndSeconds is not null)
            .ToArray();

        foreach (var range in ranges)
        {
            var rangedEvidence = referencedEvidence
                .Where(item => item.TargetId == sourceSequenceId &&
                               !item.ToolName.Equals(
                                   "inspect_boundary",
                                   StringComparison.OrdinalIgnoreCase) &&
                               item.StartSeconds is not null &&
                               item.EndSeconds is not null)
                .ToArray();
            if (!CoversRangeForRequirement(
                    rangedEvidence,
                    range.Start,
                    range.End,
                    requirement))
            {
                return $"requires referenced {requirement.ToString().ToLowerInvariant()} evidence measured across the complete source deletion range {range.Start:0.###}–{range.End:0.###}s; partial, wrong-target or unrelated evidence cannot approve unobserved material.";
            }
            if (range.Start > 0.25 &&
                !CoversBoundary(boundaryEvidence, range.Start))
            {
                return $"requires successful referenced inspect_boundary evidence centered within 1s of the internal start boundary at {range.Start:0.###}s and no wider than 30s; a coarse or off-center probe cannot approve that editing coordinate.";
            }
            if ((knownDuration <= 0 || range.End < knownDuration - 0.25) &&
                !CoversBoundary(boundaryEvidence, range.End))
            {
                return $"requires successful referenced inspect_boundary evidence centered within 1s of the internal end boundary at {range.End:0.###}s and no wider than 30s; a coarse or off-center probe cannot approve that editing coordinate.";
            }
        }

        return null;
    }

    private static bool CoversRangeForRequirement(
        IReadOnlyCollection<AgentEvidenceRecord> evidence,
        double start,
        double end,
        AgentEvidenceRequirement requirement)
    {
        var requiredCapabilities = requirement switch
        {
            AgentEvidenceRequirement.Timeline => [AgentEvidenceCapabilities.Timeline],
            AgentEvidenceRequirement.Frames => [AgentEvidenceCapabilities.Frames],
            AgentEvidenceRequirement.Audio => [AgentEvidenceCapabilities.Audio],
            AgentEvidenceRequirement.Transcript => [AgentEvidenceCapabilities.Transcript],
            AgentEvidenceRequirement.All =>
            [
                AgentEvidenceCapabilities.Frames,
                AgentEvidenceCapabilities.Audio,
                AgentEvidenceCapabilities.Transcript
            ],
            _ => Array.Empty<AgentEvidenceCapabilities>()
        };

        return requiredCapabilities.Length > 0 &&
               requiredCapabilities.All(capability =>
                   CoversRange(evidence, capability, start, end));
    }

    private static bool CoversRange(
        IEnumerable<AgentEvidenceRecord> evidence,
        AgentEvidenceCapabilities capability,
        double start,
        double end)
    {
        const double toleranceSeconds = 0.25;
        var cursor = start;
        foreach (var item in evidence
                     .Where(item => (item.Capabilities & capability) == capability)
                     .Select(item => (
                         Start: item.StartSeconds!.Value,
                         End: item.EndSeconds!.Value))
                     .Where(interval => interval.End >= start - toleranceSeconds &&
                                        interval.Start <= end + toleranceSeconds)
                     .OrderBy(interval => interval.Start)
                     .ThenBy(interval => interval.End))
        {
            if (item.Start > cursor + toleranceSeconds)
            {
                return false;
            }

            cursor = Math.Max(cursor, item.End);
            if (cursor >= end - toleranceSeconds)
            {
                return true;
            }
        }

        return cursor >= end - toleranceSeconds;
    }

    private static string? ValidateRippleDeleteArguments(
        string toolName,
        JsonElement arguments,
        ImmutableArray<AgentEvidenceRecord> evidence,
        Guid sourceSequenceId)
    {
        if (!toolName.Equals("ripple_delete_range", StringComparison.OrdinalIgnoreCase) &&
            !toolName.Equals("ripple_delete_ranges", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var ranges = ReadRippleDeleteRanges(toolName, arguments)
            .OrderBy(range => range.Start)
            .ThenBy(range => range.End)
            .ToArray();
        if (ranges.Length == 0)
        {
            return "no valid deletion range was supplied.";
        }
        if (ranges.Any(range =>
                !double.IsFinite(range.Start) ||
                !double.IsFinite(range.End) ||
                range.Start < 0 ||
                range.End <= range.Start))
        {
            return "every deletion range must be finite, non-negative and have end greater than start.";
        }
        for (var index = 1; index < ranges.Length; index++)
        {
            if (ranges[index].Start <= ranges[index - 1].End + 0.001)
            {
                return "overlapping or touching deletion ranges must be merged before approval.";
            }
        }

        var knownDuration = evidence
            .Where(item =>
                item.TargetId == sourceSequenceId &&
                item.ToolName.Equals(
                    "inspect_content_overview",
                    StringComparison.OrdinalIgnoreCase) &&
                item.EndSeconds is not null)
            .Select(item => item.EndSeconds!.Value)
            .DefaultIfEmpty(0)
            .Max();
        if (knownDuration > 0 && ranges.Any(range => range.End > knownDuration + 0.001))
        {
            return $"a deletion range extends beyond the measured source duration of {knownDuration:0.###}s.";
        }

        return null;
    }

    private static bool CoversBoundary(
        IEnumerable<AgentEvidenceRecord> evidence,
        double coordinate)
        => evidence.Any(item =>
        {
            var start = item.StartSeconds!.Value;
            var end = item.EndSeconds!.Value;
            var center = item.BoundarySeconds ?? start + (end - start) / 2;
            return end - start <= 30.001 &&
                   Math.Abs(center - coordinate) <= 1.001;
        });

    private static IReadOnlyList<(double Start, double End)> ReadRippleDeleteRanges(
        string toolName,
        JsonElement arguments)
    {
        if (toolName.Equals("ripple_delete_range", StringComparison.OrdinalIgnoreCase))
        {
            return TryReadRange(arguments, out var range)
                ? [range]
                : [];
        }
        if (!toolName.Equals("ripple_delete_ranges", StringComparison.OrdinalIgnoreCase) ||
            !arguments.TryGetProperty("ranges", out var ranges) ||
            ranges.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return ranges.EnumerateArray()
            .Select(item => TryReadRange(item, out var range)
                ? range
                : ((double Start, double End)?)null)
            .Where(item => item is not null)
            .Select(item => item!.Value)
            .ToArray();
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
            !end.TryGetDouble(out var endSeconds))
        {
            return false;
        }

        range = (startSeconds, endSeconds);
        return true;
    }

    private static bool Satisfies(
        AgentEvidenceCapabilities capabilities,
        AgentEvidenceRequirement requirement)
    {
        var required = requirement switch
        {
            AgentEvidenceRequirement.Timeline => AgentEvidenceCapabilities.Timeline,
            AgentEvidenceRequirement.Frames => AgentEvidenceCapabilities.Frames,
            AgentEvidenceRequirement.Audio => AgentEvidenceCapabilities.Audio,
            AgentEvidenceRequirement.Transcript => AgentEvidenceCapabilities.Transcript,
            AgentEvidenceRequirement.All =>
                AgentEvidenceCapabilities.Frames |
                AgentEvidenceCapabilities.Audio |
                AgentEvidenceCapabilities.Transcript,
            _ => AgentEvidenceCapabilities.None
        };
        return required != AgentEvidenceCapabilities.None &&
               (capabilities & required) == required;
    }
}
