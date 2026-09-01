using KadrStudio.Application.Automation.Agent.Persistence;

namespace KadrStudio.Application.Automation.Agent.Recovery;

public sealed class AgentRecoveryService
{
    public AgentTaskState Reconcile(AgentTaskState persisted, int persistenceFormatVersion)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        if (persisted.IsTerminal) return persisted;
        if (persistenceFormatVersion >= AgentTaskPersistenceEnvelope.CurrentFormatVersion &&
            Enum.IsDefined(persisted.Phase))
            return persisted with { Journal = persisted.SafeJournal, Checkpoint = persisted.Checkpoint?.Normalize() };

        var now = DateTimeOffset.UtcNow;
        return persisted with
        {
            Phase = AgentTaskPhase.Indexing,
            DraftSequenceId = null,
            CompletionSummary = null,
            FailureMessage = null,
            RevisionFeedback = string.Empty,
            Checkpoint = null,
            UpdatedAt = now,
            Journal = persisted.SafeJournal.Add(new AgentJournalEntry(
                Guid.NewGuid(), now, AgentJournalKind.PhaseChanged,
                "Legacy task restarted in Kadr AI Editor v2.1; the original request was preserved."))
        };
    }
}
