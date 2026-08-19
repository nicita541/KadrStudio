using System.Collections.Immutable;

namespace KadrStudio.Application.Automation.Agent;

public enum AgentJournalKind
{
    TaskStarted,
    PhaseChanged,
    QuestionAsked,
    QuestionAnswered,
    PlanPublished,
    PlanRevised,
    PlanApproved,
    ExecutionStarted,
    Progress,
    VerificationStarted,
    TaskCompleted,
    TaskFailed,
    TaskStopped
}

public enum AgentPlanRevisionSource
{
    Agent,
    User
}

public sealed record AgentJournalEntry(
    Guid Id,
    DateTimeOffset CreatedAt,
    AgentJournalKind Kind,
    string Message);

public sealed record AgentQuestion(
    Guid Id,
    string Prompt,
    string? Context,
    DateTimeOffset AskedAt,
    DateTimeOffset? AnsweredAt = null,
    string? Answer = null)
{
    public bool IsAnswered => AnsweredAt is not null;
}

public sealed record AgentPlanStepDraft(
    string Title,
    string Description);

public sealed record AgentPlanStep(
    Guid Id,
    int Order,
    string Title,
    string Description);

public sealed record AgentPlanDraft(
    string Objective,
    string Summary,
    ImmutableArray<string> Constraints,
    ImmutableArray<AgentPlanStepDraft> Steps)
{
    public static AgentPlanDraft Create(
        string objective,
        string summary,
        IEnumerable<string>? constraints,
        IEnumerable<AgentPlanStepDraft> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        return new AgentPlanDraft(
            objective,
            summary,
            constraints?.ToImmutableArray() ?? ImmutableArray<string>.Empty,
            steps.ToImmutableArray());
    }
}

public sealed record AgentPlan(
    Guid Id,
    int Version,
    string Objective,
    string Summary,
    ImmutableArray<string> Constraints,
    ImmutableArray<AgentPlanStep> Steps,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ApprovedAt,
    AgentPlanRevisionSource LastRevisionSource);

public sealed class AgentTaskChangedEventArgs : EventArgs
{
    public AgentTaskChangedEventArgs(AgentTaskState state)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
    }

    public AgentTaskState State { get; }
}
