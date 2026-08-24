using System.Collections.Immutable;
using System.Text.Json;

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

public enum AgentEvidenceRequirement
{
    Timeline,
    Frames,
    Audio,
    Transcript,
    All
}

public enum AgentTaskKind
{
    ReadOnly,
    Edit,
    Mixed
}

public enum AgentInvestigationStrategy
{
    DirectTarget,
    TimelineOnly,
    ContentDiscovery
}

public enum AgentEvidenceChannel
{
    Project,
    EditorContext,
    Timeline,
    Integrity,
    Frames,
    Audio,
    Transcript,
    Recurrence,
    SequenceDiff,
    EditLog
}

[Flags]
public enum AgentEvidenceCapabilities
{
    None = 0,
    Project = 1 << 0,
    EditorContext = 1 << 1,
    Timeline = 1 << 2,
    Frames = 1 << 3,
    Audio = 1 << 4,
    Transcript = 1 << 5,
    Integrity = 1 << 6,
    Comparison = 1 << 7,
    SequenceDiff = 1 << 8,
    EditLog = 1 << 9
}

public sealed record AgentTaskBrief(
    AgentTaskKind Kind,
    string Goal,
    string Scope,
    ImmutableArray<string> ProtectedElements,
    ImmutableArray<string> Constraints,
    ImmutableArray<string> AcceptanceCriteria,
    ImmutableArray<string> Assumptions,
    ImmutableArray<string> MissingInformation,
    AgentInvestigationStrategy InvestigationStrategy = AgentInvestigationStrategy.DirectTarget)
{
    public static AgentTaskBrief Create(
        AgentTaskKind kind,
        string goal,
        string scope,
        IEnumerable<string>? protectedElements = null,
        IEnumerable<string>? constraints = null,
        IEnumerable<string>? acceptanceCriteria = null,
        IEnumerable<string>? assumptions = null,
        IEnumerable<string>? missingInformation = null,
        AgentInvestigationStrategy investigationStrategy = AgentInvestigationStrategy.DirectTarget)
        => new(
            kind,
            goal.Trim(),
            scope.Trim(),
            Normalize(protectedElements),
            Normalize(constraints),
            Normalize(acceptanceCriteria),
            Normalize(assumptions),
            Normalize(missingInformation),
            investigationStrategy);

    private static ImmutableArray<string> Normalize(IEnumerable<string>? values)
        => values?
               .Where(value => !string.IsNullOrWhiteSpace(value))
               .Select(value => value.Trim())
               .Distinct(StringComparer.Ordinal)
               .ToImmutableArray()
           ?? ImmutableArray<string>.Empty;
}

public sealed record AgentQuestionOption(
    string Id,
    string Label,
    string Description,
    bool IsRecommended = false);

public sealed record AgentEvidenceRecord(
    Guid Id,
    int Sequence,
    AgentEvidenceChannel Channel,
    string ToolName,
    Guid TargetId,
    long? SourceRevision,
    double? StartSeconds,
    double? EndSeconds,
    string Summary,
    ImmutableArray<string> Facts,
    string? ArtifactReference,
    DateTimeOffset CreatedAt,
    AgentEvidenceCapabilities Capabilities = AgentEvidenceCapabilities.None,
    double? BoundarySeconds = null);

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
    string? Answer = null,
    ImmutableArray<AgentQuestionOption> Options = default,
    string? RecommendedOptionId = null,
    bool AllowFreeText = true)
{
    public bool IsAnswered => AnsweredAt is not null;

    public ImmutableArray<AgentQuestionOption> AvailableOptions =>
        Options.IsDefault ? ImmutableArray<AgentQuestionOption>.Empty : Options;
}

public sealed record AgentPlanStepDraft(
    string Title,
    string Description,
    string? ExpectedEditingTool = null,
    ImmutableArray<int> EvidenceObservationSequences = default,
    JsonElement? ExpectedEditingArguments = null,
    AgentEvidenceRequirement EvidenceRequirement = AgentEvidenceRequirement.Timeline,
    string ExpectedEffect = "",
    ImmutableArray<string> ProtectedInvariants = default,
    ImmutableArray<string> VerificationChecks = default);

public sealed record AgentPlanStep(
    Guid Id,
    int Order,
    string Title,
    string Description,
    string? ExpectedEditingTool = null,
    ImmutableArray<int> EvidenceObservationSequences = default,
    JsonElement? ExpectedEditingArguments = null,
    AgentEvidenceRequirement EvidenceRequirement = AgentEvidenceRequirement.Timeline,
    string ExpectedEffect = "",
    ImmutableArray<string> ProtectedInvariants = default,
    ImmutableArray<string> VerificationChecks = default);

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
