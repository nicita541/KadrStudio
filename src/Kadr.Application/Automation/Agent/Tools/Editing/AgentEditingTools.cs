using System.Text.Json;

namespace KadrStudio.Application.Automation.Agent.Tools.Editing;

public abstract class AgentEditingToolBase(
    IAgentEditingToolBackend backend) : IAgentTool
{
    protected const string ApprovedPlanReason = "Approved agent plan";

    protected IAgentEditingToolBackend Backend { get; } =
        backend ?? throw new ArgumentNullException(nameof(backend));

    public abstract AgentToolDescriptor Descriptor { get; }

    public abstract ValueTask<AgentToolExecutionOutput> ExecuteAsync(
        AgentToolContext context,
        JsonElement arguments,
        CancellationToken cancellationToken);

    protected static AgentToolExecutionOutput Output(
        string summary,
        JsonElement data)
        => new(summary, data.Clone());
}

public sealed class RippleDeleteRangeAgentTool(
    IAgentEditingToolBackend backend) : AgentEditingToolBase(backend)
{
    private static readonly AgentToolDescriptor ToolDescriptor = new(
        "ripple_delete_range",
        "Delete one exact timeline interval from the Agent Draft and close the gap. Use only after evidence supports the requested removal. Times are timeline seconds in the draft.",
        AgentToolAccess.Editing,
        AgentToolJson.ParseObject(
            """
            {
              "type": "object",
              "properties": {
                "start_seconds": { "type": "number", "minimum": 0 },
                "end_seconds": { "type": "number", "exclusiveMinimum": 0 }
              },
              "required": ["start_seconds", "end_seconds"],
              "additionalProperties": false
            }
            """));

    public override AgentToolDescriptor Descriptor => ToolDescriptor;

    public override async ValueTask<AgentToolExecutionOutput> ExecuteAsync(
        AgentToolContext context,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        AgentToolJson.EnsureOnlyProperties(
            arguments,
            "start_seconds",
            "end_seconds");

        var start = AgentToolJson.RequireFiniteDouble(arguments, "start_seconds", 0);
        var end = AgentToolJson.RequireFiniteDouble(arguments, "end_seconds", 0);
        if (end <= start)
        {
            throw new AgentToolInputException(
                "'end_seconds' must be greater than 'start_seconds'.");
        }

        var data = await Backend.RippleDeleteRangeAsync(
            context,
            start,
            end,
            ApprovedPlanReason,
            cancellationToken);

        return Output(
            $"Ripple-deleted draft range {start:0.###}s-{end:0.###}s.",
            data);
    }
}

public sealed class RippleDeleteRangesAgentTool(
    IAgentEditingToolBackend backend) : AgentEditingToolBase(backend)
{
    private static readonly AgentToolDescriptor ToolDescriptor = new(
        "ripple_delete_ranges",
        "Remove several non-overlapping ranges from the Agent Draft in one safe operation. All ranges use the current draft coordinates from before this tool call; the backend applies them from right to left so earlier deletions do not shift later coordinates.",
        AgentToolAccess.Editing,
        AgentToolJson.ParseObject(
            """
            {
              "type": "object",
              "properties": {
                "ranges": {
                  "type": "array",
                  "minItems": 1,
                  "maxItems": 32,
                  "items": {
                    "type": "object",
                    "properties": {
                      "start_seconds": { "type": "number", "minimum": 0 },
                      "end_seconds": { "type": "number", "exclusiveMinimum": 0 }
                    },
                    "required": ["start_seconds", "end_seconds"],
                    "additionalProperties": false
                  }
                }
              },
              "required": ["ranges"],
              "additionalProperties": false
            }
            """));

    public override AgentToolDescriptor Descriptor => ToolDescriptor;

    public override async ValueTask<AgentToolExecutionOutput> ExecuteAsync(
        AgentToolContext context,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        AgentToolJson.EnsureOnlyProperties(
            arguments,
            "ranges");

        if (!arguments.TryGetProperty("ranges", out var rangesElement) ||
            rangesElement.ValueKind != JsonValueKind.Array)
        {
            throw new AgentToolInputException(
                "'ranges' must be an array.");
        }

        var ranges = new List<AgentTimelineRange>();
        foreach (var item in rangesElement.EnumerateArray())
        {
            if (ranges.Count >= 32)
            {
                throw new AgentToolInputException(
                    "'ranges' cannot contain more than 32 ranges.");
            }

            AgentToolJson.EnsureOnlyProperties(
                item,
                "start_seconds",
                "end_seconds");

            var start = AgentToolJson.RequireFiniteDouble(
                item,
                "start_seconds",
                0);
            var end = AgentToolJson.RequireFiniteDouble(
                item,
                "end_seconds",
                0);

            if (end <= start)
            {
                throw new AgentToolInputException(
                    "Every range must have end_seconds greater than start_seconds.");
            }

            ranges.Add(new AgentTimelineRange(start, end));
        }

        if (ranges.Count == 0)
        {
            throw new AgentToolInputException(
                "'ranges' must contain at least one range.");
        }

        var data = await Backend.RippleDeleteRangesAsync(
            context,
            ranges,
            ApprovedPlanReason,
            cancellationToken);

        return Output(
            $"Removed {ranges.Count} ranges from the Agent Draft.",
            data);
    }
}

