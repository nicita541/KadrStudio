using System.Collections.Immutable;
using System.Text.Json;
using KadrStudio.Application.Automation.Agent.Runtime;
using KadrStudio.Application.Automation.Agent.Tools;
using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Automation.Agent.Verification;

public sealed record AgentDeterministicVerificationResult(
    bool IsValid,
    string Summary,
    ImmutableArray<string> Issues);

/// <summary>
/// Owns the pass/fail decision for an Agent Draft. Model output is deliberately
/// excluded from this policy.
/// </summary>
public sealed class AgentVerificationEngine
{
    public AgentDeterministicVerificationResult Verify(
        AgentTaskState task,
        AgentDraftCheckpoint? checkpoint,
        ImmutableArray<AgentModelObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(task);
        var issues = ImmutableArray.CreateBuilder<string>();
        var plan = task.Plan;
        if (plan is null || task.DraftSequenceId is null)
        {
            issues.Add("The task has no approved plan or Agent Draft.");
            return Result(issues);
        }
        if (checkpoint is null)
        {
            issues.Add("Agent Draft has no persistent execution checkpoint.");
            return Result(issues);
        }
        if (checkpoint.Status != AgentDraftExecutionStatus.Verifying)
        {
            issues.Add("Agent Draft checkpoint is not in the deterministic verification phase.");
        }
        if (checkpoint.TaskId != task.Id ||
            checkpoint.PlanId != plan.Id ||
            checkpoint.PlanVersion != plan.Version ||
            checkpoint.SourceSequenceId != task.SourceSequenceId ||
            !string.Equals(
                checkpoint.PlanFingerprint,
                AgentPlanFingerprint.Create(plan),
                StringComparison.Ordinal))
        {
            issues.Add("Agent Draft checkpoint does not match the approved task and plan.");
        }
        if (task.SourceSequenceRevision is { } expectedSourceRevision &&
            checkpoint.SourceSequenceRevision != expectedSourceRevision)
        {
            issues.Add("Checkpoint was created from a different source revision.");
        }

        ValidateReceipts(plan, checkpoint, issues);
        ValidateObservation(
            observations,
            "inspect_agent_edits",
            AgentEvidenceCapabilities.EditLog,
            issues);
        var integrity = ValidateObservation(
            observations,
            "inspect_timeline_integrity",
            AgentEvidenceCapabilities.Integrity,
            issues);
        if (integrity?.Data is { ValueKind: JsonValueKind.Object } integrityData)
        {
            RequireZero(integrityData, "overlap_count", "Timeline contains overlapping clips.", issues);
            RequireZero(integrityData, "link_issue_count", "Linked clips are not synchronized.", issues);
            RequireSequence(integrityData, "sequence_id", task.DraftSequenceId.Value,
                "Timeline integrity was measured on a different sequence.", issues);
        }

        var comparison = ValidateObservation(
            observations,
            "compare_sequences",
            AgentEvidenceCapabilities.SequenceDiff,
            issues);
        if (comparison?.Data is { ValueKind: JsonValueKind.Object } comparisonData)
        {
            RequireSequence(comparisonData, "source_sequence_id", task.SourceSequenceId,
                "Sequence comparison used a different source.", issues);
            RequireSequence(comparisonData, "draft_sequence_id", task.DraftSequenceId.Value,
                "Sequence comparison used a different Agent Draft.", issues);
            if (task.SourceSequenceRevision is { } expected &&
                (!comparisonData.TryGetProperty("source_revision", out var revision) ||
                 !revision.TryGetInt64(out var actual) || actual != expected))
            {
                issues.Add("Source sequence changed after the plan was approved.");
            }
        }

        var draftDuration = comparison?.Data is { ValueKind: JsonValueKind.Object } durationData
            ? TryReadDouble(durationData, "draft_duration_seconds")
            : null;
        if (draftDuration is null || !double.IsFinite(draftDuration.Value) || draftDuration < 0)
        {
            issues.Add("Sequence comparison did not report a valid Agent Draft duration.");
        }
        else
        {
            ValidateContentProbes(
                plan,
                task.DraftSequenceId.Value,
                draftDuration.Value,
                observations,
                issues);
        }

        return Result(issues);
    }

