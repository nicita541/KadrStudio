using KadrStudio.Application.Automation.Agent;
using KadrStudio.Application.Automation.Agent.Persistence;
using KadrStudio.Application.Automation.Agent.Recovery;
using KadrStudio.Application.Automation.Agent.Workflow;

namespace KadrStudio.Core.Tests;

public sealed class AgentWorkflowV2Tests
{
    [Fact]
    public void Orchestrator_runs_only_editorial_stages_and_reviews_a_draft()
    {
        var clock = new DateTimeOffset(2026, 8, 26, 10, 0, 0, TimeSpan.Zero);
        var orchestrator = new AiAgentOrchestrator(() => clock);
        var sourceId = Guid.NewGuid();
        var task = orchestrator.StartTask(Guid.NewGuid(), sourceId, "remove opening and ending", sourceSequenceRevision: 7);
        Assert.Equal(AgentTaskPhase.Indexing, task.Phase);

        foreach (var phase in new[]
                 {
                     AgentTaskPhase.Directing, AgentTaskPhase.Retrieving, AgentTaskPhase.RoughCut,
                     AgentTaskPhase.BoundaryRefining, AgentTaskPhase.Compiling, AgentTaskPhase.Verifying
                 })
            Assert.Equal(phase, orchestrator.BeginEditorialStage(phase).Phase);

        var checkpoint = new EditorialTaskCheckpoint(
            AgentTaskPhase.Verifying, ["asset"], ["job"], ["artifact"],
            "anime-episode", "remove-named-sections", "evidence", "graph", clock);
        Assert.Equal("graph", orchestrator.SaveCheckpoint(checkpoint).Checkpoint?.GraphFingerprint);

        var draftId = Guid.NewGuid();
        Assert.Equal(AgentTaskPhase.ReviewingDraft,
            orchestrator.ReadyForCompiledDraftReview(draftId, "ready").Phase);
        Assert.Equal(AgentTaskPhase.Accepted, orchestrator.AcceptDraft().Phase);
    }

    [Fact]
    public void Revision_returns_to_directing_without_mutating_source_identity()
    {
        var orchestrator = new AiAgentOrchestrator();
        var sourceId = Guid.NewGuid();
        var task = orchestrator.StartTask(Guid.NewGuid(), sourceId, "remove OP ED", sourceSequenceRevision: 11);
        orchestrator.ReadyForCompiledDraftReview(Guid.NewGuid(), "review");

        var revised = orchestrator.ReviseDraft("keep the preview");

        Assert.Equal(AgentTaskPhase.Directing, revised.Phase);
        Assert.Null(revised.DraftSequenceId);
        Assert.Equal(sourceId, revised.SourceSequenceId);
        Assert.Equal(11, revised.SourceSequenceRevision);
        Assert.Equal("keep the preview", revised.RevisionFeedback);
    }

    [Fact]
    public void Recovery_restarts_legacy_numeric_phase_but_resumes_v2_checkpoint()
    {
        var now = DateTimeOffset.UtcNow;
        var legacy = NewState((AgentTaskPhase)5, now);
        var recovery = new AgentRecoveryService();

        var restarted = recovery.Reconcile(legacy, persistenceFormatVersion: 3);
        Assert.Equal(AgentTaskPhase.Indexing, restarted.Phase);
        Assert.Equal(legacy.UserRequest, restarted.UserRequest);

        var current = NewState(AgentTaskPhase.Compiling, now) with
        {
            Checkpoint = new EditorialTaskCheckpoint(
                AgentTaskPhase.Compiling, [], ["job"], [], "anime-episode", null, "e", "g", now)
        };
        var resumed = recovery.Reconcile(current, AgentTaskPersistenceEnvelope.CurrentFormatVersion);
        Assert.Equal(AgentTaskPhase.Compiling, resumed.Phase);
        Assert.Equal("job", Assert.Single(resumed.Checkpoint!.JobIds));
    }

    [Fact]
    public void Workflow_facade_exposes_no_plan_approval_or_tool_loop_methods()
    {
        var methods = typeof(IAgentWorkflowService).GetMethods().Select(item => item.Name).ToHashSet();
        var expected = new[]
        {
            "add_Changed", "remove_Changed", "get_Current", "StartAsync", "ContinueAsync",
            "AcceptDraftAsync", "ReviseDraftAsync", "DiscardDraftAsync", "CancelAsync"
        }.ToHashSet();
        Assert.True(expected.SetEquals(methods),
            $"Unexpected workflow API: {string.Join(", ", methods.Order())}");
    }

    private static AgentTaskState NewState(AgentTaskPhase phase, DateTimeOffset now)
        => new(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "original request", phase,
            null, null, null, now, now, 1);
}
