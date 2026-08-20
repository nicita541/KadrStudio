using System.Collections.Immutable;

namespace KadrStudio.Application.Automation.Agent;

public sealed record AgentTaskState(
    Guid Id,
    Guid ProjectId,
    Guid SourceSequenceId,
    Guid? ConversationId,
    string UserRequest,
    AgentTaskPhase Phase,
    AgentTaskPhase? ResumePhase,
    AgentPlan? Plan,
    ImmutableArray<AgentQuestion> Questions,
    ImmutableArray<AgentJournalEntry> Journal,
    Guid? DraftSequenceId,
    string? CompletionSummary,
    string? FailureMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long? SourceSequenceRevision = null)
{
    public bool IsTerminal =>
        Phase is AgentTaskPhase.Completed or AgentTaskPhase.Failed or AgentTaskPhase.Stopped;

    public bool HasOpenQuestion =>
        Questions.Any(question => !question.IsAnswered);

    public bool HasApprovedPlan =>
        Plan?.ApprovedAt is not null;

    // The future UI can bind to this property:
    // during agent execution/verification the draft is visible, but user editing is locked.
    public bool IsDraftReadOnlyForUser =>
        DraftSequenceId is not null &&
        (
            Phase is AgentTaskPhase.Executing or AgentTaskPhase.Verifying ||
            Phase == AgentTaskPhase.WaitingForUserInput &&
            ResumePhase is AgentTaskPhase.Executing or AgentTaskPhase.Verifying
        );
}
