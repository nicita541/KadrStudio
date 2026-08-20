using System.Text.Json;

namespace KadrStudio.Application.Automation.Agent.Tools.ReadOnly;

public sealed class InspectTimelineIntegrityTool : IAgentTool
{
    private static readonly AgentToolDescriptor ToolDescriptor = new(
        "inspect_timeline_integrity",
        "Measure timeline gaps, overlaps, linked-clip synchronization, junctions and source-range coverage without changing the sequence.",
        AgentToolAccess.ReadOnly,
        AgentToolJson.ParseObject(
            """
            {
              "type": "object",
              "properties": {
                "sequence_id": { "type": "string", "format": "uuid" }
              },
              "additionalProperties": false
            }
            """));

    private readonly IAgentReadOnlyToolBackend _backend;

    public InspectTimelineIntegrityTool(IAgentReadOnlyToolBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public AgentToolDescriptor Descriptor => ToolDescriptor;

    public async ValueTask<AgentToolExecutionOutput> ExecuteAsync(
        AgentToolContext context,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        AgentToolJson.EnsureOnlyProperties(arguments, "sequence_id");
        var sequenceId = AgentToolJson.OptionalGuid(arguments, "sequence_id")
                         ?? context.DefaultReadSequenceId;
        var data = await _backend.InspectTimelineIntegrityAsync(
            context,
            sequenceId,
            cancellationToken);
        return new AgentToolExecutionOutput(
            $"Timeline integrity inspection completed for sequence '{sequenceId}'.",
            data.Clone());
    }
}