    private static void ValidateContentProbes(
        AgentPlan plan,
        Guid draftSequenceId,
        double draftDuration,
        ImmutableArray<AgentModelObservation> observations,
        ImmutableArray<string>.Builder issues)
    {
        var expected = AgentVerificationProbePlanner.Create(plan);
        if (expected.IsDefaultOrEmpty)
        {
            return;
        }

        var actual = observations
            .Where(item =>
                string.Equals(item.ToolName, "inspect_boundary", StringComparison.OrdinalIgnoreCase) &&
                item.Status == AgentToolResultStatus.Succeeded &&
                item.Data is { ValueKind: JsonValueKind.Object } data &&
                TryReadGuid(data, "sequence_id") == draftSequenceId &&
                TryReadString(data, "query").StartsWith(
                    "Постмонтажная проверка",
                    StringComparison.Ordinal))
            .ToArray();

        foreach (var probe in expected)
        {
            var observation = actual.FirstOrDefault(item =>
                IsProbeForBoundary(
                    item,
                    probe.DraftBoundarySeconds,
                    probe.WindowSeconds,
                    draftDuration) &&
                (item.EvidenceCapabilities & probe.RequiredCapabilities) ==
                probe.RequiredCapabilities);
            if (observation is null)
            {
                issues.Add(
                    $"No {ChannelName(probe.RequiredCapabilities)} post-edit observation was recorded " +
                    $"for approved step {probe.StepOrder}.");
                continue;
            }

            if (probe.RequiredCapabilities == AgentEvidenceCapabilities.Transcript)
            {
                ValidateTranscriptContinuity(
                    observation,
                    probe.DraftBoundarySeconds,
                    probe.StepOrder,
                    issues);
            }
        }
    }

    private static bool IsProbeForBoundary(
        AgentModelObservation observation,
        double expectedBoundary,
        double window,
        double draftDuration)
    {
        if (observation.Data is not { ValueKind: JsonValueKind.Object } data ||
            !data.TryGetProperty("start_seconds", out var startValue) ||
            !startValue.TryGetDouble(out var start) ||
            !data.TryGetProperty("end_seconds", out var endValue) ||
            !endValue.TryGetDouble(out var end))
        {
            return false;
        }

        var expectedStart = Math.Max(0, expectedBoundary - window);
        var expectedEnd = Math.Min(draftDuration, expectedBoundary + window);
        return end > start &&
               Math.Abs(start - expectedStart) <= 0.25 &&
               Math.Abs(end - expectedEnd) <= 0.25 &&
               start <= expectedBoundary + 0.25 &&
               end >= expectedBoundary - 0.25;
    }

