using System.Text.Json;

namespace KadrStudio.Application.Automation.Agent.Tools.ReadOnly;

public sealed class InspectTimelineTool : IAgentTool
{
    private static readonly AgentToolDescriptor ToolDescriptor = new(
        "inspect_timeline",
        "Read the structure of a sequence: tracks, clips, timing and stable ids. If sequence_id is omitted, the current source/draft sequence is used. This tool never edits the timeline.",
        AgentToolAccess.ReadOnly,
        AgentToolJson.ParseObject(
            """
            {
              "type": "object",
              "properties": {
                "sequence_id": {
                  "type": "string",
                  "format": "uuid"
                }
              },
              "additionalProperties": false
            }
            """));

    private readonly IAgentReadOnlyToolBackend _backend;

    public InspectTimelineTool(IAgentReadOnlyToolBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public AgentToolDescriptor Descriptor => ToolDescriptor;

    public async ValueTask<AgentToolExecutionOutput> ExecuteAsync(
        AgentToolContext context,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        AgentToolJson.EnsureOnlyProperties(
            arguments,
            "sequence_id");

        var sequenceId =
            AgentToolJson.OptionalGuid(arguments, "sequence_id")
            ?? context.DefaultReadSequenceId;

        var data = await _backend.InspectTimelineAsync(
            context,
            sequenceId,
            cancellationToken);

        return new AgentToolExecutionOutput(
            $"Timeline inspection completed for sequence '{sequenceId}'.",
            data.Clone());
    }
}
