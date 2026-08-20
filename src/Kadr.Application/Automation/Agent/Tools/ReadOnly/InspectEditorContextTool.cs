using System.Text.Json;

namespace KadrStudio.Application.Automation.Agent.Tools.ReadOnly;

public sealed class InspectEditorContextTool(IAgentReadOnlyToolBackend backend) : IAgentTool
{
    private static readonly AgentToolDescriptor ToolDescriptor = new(
        "inspect_editor_context",
        "Read the active sequence, revision, playhead, selected clip and In/Out range. Use it to resolve references such as 'this clip' without asking the user.",
        AgentToolAccess.ReadOnly,
        AgentToolJson.ParseObject(
            """{"type":"object","properties":{},"additionalProperties":false}"""));

    public AgentToolDescriptor Descriptor => ToolDescriptor;

    public async ValueTask<AgentToolExecutionOutput> ExecuteAsync(
        AgentToolContext context,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        AgentToolJson.EnsureOnlyProperties(arguments);
        var data = await backend.InspectEditorContextAsync(context, cancellationToken);
        return new AgentToolExecutionOutput("Editor context inspected.", data.Clone());
    }
}
