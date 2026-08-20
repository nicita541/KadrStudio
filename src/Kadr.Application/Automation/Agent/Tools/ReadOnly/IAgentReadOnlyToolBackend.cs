using System.Text.Json;

namespace KadrStudio.Application.Automation.Agent.Tools.ReadOnly;

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
    ValueTask<JsonElement> InspectProjectAsync(
        AgentToolContext context,
        CancellationToken cancellationToken);

    ValueTask<JsonElement> InspectTimelineAsync(
        AgentToolContext context,
        Guid sequenceId,
        CancellationToken cancellationToken);

    ValueTask<JsonElement> InspectMediaAsync(
        AgentToolContext context,
        Guid mediaId,
        CancellationToken cancellationToken);

    ValueTask<JsonElement> InspectRangeAsync(
        AgentToolContext context,
        AgentRangeInspectionRequest request,
        CancellationToken cancellationToken);
}
