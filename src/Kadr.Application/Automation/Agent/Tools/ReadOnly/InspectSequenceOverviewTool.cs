using System.Text.Json;

namespace KadrStudio.Application.Automation.Agent.Tools.ReadOnly;

public sealed class InspectSequenceOverviewTool(IAgentReadOnlyToolBackend backend) : IAgentTool
{
    private static readonly AgentToolDescriptor ToolDescriptor = new(
        "inspect_sequence_overview",
        "Return an evenly sampled technical overview of the full sequence. It reports coverage and measured candidates without semantic labels or editing decisions.",
        AgentToolAccess.ReadOnly,
        AgentToolJson.ParseObject(
            """
            {"type":"object","properties":{"sequence_id":{"type":"string","format":"uuid"},"bucket_count":{"type":"integer","minimum":4,"maximum":64}},"additionalProperties":false}
            """));

    public AgentToolDescriptor Descriptor => ToolDescriptor;

    public async ValueTask<AgentToolExecutionOutput> ExecuteAsync(
        AgentToolContext context,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        AgentToolJson.EnsureOnlyProperties(arguments, "sequence_id", "bucket_count");
        var sequenceId = AgentToolJson.OptionalGuid(arguments, "sequence_id")
                         ?? context.DefaultReadSequenceId;
        var bucketCount = arguments.TryGetProperty("bucket_count", out var count) && count.TryGetInt32(out var parsed)
            ? Math.Clamp(parsed, 4, 64)
            : 16;
        var data = await backend.InspectSequenceOverviewAsync(
            context,
            sequenceId,
            bucketCount,
            cancellationToken);
        return new AgentToolExecutionOutput("Sequence technical overview completed.", data.Clone());
    }
}