public sealed class DeleteClipsAgentTool(
    IAgentEditingToolBackend backend) : AgentEditingToolBase(backend)
{
    private static readonly AgentToolDescriptor ToolDescriptor = new(
        "delete_clips",
        "Delete specific draft clip ids. Prefer ripple_delete_range when the requested edit is a continuous time removal. Linked A/V clips can be removed together.",
        AgentToolAccess.Editing,
        AgentToolJson.ParseObject(
            """
            {
              "type": "object",
              "properties": {
                "clip_ids": {
                  "type": "array",
                  "items": { "type": "string", "format": "uuid" },
                  "minItems": 1,
                  "maxItems": 100
                },
                "include_linked": { "type": "boolean", "default": true }
              },
              "required": ["clip_ids"],
              "additionalProperties": false
            }
            """));

    public override AgentToolDescriptor Descriptor => ToolDescriptor;

    public override async ValueTask<AgentToolExecutionOutput> ExecuteAsync(
        AgentToolContext context,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        AgentToolJson.EnsureOnlyProperties(
            arguments,
            "clip_ids",
            "include_linked");

        var clipIds = AgentToolJson.RequireGuidArray(arguments, "clip_ids");
        var includeLinked = AgentToolJson.OptionalBoolean(
            arguments,
            "include_linked",
            true);
        var data = await Backend.DeleteClipsAsync(
            context,
            clipIds,
            includeLinked,
            ApprovedPlanReason,
            cancellationToken);

        return Output(
            $"Deleted {clipIds.Count} requested draft clip(s).",
            data);
    }
}

public sealed class TrimClipAgentTool(
    IAgentEditingToolBackend backend) : AgentEditingToolBase(backend)
{
    private static readonly AgentToolDescriptor ToolDescriptor = new(
        "trim_clip",
        "Trim one draft clip to a new absolute timeline edge. edge is left or right; edge_seconds is the new timeline position.",
        AgentToolAccess.Editing,
        AgentToolJson.ParseObject(
            """
            {
              "type": "object",
              "properties": {
                "clip_id": { "type": "string", "format": "uuid" },
                "edge": { "type": "string", "enum": ["left", "right"] },
                "edge_seconds": { "type": "number", "minimum": 0 }
              },
              "required": ["clip_id", "edge", "edge_seconds"],
              "additionalProperties": false
            }
            """));

    public override AgentToolDescriptor Descriptor => ToolDescriptor;

    public override async ValueTask<AgentToolExecutionOutput> ExecuteAsync(
        AgentToolContext context,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        AgentToolJson.EnsureOnlyProperties(
            arguments,
            "clip_id",
            "edge",
            "edge_seconds");

        var clipId = AgentToolJson.RequireGuid(arguments, "clip_id");
        var edge = AgentToolJson.RequireString(arguments, "edge").ToLowerInvariant();
        if (edge is not ("left" or "right"))
        {
            throw new AgentToolInputException(
                "'edge' must be 'left' or 'right'.");
        }

        var edgeSeconds = AgentToolJson.RequireFiniteDouble(
            arguments,
            "edge_seconds",
            0);
        var data = await Backend.TrimClipAsync(
            context,
            clipId,
            edge,
            edgeSeconds,
            ApprovedPlanReason,
            cancellationToken);

        return Output(
            $"Trimmed draft clip '{clipId}' {edge} edge.",
            data);
    }
}

