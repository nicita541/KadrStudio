using System.Text.Json;

namespace KadrStudio.Application.Automation.Agent.Tools.Editing;

public sealed class SplitClipsAgentTool(IAgentEditingToolBackend backend) : AgentEditingToolBase(backend)
{
    private static readonly AgentToolDescriptor ToolDescriptor = new(
        "split_clips",
        "Split only explicitly named draft clips at an exact timeline position. include_linked may include their current linked partners.",
        AgentToolAccess.Editing,
        AgentToolJson.ParseObject(
            """
            {"type":"object","properties":{"clip_ids":{"type":"array","minItems":1,"maxItems":100,"items":{"type":"string","format":"uuid"}},"position_seconds":{"type":"number","minimum":0},"include_linked":{"type":"boolean"}},"required":["clip_ids","position_seconds","include_linked"],"additionalProperties":false}
            """));

    public override AgentToolDescriptor Descriptor => ToolDescriptor;

    public override async ValueTask<AgentToolExecutionOutput> ExecuteAsync(
        AgentToolContext context,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        AgentToolJson.EnsureOnlyProperties(arguments, "clip_ids", "position_seconds", "include_linked");
        var ids = AgentToolJson.RequireGuidArray(arguments, "clip_ids", 100);
        var position = AgentToolJson.RequireFiniteDouble(arguments, "position_seconds", 0);
        var includeLinked = AgentToolJson.OptionalBoolean(arguments, "include_linked");
        var data = await Backend.SplitClipsAsync(
            context,
            ids,
            position,
            includeLinked,
            ApprovedPlanReason,
            cancellationToken);
        return Output($"Split {ids.Count} explicitly selected clip(s).", data);
    }
}

public sealed class InsertSourceRangeAgentTool(IAgentEditingToolBackend backend) : AgentEditingToolBase(backend)
{
    private static readonly AgentToolDescriptor ToolDescriptor = new(
        "insert_source_range",
        "Insert one exact source range as a new clip on an existing compatible Agent Draft track.",
        AgentToolAccess.Editing,
        AgentToolJson.ParseObject(
            """
            {"type":"object","properties":{"source_id":{"type":"string","format":"uuid"},"target_track_id":{"type":"string","format":"uuid"},"source_start_seconds":{"type":"number","minimum":0},"source_end_seconds":{"type":"number","exclusiveMinimum":0},"timeline_start_seconds":{"type":"number","minimum":0}},"required":["source_id","target_track_id","source_start_seconds","source_end_seconds","timeline_start_seconds"],"additionalProperties":false}
            """));

    public override AgentToolDescriptor Descriptor => ToolDescriptor;

    public override async ValueTask<AgentToolExecutionOutput> ExecuteAsync(
        AgentToolContext context,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        AgentToolJson.EnsureOnlyProperties(
            arguments,
            "source_id", "target_track_id", "source_start_seconds", "source_end_seconds", "timeline_start_seconds");
        var sourceStart = AgentToolJson.RequireFiniteDouble(arguments, "source_start_seconds", 0);
        var sourceEnd = AgentToolJson.RequireFiniteDouble(arguments, "source_end_seconds", double.Epsilon);
        if (sourceEnd <= sourceStart)
            throw new AgentToolInputException("'source_end_seconds' must be greater than 'source_start_seconds'.");
        var data = await Backend.InsertSourceRangeAsync(
            context,
            AgentToolJson.RequireGuid(arguments, "source_id"),
            AgentToolJson.RequireGuid(arguments, "target_track_id"),
            sourceStart,
            sourceEnd,
            AgentToolJson.RequireFiniteDouble(arguments, "timeline_start_seconds", 0),
            ApprovedPlanReason,
            cancellationToken);
        return Output("Inserted exact source range into Agent Draft.", data);
    }
}
