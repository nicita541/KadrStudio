using System.Text.Json;

namespace KadrStudio.Application.Automation.Agent.Tools.Editing;

public sealed record AgentAppliedEdit(
    int Sequence,
    string ToolName,
    string Reason,
    string Summary,
    DateTimeOffset AppliedAt);

public sealed record AgentTimelineRange(
    double StartSeconds,
    double EndSeconds);

public interface IAgentEditingToolBackend
{
    ValueTask<JsonElement> RippleDeleteRangeAsync(
        AgentToolContext context,
        double startSeconds,
        double endSeconds,
        string reason,
        CancellationToken cancellationToken);

    ValueTask<JsonElement> RippleDeleteRangesAsync(
        AgentToolContext context,
        IReadOnlyList<AgentTimelineRange> ranges,
        string reason,
        CancellationToken cancellationToken);

    ValueTask<JsonElement> SplitTimelineAsync(
        AgentToolContext context,
        double positionSeconds,
        string reason,
        CancellationToken cancellationToken);

    ValueTask<JsonElement> DeleteClipsAsync(
        AgentToolContext context,
        IReadOnlyCollection<Guid> clipIds,
        bool includeLinked,
        string reason,
        CancellationToken cancellationToken);

    ValueTask<JsonElement> TrimClipAsync(
        AgentToolContext context,
        Guid clipId,
        string edge,
        double edgeSeconds,
        string reason,
        CancellationToken cancellationToken);

    ValueTask<JsonElement> MoveClipAsync(
        AgentToolContext context,
        Guid clipId,
        Guid targetTrackId,
        double startSeconds,
        string reason,
        CancellationToken cancellationToken);

    ValueTask<JsonElement> SetClipVolumeAsync(
        AgentToolContext context,
        Guid clipId,
        double volume,
        bool muted,
        string reason,
        CancellationToken cancellationToken);

    ValueTask<JsonElement> AddTextAsync(
        AgentToolContext context,
        double startSeconds,
        double durationSeconds,
        string text,
        bool subtitle,
        double fontSize,
        double x,
        double y,
        string reason,
        CancellationToken cancellationToken);

    ValueTask<JsonElement> AddTransitionAsync(
        AgentToolContext context,
        Guid fromClipId,
        string kind,
        double durationSeconds,
        string reason,
        CancellationToken cancellationToken);

    ValueTask<JsonElement> InspectEditLogAsync(
        AgentToolContext context,
        CancellationToken cancellationToken);

    void Reset(Guid taskId);
}