public sealed class MoveClipAgentTool(
    IAgentEditingToolBackend backend) : AgentEditingToolBase(backend)
{
    private static readonly AgentToolDescriptor ToolDescriptor = new(
        "move_clip",
        "Move one draft clip to an existing compatible track and absolute timeline start. Use ids returned by inspect_timeline.",
        AgentToolAccess.Editing,
        AgentToolJson.ParseObject(
            """
            {
              "type": "object",
              "properties": {
                "clip_id": { "type": "string", "format": "uuid" },
                "target_track_id": { "type": "string", "format": "uuid" },
                "start_seconds": { "type": "number", "minimum": 0 }
              },
              "required": ["clip_id", "target_track_id", "start_seconds"],
              "additionalProperties": false
            }
            """));

    public override AgentToolDescriptor Descriptor => ToolDescriptor;

    public override async ValueTask<AgentToolExecutionOutput> ExecuteAsync(
        AgentToolContext context,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        AgentToolJson.EnsureOnlyProperties(
            arguments,
            "clip_id",
            "target_track_id",
            "start_seconds");

        var clipId = AgentToolJson.RequireGuid(arguments, "clip_id");
        var trackId = AgentToolJson.RequireGuid(arguments, "target_track_id");
        var startSeconds = AgentToolJson.RequireFiniteDouble(
            arguments,
            "start_seconds",
            0);
        var data = await Backend.MoveClipAsync(
            context,
            clipId,
            trackId,
            startSeconds,
            ApprovedPlanReason,
            cancellationToken);

        return Output(
            $"Moved draft clip '{clipId}'.",
            data);
    }
}

public sealed class SetClipVolumeAgentTool(
    IAgentEditingToolBackend backend) : AgentEditingToolBase(backend)
{
    private static readonly AgentToolDescriptor ToolDescriptor = new(
        "set_clip_volume",
        "Set volume/mute state of one audio-track clip in the Agent Draft. volume is linear gain: 1.0 keeps original level, 0.5 halves it.",
        AgentToolAccess.Editing,
        AgentToolJson.ParseObject(
            """
            {
              "type": "object",
              "properties": {
                "clip_id": { "type": "string", "format": "uuid" },
                "volume": { "type": "number", "minimum": 0, "maximum": 4 },
                "muted": { "type": "boolean" }
              },
              "required": ["clip_id", "volume", "muted"],
              "additionalProperties": false
            }
            """));

    public override AgentToolDescriptor Descriptor => ToolDescriptor;

    public override async ValueTask<AgentToolExecutionOutput> ExecuteAsync(
        AgentToolContext context,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        AgentToolJson.EnsureOnlyProperties(
            arguments,
            "clip_id",
            "volume",
            "muted");

        var clipId = AgentToolJson.RequireGuid(arguments, "clip_id");
        var volume = AgentToolJson.RequireFiniteDouble(arguments, "volume", 0);
        if (volume > 4)
        {
            throw new AgentToolInputException(
                "'volume' must be between 0 and 4.");
        }

        var muted = AgentToolJson.OptionalBoolean(arguments, "muted");
        var data = await Backend.SetClipVolumeAsync(
            context,
            clipId,
            volume,
            muted,
            ApprovedPlanReason,
            cancellationToken);

        return Output(
            $"Updated volume of draft clip '{clipId}'.",
            data);
    }
}

