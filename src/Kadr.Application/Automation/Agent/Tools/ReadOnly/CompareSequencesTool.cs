using System.Text.Json;

namespace KadrStudio.Application.Automation.Agent.Tools.ReadOnly;

public sealed class CompareSequencesTool(IAgentReadOnlyToolBackend backend) : IAgentTool
{
    private static readonly AgentToolDescriptor ToolDescriptor = new(
        "compare_sequences",
        "Compare source and Agent Draft by stable ids, revisions, clip geometry and non-media objects. This tool reports measured differences and never edits either sequence.",
        AgentToolAccess.ReadOnly,
        AgentToolJson.ParseObject(
            """
            {
              "type":"object",
              "properties":{
                "source_sequence_id":{"type":"string","format":"uuid"},
                "draft_sequence_id":{"type":"string","format":"uuid"}
              },
              "additionalProperties":false
            }
            """));

    public AgentToolDescriptor Descriptor => ToolDescriptor;

    public async ValueTask<AgentToolExecutionOutput> ExecuteAsync(
        AgentToolContext context,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        AgentToolJson.EnsureOnlyProperties(arguments, "source_sequence_id", "draft_sequence_id");
        var sourceId = AgentToolJson.OptionalGuid(arguments, "source_sequence_id") ?? context.SourceSequenceId;
        var draftId = AgentToolJson.OptionalGuid(arguments, "draft_sequence_id")
                      ?? context.DraftSequenceId
                      ?? throw new AgentToolRejectedException("Agent Draft does not exist yet.", "draft_required");
        var data = await backend.CompareSequencesAsync(context, sourceId, draftId, cancellationToken);
        return new AgentToolExecutionOutput("Source and Agent Draft compared.", data.Clone());
    }
}
