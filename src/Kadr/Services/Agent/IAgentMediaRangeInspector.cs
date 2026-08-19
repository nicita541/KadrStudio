using System.Text.Json;
using KadrStudio.Application.Automation.Agent.Tools.ReadOnly;
using KadrStudio.Core.Domain;

namespace KadrStudio.Services.Agent;

public interface IAgentMediaRangeInspector
{
    ValueTask<JsonElement> InspectAsync(
        MediaSource source,
        AgentRangeInspectionRequest request,
        CancellationToken cancellationToken);
}
