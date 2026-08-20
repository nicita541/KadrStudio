using System.Text.Json;

namespace KadrStudio.Application.Automation.Agent.Tools;

public interface IAgentTool
{
    AgentToolDescriptor Descriptor { get; }

    ValueTask<AgentToolExecutionOutput> ExecuteAsync(
        AgentToolContext context,
        JsonElement arguments,
        CancellationToken cancellationToken);
}