    private static void ValidateTranscriptContinuity(
        AgentModelObservation observation,
        double boundary,
        int stepOrder,
        ImmutableArray<string>.Builder issues)
    {
        if ((observation.EvidenceCapabilities & AgentEvidenceCapabilities.Transcript) == 0 ||
            observation.Data is not { ValueKind: JsonValueKind.Object } data ||
            !data.TryGetProperty("analyses", out var analyses) ||
            analyses.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var analysis in analyses.EnumerateArray())
        {
            if (!analysis.TryGetProperty("observation", out var measured) ||
                measured.ValueKind != JsonValueKind.Object ||
                !measured.TryGetProperty("transcript", out var transcript) ||
                transcript.ValueKind != JsonValueKind.Object ||
                !transcript.TryGetProperty("cues", out var cues) ||
                cues.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var timelineStart = TryReadDouble(analysis, "timeline_start_seconds");
            var sourceStart = TryReadDouble(analysis, "source_start_seconds");
            if (timelineStart is null || sourceStart is null)
            {
                continue;
            }

            if (cues.EnumerateArray().Any(cue =>
                    TryReadDouble(cue, "start_seconds") is { } cueStart &&
                    TryReadDouble(cue, "end_seconds") is { } cueEnd &&
                    timelineStart + cueStart - sourceStart < boundary &&
                    timelineStart + cueEnd - sourceStart > boundary))
            {
                issues.Add($"A transcript cue crosses the new edit boundary for step {stepOrder}; text continuity is not proven.");
                return;
            }
        }
    }

    private static void ValidateReceipts(
        AgentPlan plan,
        AgentDraftCheckpoint checkpoint,
        ImmutableArray<string>.Builder issues)
    {
        var steps = plan.Steps
            .Where(step => !string.IsNullOrWhiteSpace(step.ExpectedEditingTool))
            .OrderBy(step => step.Order)
            .ToArray();
        var receipts = checkpoint.Receipts.OrderBy(receipt => receipt.Order).ToArray();
        if (receipts.Length != steps.Length ||
            receipts.Select(receipt => receipt.StepId).Distinct().Count() != receipts.Length)
        {
            issues.Add("Persistent edit receipts do not match the number of approved actions.");
            return;
        }

        for (var index = 0; index < steps.Length; index++)
        {
            var step = steps[index];
            var receipt = receipts[index];
            if (step.ExpectedEditingArguments is not { ValueKind: JsonValueKind.Object } arguments ||
                receipt.StepId != step.Id ||
                receipt.Order != step.Order ||
                !string.Equals(receipt.ToolName, step.ExpectedEditingTool, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    receipt.ArgumentsFingerprint,
                    AgentPlanFingerprint.CreateArguments(step.ExpectedEditingTool!, arguments),
                    StringComparison.Ordinal))
            {
                issues.Add($"Receipt for approved step {step.Order} does not match the plan.");
                return;
            }
            if (receipt.AfterDraftRevision <= receipt.BeforeDraftRevision ||
                index > 0 && receipt.BeforeDraftRevision != receipts[index - 1].AfterDraftRevision)
            {
                issues.Add("Agent step receipts contain a revision gap or invalid revision order.");
                return;
            }
        }
    }

    private static AgentModelObservation? ValidateObservation(
        ImmutableArray<AgentModelObservation> observations,
        string toolName,
        AgentEvidenceCapabilities capability,
        ImmutableArray<string>.Builder issues)
    {
        var observation = observations.LastOrDefault(item =>
            string.Equals(item.ToolName, toolName, StringComparison.OrdinalIgnoreCase));
        if (observation is null ||
            observation.Status != AgentToolResultStatus.Succeeded ||
            (observation.EvidenceCapabilities & capability) != capability)
        {
            issues.Add($"Required deterministic check '{toolName}' did not succeed.");
            return null;
        }
        return observation;
    }

    private static void RequireZero(
        JsonElement data,
        string propertyName,
        string issue,
        ImmutableArray<string>.Builder issues)
    {
        if (!data.TryGetProperty(propertyName, out var value) ||
            !value.TryGetInt32(out var count) || count != 0)
        {
            issues.Add(issue);
        }
    }

    private static void RequireSequence(
        JsonElement data,
        string propertyName,
        Guid expected,
        string issue,
        ImmutableArray<string>.Builder issues)
    {
        if (!data.TryGetProperty(propertyName, out var value) ||
            !value.TryGetGuid(out var actual) || actual != expected)
        {
            issues.Add(issue);
        }
    }

    private static Guid? TryReadGuid(JsonElement data, string propertyName)
        => data.ValueKind == JsonValueKind.Object &&
           data.TryGetProperty(propertyName, out var value) &&
           value.TryGetGuid(out var result)
            ? result
            : null;

    private static string TryReadString(JsonElement data, string propertyName)
        => data.ValueKind == JsonValueKind.Object &&
           data.TryGetProperty(propertyName, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static double? TryReadDouble(JsonElement data, string propertyName)
        => data.ValueKind == JsonValueKind.Object &&
           data.TryGetProperty(propertyName, out var value) &&
           value.TryGetDouble(out var result)
            ? result
            : null;

    private static string ChannelName(AgentEvidenceCapabilities capability)
        => capability switch
        {
            AgentEvidenceCapabilities.Frames => "frame",
            AgentEvidenceCapabilities.Audio => "audio",
            AgentEvidenceCapabilities.Transcript => "transcript",
            _ => "required-channel"
        };

    private static AgentDeterministicVerificationResult Result(
        ImmutableArray<string>.Builder issues)
        => issues.Count == 0
            ? new AgentDeterministicVerificationResult(
                true,
                "Approved actions were committed once; source revision, receipts, timeline integrity and required frame/audio/transcript probes were verified.",
                [])
            : new AgentDeterministicVerificationResult(
                false,
                "Agent Draft failed deterministic verification.",
                issues.ToImmutable());
}
