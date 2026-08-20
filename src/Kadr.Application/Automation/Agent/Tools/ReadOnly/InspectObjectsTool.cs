using System.Text.Json;

namespace KadrStudio.Application.Automation.Agent.Tools.ReadOnly;

public sealed class InspectObjectsTool(IAgentReadOnlyToolBackend backend) : IAgentTool
{
    private static readonly AgentToolDescriptor ToolDescriptor = new(
        "inspect_objects",
        "Read complete parameters of timeline clips, text, markers, transitions or tracks by stable ID.",
        AgentToolAccess.ReadOnly,
        AgentToolJson.ParseObject(
            """
            {"type":"object","properties":{"sequence_id":{"type":"string","format":"uuid"},"object_ids":{"type":"array","minItems":1,"maxItems":100,"items":{"type":"string","format":"uuid"}}},"required":["object_ids"],"additionalProperties":false}
            """));

    public AgentToolDescriptor Descriptor => ToolDescriptor;

    public async ValueTask<AgentToolExecutionOutput> ExecuteAsync(
        AgentToolContext context,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        AgentToolJson.EnsureOnlyProperties(arguments, "sequence_id", "object_ids");
        var ids = AgentToolJson.RequireGuidArray(arguments, "object_ids", 100);
        var sequenceId = AgentToolJson.OptionalGuid(arguments, "sequence_id")
                         ?? context.DefaultReadSequenceId;
        var data = await backend.InspectObjectsAsync(context, sequenceId, ids, cancellationToken);
        return new AgentToolExecutionOutput($"Inspected {ids.Count} timeline object id(s).", data.Clone());
    }
}
