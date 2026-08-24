using System.Collections.Immutable;
using System.Text.Json;
using KadrStudio.Application.Automation.Agent;
using KadrStudio.Application.Automation.Agent.Execution;
using KadrStudio.Application.Automation.Agent.Persistence;
using KadrStudio.Application.Automation.Agent.Recovery;
using KadrStudio.Application.Automation.Agent.Tools;
using KadrStudio.Core.Domain;

namespace KadrStudio.Core.Tests;

public sealed class AgentRecoveryServiceTests
{
    [Fact]
    public void Persistence_envelope_preserves_content_discovery_strategy()
    {
        var orchestrator = new AiAgentOrchestrator();
        orchestrator.StartTask(Guid.NewGuid(), Guid.NewGuid(), "Найти смысловой блок.");
        var task = orchestrator.SetTaskBrief(AgentTaskBrief.Create(
            AgentTaskKind.Edit,
            "Найти и удалить блок",
            "Активная последовательность",
            investigationStrategy: AgentInvestigationStrategy.ContentDiscovery));

        var json = JsonSerializer.Serialize(AgentTaskPersistenceEnvelope.Create(task));
        var restored = JsonSerializer.Deserialize<AgentTaskPersistenceEnvelope>(json);

        Assert.NotNull(restored);
        Assert.Equal(
            AgentInvestigationStrategy.ContentDiscovery,
            restored!.Task.Brief!.InvestigationStrategy);
    }

    [Fact]
    public void Legacy_active_task_is_interrupted_without_guessing_completed_edits()
    {
        var (task, store) = CreateExecutingTask();

        var recovered = new AgentRecoveryService(store).Reconcile(task, 1);

        Assert.Equal(AgentTaskPhase.Interrupted, recovered.Phase);
        Assert.Equal(task.DraftSequenceId, recovered.DraftSequenceId);
        Assert.Contains("не имеет безопасных checkpoints", recovered.FailureMessage);
    }

    [Theory]
    [InlineData(AgentDraftExecutionStatus.Executing, AgentTaskPhase.Executing)]
    [InlineData(AgentDraftExecutionStatus.Verifying, AgentTaskPhase.Verifying)]
    [InlineData(AgentDraftExecutionStatus.Completed, AgentTaskPhase.Completed)]
    public void Valid_checkpoint_restores_the_durable_phase(
        AgentDraftExecutionStatus status,
        AgentTaskPhase expectedPhase)
    {
        var (task, store) = CreateExecutingTask(status);

        var recovered = new AgentRecoveryService(store).Reconcile(task, 2);

        Assert.Equal(expectedPhase, recovered.Phase);
        Assert.NotEqual(AgentTaskPhase.Interrupted, recovered.Phase);
    }

    [Fact]
    public void Changed_source_revision_interrupts_and_preserves_draft_identity()
    {
        var (task, store) = CreateExecutingTask();
        store.ActualSourceRevision++;

        var recovered = new AgentRecoveryService(store).Reconcile(task, 2);

        Assert.Equal(AgentTaskPhase.Interrupted, recovered.Phase);
        Assert.Equal(task.DraftSequenceId, recovered.DraftSequenceId);
        Assert.Contains("не совпадают", recovered.FailureMessage);
    }

    [Fact]
    public void Draft_with_wrong_parent_is_interrupted()
    {
        var (task, store) = CreateExecutingTask();
        store.IsDraftIdentityValid = false;

        var recovered = new AgentRecoveryService(store).Reconcile(task, 2);

        Assert.Equal(AgentTaskPhase.Interrupted, recovered.Phase);
        Assert.Contains("не совпадают", recovered.FailureMessage);
    }

    [Fact]
    public void Changed_step_description_invalidates_the_checkpoint_fingerprint()
    {
        var (task, store) = CreateExecutingTask();
        var changedPlan = task.Plan! with
        {
            Steps = task.Plan.Steps
                .Select(step => step with { Description = step.Description + " изменено" })
                .ToImmutableArray()
        };

        var recovered = new AgentRecoveryService(store).Reconcile(
            task with { Plan = changedPlan },
            2);

        Assert.Equal(AgentTaskPhase.Interrupted, recovered.Phase);
        Assert.Contains("не совпадают", recovered.FailureMessage);
    }

    private static (AgentTaskState Task, RecoveryCheckpointStore Store) CreateExecutingTask(
        AgentDraftExecutionStatus status = AgentDraftExecutionStatus.Executing)
    {
        const long sourceRevision = 11;
        var sourceId = Guid.NewGuid();
        var draftId = Guid.NewGuid();
        var orchestrator = new AiAgentOrchestrator();
        orchestrator.StartTask(
            Guid.NewGuid(),
            sourceId,
            "Удалить опенинг.",
            sourceSequenceRevision: sourceRevision);
        orchestrator.SetTaskBrief(AgentTaskBrief.Create(
            AgentTaskKind.Edit,
            "Удалить опенинг",
            "Активная последовательность"));
        orchestrator.BeginPlanning();
        orchestrator.PublishPlan(AgentPlanDraft.Create(
            "Удалить опенинг",
            "Один точный шаг",
            [],
            [new AgentPlanStepDraft(
                "Удалить",
                "Удалить диапазон",
                "ripple_delete_range",
                [1],
                AgentToolJson.ToElement(new
                {
                    start_seconds = 0,
                    end_seconds = 10
                }),
                AgentEvidenceRequirement.Frames)]));
        orchestrator.ApprovePlan();
        orchestrator.BeginExecution(draftId);
        var task = orchestrator.CurrentTask!;
        var plan = task.Plan!;
        var checkpoint = new AgentDraftCheckpoint(
            task.Id,
            plan.Id,
            plan.Version,
            AgentPlanFingerprint.Create(plan),
            sourceId,
            sourceRevision,
            status,
            [],
            DateTimeOffset.UtcNow);
        return (task, new RecoveryCheckpointStore(
            draftId,
            sourceId,
            sourceRevision,
            checkpoint));
    }

    private sealed class RecoveryCheckpointStore(
        Guid draftId,
        Guid sourceId,
        long sourceRevision,
        AgentDraftCheckpoint checkpoint) : IAgentCheckpointStore
    {
        public long ActualSourceRevision { get; set; } = sourceRevision;
        public bool IsDraftIdentityValid { get; set; } = true;

        public AgentDraftCheckpoint? Read(Guid draftSequenceId)
            => draftSequenceId == draftId ? checkpoint : null;

        public long? ReadSequenceRevision(Guid sequenceId)
            => sequenceId == sourceId ? ActualSourceRevision : null;

        public bool IsAgentDraft(Guid draftSequenceId, Guid sourceSequenceId)
            => IsDraftIdentityValid && draftSequenceId == draftId && sourceSequenceId == sourceId;

        public bool SetStatus(AgentTaskState task, AgentDraftExecutionStatus status)
            => throw new NotSupportedException();
    }
}
