using System.Text.Json;
using KadrStudio.Core.Domain;

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

public sealed record AgentVideoParametersPatch(
    double? Brightness = null,
    double? Contrast = null,
    double? Saturation = null,
    double? Temperature = null,
    double? PositionX = null,
    double? PositionY = null,
    double? ScaleX = null,
    double? ScaleY = null,
    double? Rotation = null,
    double? CropLeft = null,
    double? CropTop = null,
    double? CropRight = null,
    double? CropBottom = null,
    double? Opacity = null);

public sealed record AgentAudioParametersPatch(
    double? Volume = null,
    bool? Muted = null,
    double? Pan = null,
    double? FadeInSeconds = null,
    double? FadeOutSeconds = null,
    double? Bass = null,
    double? Mid = null,
    double? Treble = null);

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

    ValueTask<JsonElement> SplitClipsAsync(
        AgentToolContext context,
        IReadOnlyCollection<Guid> clipIds,
        double positionSeconds,
        bool includeLinked,
        string reason,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(AgentToolJson.EmptyObject());

    ValueTask<JsonElement> SetClipVideoAsync(
        AgentToolContext context,
        Guid clipId,
        VideoParameters parameters,
        string reason,
        CancellationToken cancellationToken);

    ValueTask<JsonElement> SetClipAudioAsync(
        AgentToolContext context,
        Guid clipId,
        AudioParameters parameters,
        string reason,
        CancellationToken cancellationToken);

    ValueTask<JsonElement> UpdateClipVideoAsync(
        AgentToolContext context,
        Guid clipId,
        AgentVideoParametersPatch patch,
        string reason,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(AgentToolJson.EmptyObject());

    ValueTask<JsonElement> UpdateClipAudioAsync(
        AgentToolContext context,
        Guid clipId,
        AgentAudioParametersPatch patch,
        string reason,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(AgentToolJson.EmptyObject());

    ValueTask<JsonElement> InsertSourceRangeAsync(
        AgentToolContext context,
        Guid sourceId,
        Guid targetTrackId,
        double sourceStartSeconds,
        double sourceEndSeconds,
        double timelineStartSeconds,
        string reason,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(AgentToolJson.EmptyObject());

    ValueTask<JsonElement> UnlinkClipsAsync(
        AgentToolContext context,
        IReadOnlyCollection<Guid> clipIds,
        string reason,
        CancellationToken cancellationToken);

    ValueTask<JsonElement> DeleteTimelineObjectsAsync(
        AgentToolContext context,
        IReadOnlyCollection<Guid> textClipIds,
        IReadOnlyCollection<Guid> transitionIds,
        IReadOnlyCollection<Guid> markerIds,
        string reason,
        CancellationToken cancellationToken);

    ValueTask<JsonElement> AddMarkerAsync(
        AgentToolContext context,
        double startSeconds,
        double durationSeconds,
        string title,
        string description,
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

    ValueTask<JsonElement> UpdateTextAsync(
        AgentToolContext context,
        Guid textClipId,
        double? startSeconds,
        double? durationSeconds,
        string? text,
        bool? subtitle,
        double? fontSize,
        double? x,
        double? y,
        string reason,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(AgentToolJson.EmptyObject());

    ValueTask<JsonElement> UpdateMarkerAsync(
        AgentToolContext context,
        Guid markerId,
        double? startSeconds,
        double? durationSeconds,
        string? title,
        string? description,
        string reason,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(AgentToolJson.EmptyObject());

    ValueTask<JsonElement> UpdateTransitionAsync(
        AgentToolContext context,
        Guid transitionId,
        string? kind,
        double? durationSeconds,
        string reason,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(AgentToolJson.EmptyObject());

    ValueTask<JsonElement> InspectEditLogAsync(
        AgentToolContext context,
        CancellationToken cancellationToken);

    void Reset(Guid taskId);
}
