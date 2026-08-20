using System.Text.Json;

namespace KadrStudio.Application.Automation.Agent.Tools.ReadOnly;

public sealed record AgentEditorContextSnapshot(
    Guid ActiveSequenceId,
    long ActiveSequenceRevision,
    double PlayheadSeconds,
    Guid? SelectedClipId,
    double? InPointSeconds,
    double? OutPointSeconds);

public sealed record AgentRecurringSectionSample(
    string Id,
    Guid MediaId,
    double StartSeconds,
    double EndSeconds);

public sealed record AgentTimelineSearchRequest(
    Guid SequenceId,
    double? StartSeconds,
    double? EndSeconds,
    Guid? SourceId,
    Guid? TrackId,
    int Cursor,
    int PageSize);

/// <summary>
/// Adapter boundary between the generic agent tool API and Kadr's existing
/// project/media analysis services.
///
/// Implementations must not mutate project, timeline or media state.
/// Returned JSON should be compact, factual and use stable ids so the agent
/// can request a narrower follow-up observation when necessary.
/// </summary>
public interface IAgentReadOnlyToolBackend
{
    ValueTask<JsonElement> InspectEditorContextAsync(
        AgentToolContext context,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(AgentToolJson.EmptyObject());

    ValueTask<JsonElement> InspectProjectAsync(
        AgentToolContext context,
        CancellationToken cancellationToken);

    ValueTask<JsonElement> InspectTimelineAsync(
        AgentToolContext context,
        Guid sequenceId,
        CancellationToken cancellationToken);

    ValueTask<JsonElement> InspectTimelineIntegrityAsync(
        AgentToolContext context,
        Guid sequenceId,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(AgentToolJson.EmptyObject());

    ValueTask<JsonElement> InspectMediaAsync(
        AgentToolContext context,
        Guid mediaId,
        CancellationToken cancellationToken);

    ValueTask<JsonElement> InspectRangeAsync(
        AgentToolContext context,
        AgentRangeInspectionRequest request,
        CancellationToken cancellationToken);

    ValueTask<JsonElement> CompareSequencesAsync(
        AgentToolContext context,
        Guid sourceSequenceId,
        Guid draftSequenceId,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(AgentToolJson.EmptyObject());

    ValueTask<JsonElement> InspectObjectsAsync(
        AgentToolContext context,
        Guid sequenceId,
        IReadOnlyCollection<Guid> objectIds,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(AgentToolJson.EmptyObject());

    ValueTask<JsonElement> SearchTimelineAsync(
        AgentToolContext context,
        AgentTimelineSearchRequest request,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(AgentToolJson.EmptyObject());

    ValueTask<JsonElement> InspectSequenceOverviewAsync(
        AgentToolContext context,
        Guid sequenceId,
        int bucketCount,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(AgentToolJson.EmptyObject());

    ValueTask<JsonElement> CompareMediaRangesAsync(
        AgentToolContext context,
        IReadOnlyList<AgentRecurringSectionSample> samples,
        double minimumSimilarity,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(AgentToolJson.EmptyObject());
}
