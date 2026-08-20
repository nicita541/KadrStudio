using System.Text.Json;

namespace KadrStudio.Application.Automation.Agent.Tools.ReadOnly;

public sealed class SearchTimelineTool(IAgentReadOnlyToolBackend backend) : IAgentTool
{
    private static readonly AgentToolDescriptor ToolDescriptor = new(
        "search_timeline",
        "Page through timeline clips without losing results after the overview limit. Filter by time range, source or track and continue with next_cursor.",
        AgentToolAccess.ReadOnly,
        AgentToolJson.ParseObject(
            """
            {"type":"object","properties":{"sequence_id":{"type":"string","format":"uuid"},"start_seconds":{"type":"number","minimum":0},"end_seconds":{"type":"number","minimum":0},"source_id":{"type":"string","format":"uuid"},"track_id":{"type":"string","format":"uuid"},"cursor":{"type":"integer","minimum":0},"page_size":{"type":"integer","minimum":1,"maximum":100}},"additionalProperties":false}
            """));

    public AgentToolDescriptor Descriptor => ToolDescriptor;

    public async ValueTask<AgentToolExecutionOutput> ExecuteAsync(
        AgentToolContext context,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        AgentToolJson.EnsureOnlyProperties(
            arguments,
            "sequence_id", "start_seconds", "end_seconds", "source_id", "track_id", "cursor", "page_size");
        var start = OptionalDouble(arguments, "start_seconds");
        var end = OptionalDouble(arguments, "end_seconds");
        if (start is { } startValue && end is { } endValue && endValue <= startValue)
        {
            throw new AgentToolInputException("'end_seconds' must be greater than 'start_seconds'.");
        }

        var request = new AgentTimelineSearchRequest(
            AgentToolJson.OptionalGuid(arguments, "sequence_id") ?? context.DefaultReadSequenceId,
            start,
            end,
            AgentToolJson.OptionalGuid(arguments, "source_id"),
            AgentToolJson.OptionalGuid(arguments, "track_id"),
            OptionalInt(arguments, "cursor", 0),
            OptionalInt(arguments, "page_size", 50));
        var data = await backend.SearchTimelineAsync(context, request, cancellationToken);
        return new AgentToolExecutionOutput("Timeline search page loaded.", data.Clone());
    }

    private static double? OptionalDouble(JsonElement element, string name)
        => element.TryGetProperty(name, out _) ? AgentToolJson.RequireFiniteDouble(element, name, 0) : null;

    private static int OptionalInt(JsonElement element, string name, int fallback)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : fallback;
}
