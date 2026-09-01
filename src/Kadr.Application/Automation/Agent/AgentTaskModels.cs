using System.Collections.Immutable;

namespace KadrStudio.Application.Automation.Agent;

public enum AgentJournalKind
{
    TaskStarted,
    PhaseChanged,
    Progress,
    TaskFailed,
    TaskCancelled,
    DraftReviewReady,
    DraftAccepted,
    DraftDiscarded,
    CheckpointSaved
}

public sealed record AgentJournalEntry(
    Guid Id,
    DateTimeOffset CreatedAt,
    AgentJournalKind Kind,
    string Message);

public sealed record EditorialTaskCheckpoint(
    AgentTaskPhase Stage,
    ImmutableArray<string> AssetIds,
    ImmutableArray<string> JobIds,
    ImmutableArray<string> ArtifactIds,
    string? ProfileId,
    string? Intent,
    string? EvidenceFingerprint,
    string? GraphFingerprint,
    DateTimeOffset SavedAt)
{
    public EditorialTaskCheckpoint Normalize() => this with
    {
        AssetIds = AssetIds.IsDefault ? [] : AssetIds,
        JobIds = JobIds.IsDefault ? [] : JobIds,
        ArtifactIds = ArtifactIds.IsDefault ? [] : ArtifactIds
    };
}

public sealed class AgentTaskChangedEventArgs(AgentTaskState state) : EventArgs
{
    public AgentTaskState State { get; } = state ?? throw new ArgumentNullException(nameof(state));
}
