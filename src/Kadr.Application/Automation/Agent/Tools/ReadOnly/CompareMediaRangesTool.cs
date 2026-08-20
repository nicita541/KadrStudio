using System.Text.Json;

namespace KadrStudio.Application.Automation.Agent.Tools.ReadOnly;

public sealed class CompareMediaRangesTool(IAgentReadOnlyToolBackend backend) : IAgentTool
{
    private static readonly AgentToolDescriptor ToolDescriptor = new(
        "compare_media_ranges",
        "Measure similarity between explicitly supplied media ranges. Returns measurements only and never assigns semantic labels or decides edits.",
        AgentToolAccess.ReadOnly,
        AgentToolJson.ParseObject(
            """
            {"type":"object","properties":{"ranges":{"type":"array","minItems":2,"maxItems":24,"items":{"type":"object","properties":{"id":{"type":"string","maxLength":100},"media_id":{"type":"string","format":"uuid"},"start_seconds":{"type":"number","minimum":0},"end_seconds":{"type":"number","exclusiveMinimum":0}},"required":["id","media_id","start_seconds","end_seconds"],"additionalProperties":false}},"minimum_similarity":{"type":"number","minimum":0,"maximum":1}},"required":["ranges"],"additionalProperties":false}
            """));

    public AgentToolDescriptor Descriptor => ToolDescriptor;

    public async ValueTask<AgentToolExecutionOutput> ExecuteAsync(
        AgentToolContext context,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        AgentToolJson.EnsureOnlyProperties(arguments, "ranges", "minimum_similarity");
        if (!arguments.TryGetProperty("ranges", out var ranges) || ranges.ValueKind != JsonValueKind.Array)
        {
            throw new AgentToolInputException("'ranges' must be an array.");
        }

        var samples = ranges.EnumerateArray().Select(item =>
        {
            AgentToolJson.EnsureOnlyProperties(item, "id", "media_id", "start_seconds", "end_seconds");
            var sample = new AgentRecurringSectionSample(
                AgentToolJson.RequireString(item, "id"),
                AgentToolJson.RequireGuid(item, "media_id"),
                AgentToolJson.RequireFiniteDouble(item, "start_seconds", 0),
                AgentToolJson.RequireFiniteDouble(item, "end_seconds", double.Epsilon));
            if (sample.EndSeconds <= sample.StartSeconds)
            {
                throw new AgentToolInputException("Every range end must be greater than its start.");
            }
            return sample;
        }).ToArray();
        if (samples.Length is < 2 or > 24 || samples.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != samples.Length)
        {
            throw new AgentToolInputException("Provide 2–24 ranges with unique ids.");
        }

        var threshold = arguments.TryGetProperty("minimum_similarity", out _)
            ? AgentToolJson.RequireFiniteDouble(arguments, "minimum_similarity", 0)
            : 0;
        var data = await backend.CompareMediaRangesAsync(context, samples, threshold, cancellationToken);
        return new AgentToolExecutionOutput($"Compared {samples.Length} media ranges.", data.Clone());
    }
}
