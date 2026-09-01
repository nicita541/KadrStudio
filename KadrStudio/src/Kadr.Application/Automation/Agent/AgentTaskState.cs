using System.Collections.Immutable;

namespace KadrStudio.Application.Automation.Agent;

public sealed record AgentTaskState(
    Guid Id,
    Guid ProjectId,
    Guid SourceSequenceId,
    Guid? ConversationId,
    string UserRequest,
    AgentTaskPhase Phase,
    Guid? DraftSequenceId,
    string? CompletionSummary,
    string? FailureMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long? SourceSequenceRevision = null,
    string RevisionFeedback = "",
    ImmutableArray<AgentJournalEntry> Journal = default,
    EditorialTaskCheckpoint? Checkpoint = null,
    ImmutableArray<Guid> TargetSourceIds = default)
{
    public bool IsTerminal => Phase is AgentTaskPhase.Accepted or AgentTaskPhase.Discarded or
        AgentTaskPhase.Failed or AgentTaskPhase.Cancelled;

    public bool IsDraftReadOnlyForUser => DraftSequenceId is not null && Phase == AgentTaskPhase.ReviewingDraft;

    public ImmutableArray<AgentJournalEntry> SafeJournal => Journal.IsDefault ? [] : Journal;
    public ImmutableArray<Guid> SafeTargetSourceIds =>
        TargetSourceIds.IsDefault ? [] : TargetSourceIds.Distinct().ToImmutableArray();
}