public sealed class AddTextAgentTool(
    IAgentEditingToolBackend backend) : AgentEditingToolBase(backend)
{
    private static readonly AgentToolDescriptor ToolDescriptor = new(
        "add_text",
        "Add a text/title/subtitle clip to the Agent Draft. Coordinates x/y are normalized from 0 to 1.",
        AgentToolAccess.Editing,
        AgentToolJson.ParseObject(
            """
            {
              "type": "object",
              "properties": {
                "start_seconds": { "type": "number", "minimum": 0 },
                "duration_seconds": { "type": "number", "exclusiveMinimum": 0 },
                "text": { "type": "string" },
                "subtitle": { "type": "boolean" },
                "font_size": { "type": "number", "minimum": 8, "maximum": 240 },
                "x": { "type": "number", "minimum": 0, "maximum": 1 },
                "y": { "type": "number", "minimum": 0, "maximum": 1 }
              },
              "required": [
                "start_seconds",
                "duration_seconds",
                "text",
                "subtitle",
                "font_size",
                "x",
                "y"
              ],
              "additionalProperties": false
            }
            """));

    public override AgentToolDescriptor Descriptor => ToolDescriptor;

    public override async ValueTask<AgentToolExecutionOutput> ExecuteAsync(
        AgentToolContext context,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        AgentToolJson.EnsureOnlyProperties(
            arguments,
            "start_seconds",
            "duration_seconds",
            "text",
            "subtitle",
            "font_size",
            "x",
            "y");

        var start = AgentToolJson.RequireFiniteDouble(arguments, "start_seconds", 0);
        var duration = AgentToolJson.RequireFiniteDouble(arguments, "duration_seconds", 0.001);
        var text = AgentToolJson.RequireString(arguments, "text");
        var subtitle = AgentToolJson.OptionalBoolean(arguments, "subtitle");
        var fontSize = AgentToolJson.RequireFiniteDouble(arguments, "font_size", 8);
        var x = AgentToolJson.RequireFiniteDouble(arguments, "x", 0);
        var y = AgentToolJson.RequireFiniteDouble(arguments, "y", 0);
        if (fontSize > 240 || x > 1 || y > 1)
        {
            throw new AgentToolInputException(
                "font_size/x/y exceed their allowed ranges.");
        }

        var data = await Backend.AddTextAsync(
            context,
            start,
            duration,
            text,
            subtitle,
            fontSize,
            x,
            y,
            ApprovedPlanReason,
            cancellationToken);

        return Output(
            "Added text to the Agent Draft.",
            data);
    }
}

public sealed class AddTransitionAgentTool(
    IAgentEditingToolBackend backend) : AgentEditingToolBase(backend)
{
    private static readonly AgentToolDescriptor ToolDescriptor = new(
        "add_transition",
        "Add a transition at the edit after from_clip_id in the Agent Draft. The clips must be adjacent on one track.",
        AgentToolAccess.Editing,
        AgentToolJson.ParseObject(
            """
            {
              "type": "object",
              "properties": {
                "from_clip_id": { "type": "string", "format": "uuid" },
                "kind": {
                  "type": "string",
                  "enum": [
                    "cross_dissolve",
                    "dip_to_black",
                    "dip_to_white",
                    "wipe",
                    "slide",
                    "constant_power_audio"
                  ]
                },
                "duration_seconds": { "type": "number", "exclusiveMinimum": 0 }
              },
              "required": ["from_clip_id", "kind", "duration_seconds"],
              "additionalProperties": false
            }
            """));

    public override AgentToolDescriptor Descriptor => ToolDescriptor;

    public override async ValueTask<AgentToolExecutionOutput> ExecuteAsync(
        AgentToolContext context,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        AgentToolJson.EnsureOnlyProperties(
            arguments,
            "from_clip_id",
            "kind",
            "duration_seconds");

        var fromClipId = AgentToolJson.RequireGuid(arguments, "from_clip_id");
        var kind = AgentToolJson.RequireString(arguments, "kind");
        var duration = AgentToolJson.RequireFiniteDouble(
            arguments,
            "duration_seconds",
            0.001);
        var data = await Backend.AddTransitionAsync(
            context,
            fromClipId,
            kind,
            duration,
            ApprovedPlanReason,
            cancellationToken);

        return Output(
            $"Added transition after draft clip '{fromClipId}'.",
            data);
    }
}

public sealed class InspectAgentEditsTool(
    IAgentEditingToolBackend backend) : AgentEditingToolBase(backend)
{
    private static readonly AgentToolDescriptor ToolDescriptor = new(
        "inspect_agent_edits",
        "Read the exact edit log applied by the agent to the current Agent Draft. Use during verification to confirm what changed. This tool never edits.",
        AgentToolAccess.ReadOnly,
        AgentToolJson.ParseObject(
            """
            {
              "type": "object",
              "properties": {},
              "additionalProperties": false
            }
            """));

    public override AgentToolDescriptor Descriptor => ToolDescriptor;

    public override async ValueTask<AgentToolExecutionOutput> ExecuteAsync(
        AgentToolContext context,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        AgentToolJson.EnsureOnlyProperties(arguments);
        var data = await Backend.InspectEditLogAsync(
            context,
            cancellationToken);

        return Output(
            "Agent edit log inspected.",
            data);
    }
}
