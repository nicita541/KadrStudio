using KadrStudio.Application.Automation.Agent.Execution;
using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Automation.Agent.Recovery;

public sealed class AgentRecoveryService(IAgentCheckpointStore checkpointStore)
{
    public AgentTaskState Reconcile(
        AgentTaskState persisted,
        int persistenceFormatVersion)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        if (persisted.IsTerminal)
        {
            return persisted;
        }
        if (persistenceFormatVersion < 2)
        {
            return Interrupt(
                persisted,
                "Старая незавершённая задача агента не имеет безопасных checkpoints. Черновик сохранён; создайте новый план.");
        }

        if (persisted.DraftSequenceId is not { } draftId)
        {
            return persisted.Phase is AgentTaskPhase.Executing or AgentTaskPhase.Verifying
                ? Interrupt(persisted, "Agent Draft не найден после перезапуска.")
                : persisted;
        }

        var plan = persisted.Plan;
        var checkpoint = checkpointStore.Read(draftId);
        if (plan is null || checkpoint is null)
        {
            return Interrupt(
                persisted,
                "Agent Draft не содержит checkpoint утверждённого плана.");
        }
        var actualSourceRevision = checkpointStore.ReadSequenceRevision(
            persisted.SourceSequenceId);
        if (checkpoint.TaskId != persisted.Id ||
            checkpoint.PlanId != plan.Id ||
            checkpoint.PlanVersion != plan.Version ||
            checkpoint.SourceSequenceId != persisted.SourceSequenceId ||
            !checkpointStore.IsAgentDraft(draftId, persisted.SourceSequenceId) ||
            actualSourceRevision != checkpoint.SourceSequenceRevision ||
            !string.Equals(
                checkpoint.PlanFingerprint,
                AgentPlanFingerprint.Create(plan),
                StringComparison.Ordinal))
        {
            return Interrupt(
                persisted,
                "Checkpoint, утверждённый план или исходный таймлайн больше не совпадают. Автоматическое продолжение отменено.");
        }

        var approvedPlan = plan.ApprovedAt is null
            ? plan with { ApprovedAt = checkpoint.UpdatedAt }
            : plan;
        return checkpoint.Status switch
        {
            AgentDraftExecutionStatus.Executing => persisted with
            {
                Phase = AgentTaskPhase.Executing,
                ResumePhase = null,
                Plan = approvedPlan,
                SourceSequenceRevision = checkpoint.SourceSequenceRevision,
                FailureMessage = null,
                UpdatedAt = checkpoint.UpdatedAt
            },
            AgentDraftExecutionStatus.Verifying => persisted with
            {
                Phase = AgentTaskPhase.Verifying,
                ResumePhase = null,
                Plan = approvedPlan,
                SourceSequenceRevision = checkpoint.SourceSequenceRevision,
                FailureMessage = null,
                UpdatedAt = checkpoint.UpdatedAt
            },
            AgentDraftExecutionStatus.Completed => persisted with
            {
                Phase = AgentTaskPhase.Completed,
                ResumePhase = null,
                Plan = approvedPlan,
                CompletionSummary = persisted.CompletionSummary ??
                    "Agent Draft был выполнен и проверен до перезапуска.",
                FailureMessage = null,
                UpdatedAt = checkpoint.UpdatedAt
            },
            _ => Interrupt(
                persisted,
                "Выполнение Agent Draft было ранее прервано.")
        };
    }

    private static AgentTaskState Interrupt(
        AgentTaskState task,
        string message)
        => task with
        {
            Phase = AgentTaskPhase.Interrupted,
            ResumePhase = null,
            FailureMessage = message,
            UpdatedAt = DateTimeOffset.UtcNow
        };
}
