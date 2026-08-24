using System.Collections.Immutable;
using KadrStudio.Application.Automation.Agent;
using KadrStudio.Application.Automation.Agent.Runtime;
using KadrStudio.Application.Automation.Agent.Tools;
using KadrStudio.Application.Automation.Agent.Verification;
using KadrStudio.Core.Domain;

namespace KadrStudio.Core.Tests;

public sealed class AgentVerificationEngineTests
{
    [Fact]
    public void Post_edit_probe_must_cover_the_exact_requested_window_unless_clamped_by_draft_edge()
    {
        const long sourceRevision = 7;
        var sourceId = Guid.NewGuid();
        var draftId = Guid.NewGuid();
        var orchestrator = new AiAgentOrchestrator();
        orchestrator.StartTask(
            Guid.NewGuid(),
            sourceId,
            "Удалить доказанный диапазон.",
            sourceSequenceRevision: sourceRevision);
        orchestrator.SetTaskBrief(AgentTaskBrief.Create(
            AgentTaskKind.Edit,
            "Удалить диапазон",
            "Активная последовательность"));
        orchestrator.BeginPlanning();
        orchestrator.PublishPlan(AgentPlanDraft.Create(
            "Удалить диапазон",
            "Выполнить одну точную склейку",
            [],
            [new AgentPlanStepDraft(
                "Удалить",
                "Удалить 10–20 секунд",
                "ripple_delete_range",
                [1],
                AgentToolJson.ToElement(new
                {
                    start_seconds = 10d,
                    end_seconds = 20d
                }),
                AgentEvidenceRequirement.Frames)]));
        orchestrator.ApprovePlan();
        var task = orchestrator.BeginExecution(draftId);
        task = orchestrator.BeginVerification();
        var plan = task.Plan!;
        var step = Assert.Single(plan.Steps);
        var arguments = step.ExpectedEditingArguments!.Value;
        var checkpoint = new AgentDraftCheckpoint(
            task.Id,
            plan.Id,
            plan.Version,
            AgentPlanFingerprint.Create(plan),
            sourceId,
            sourceRevision,
            AgentDraftExecutionStatus.Verifying,
            [new AgentStepReceipt(
                step.Id,
                step.Order,
                step.ExpectedEditingTool!,
                AgentPlanFingerprint.CreateArguments(step.ExpectedEditingTool!, arguments),
                0,
                1,
                "Committed",
                DateTimeOffset.UtcNow)],
            DateTimeOffset.UtcNow);

        var observations = ImmutableArray.Create(
            Observation(
                1,
                "inspect_agent_edits",
                AgentEvidenceCapabilities.EditLog,
                new { }),
            Observation(
                2,
                "inspect_timeline_integrity",
                AgentEvidenceCapabilities.Integrity,
                new { sequence_id = draftId, overlap_count = 0, link_issue_count = 0 }),
            Observation(
                3,
                "compare_sequences",
                AgentEvidenceCapabilities.SequenceDiff,
                new
                {
                    source_sequence_id = sourceId,
                    draft_sequence_id = draftId,
                    source_revision = sourceRevision,
                    draft_duration_seconds = 100d
                }),
            // Requested post-edit window is 5–15s. Ending it at the junction is
            // not a valid clamp because the draft continues to 100s.
            Observation(
                4,
                "inspect_boundary",
                AgentEvidenceCapabilities.Frames,
                new
                {
                    sequence_id = draftId,
                    query = "Постмонтажная проверка утверждённого шага 1",
                    start_seconds = 5d,
                    end_seconds = 10d
                }));

        var result = new AgentVerificationEngine().Verify(
            task,
            checkpoint,
            observations);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue =>
            issue.Contains("No frame post-edit observation", StringComparison.Ordinal));
    }

    private static AgentModelObservation Observation(
        int sequence,
        string toolName,
        AgentEvidenceCapabilities capabilities,
        object data)
        => new(
            sequence,
            toolName,
            AgentToolResultStatus.Succeeded,
            "Measured",
            AgentToolJson.ToElement(data),
            null,
            capabilities);
}
