using System.Text.Json;
using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Automation.Agent.Tools.Editing;

public sealed class SetClipVideoAgentTool(IAgentEditingToolBackend backend) : AgentEditingToolBase(backend)
{
    private static readonly AgentToolDescriptor ToolDescriptor = new(
        "set_clip_video", "Partially update video parameters on one visual draft clip. Omitted values remain unchanged.", AgentToolAccess.Editing,
        AgentToolJson.ParseObject("""
        {"type":"object","properties":{"clip_id":{"type":"string","format":"uuid"},"brightness":{"type":"number"},"contrast":{"type":"number"},"saturation":{"type":"number"},"temperature":{"type":"number"},"position_x":{"type":"number"},"position_y":{"type":"number"},"scale_x":{"type":"number","exclusiveMinimum":0},"scale_y":{"type":"number","exclusiveMinimum":0},"rotation":{"type":"number"},"crop_left":{"type":"number","minimum":0,"maximum":1},"crop_top":{"type":"number","minimum":0,"maximum":1},"crop_right":{"type":"number","minimum":0,"maximum":1},"crop_bottom":{"type":"number","minimum":0,"maximum":1},"opacity":{"type":"number","minimum":0,"maximum":1}},"required":["clip_id"],"additionalProperties":false}
        """));
    public override AgentToolDescriptor Descriptor => ToolDescriptor;
    public override async ValueTask<AgentToolExecutionOutput> ExecuteAsync(AgentToolContext context, JsonElement arguments, CancellationToken cancellationToken)
    {
        AgentToolJson.EnsureOnlyProperties(arguments, "clip_id", "brightness", "contrast", "saturation", "temperature", "position_x", "position_y", "scale_x", "scale_y", "rotation", "crop_left", "crop_top", "crop_right", "crop_bottom", "opacity");
        if (arguments.EnumerateObject().Count() == 1)
            throw new AgentToolInputException("At least one video parameter must be supplied.");
        var patch = new AgentVideoParametersPatch(
            OptionalDouble(arguments, "brightness"), OptionalDouble(arguments, "contrast"),
            OptionalDouble(arguments, "saturation"), OptionalDouble(arguments, "temperature"),
            OptionalDouble(arguments, "position_x"), OptionalDouble(arguments, "position_y"),
            OptionalDouble(arguments, "scale_x", double.Epsilon), OptionalDouble(arguments, "scale_y", double.Epsilon),
            OptionalDouble(arguments, "rotation"), OptionalDouble(arguments, "crop_left", 0, 1),
            OptionalDouble(arguments, "crop_top", 0, 1), OptionalDouble(arguments, "crop_right", 0, 1),
            OptionalDouble(arguments, "crop_bottom", 0, 1), OptionalDouble(arguments, "opacity", 0, 1));
        var data = await Backend.UpdateClipVideoAsync(context, AgentToolJson.RequireGuid(arguments, "clip_id"), patch, ApprovedPlanReason, cancellationToken);
        return Output("Video parameters applied.", data);
    }

    private static double? OptionalDouble(JsonElement arguments, string name, double minimum = double.NegativeInfinity, double maximum = double.PositiveInfinity)
    {
        if (!arguments.TryGetProperty(name, out _)) return null;
        var value = AgentToolJson.RequireFiniteDouble(arguments, name, minimum);
        if (value > maximum) throw new AgentToolInputException($"'{name}' exceeds its maximum value.");
        return value;
    }
}

public sealed class SetClipAudioAgentTool(IAgentEditingToolBackend backend) : AgentEditingToolBase(backend)
{
    private static readonly AgentToolDescriptor ToolDescriptor = new(
        "set_clip_audio", "Partially update audio parameters on one audio draft clip. Omitted values remain unchanged.", AgentToolAccess.Editing,
        AgentToolJson.ParseObject("""
        {"type":"object","properties":{"clip_id":{"type":"string","format":"uuid"},"volume":{"type":"number","minimum":0,"maximum":4},"muted":{"type":"boolean"},"pan":{"type":"number","minimum":-1,"maximum":1},"fade_in_seconds":{"type":"number","minimum":0},"fade_out_seconds":{"type":"number","minimum":0},"bass":{"type":"number"},"mid":{"type":"number"},"treble":{"type":"number"}},"required":["clip_id"],"additionalProperties":false}
        """));
    public override AgentToolDescriptor Descriptor => ToolDescriptor;
    public override async ValueTask<AgentToolExecutionOutput> ExecuteAsync(AgentToolContext context, JsonElement arguments, CancellationToken cancellationToken)
    {
        AgentToolJson.EnsureOnlyProperties(arguments, "clip_id", "volume", "muted", "pan", "fade_in_seconds", "fade_out_seconds", "bass", "mid", "treble");
        if (arguments.EnumerateObject().Count() == 1)
            throw new AgentToolInputException("At least one audio parameter must be supplied.");
        var patch = new AgentAudioParametersPatch(
            OptionalDouble(arguments, "volume", 0, 4),
            arguments.TryGetProperty("muted", out _) ? AgentToolJson.OptionalBoolean(arguments, "muted") : null,
            OptionalDouble(arguments, "pan", -1, 1),
            OptionalDouble(arguments, "fade_in_seconds", 0),
            OptionalDouble(arguments, "fade_out_seconds", 0),
            OptionalDouble(arguments, "bass"), OptionalDouble(arguments, "mid"), OptionalDouble(arguments, "treble"));
        var data = await Backend.UpdateClipAudioAsync(context, AgentToolJson.RequireGuid(arguments, "clip_id"), patch, ApprovedPlanReason, cancellationToken);
        return Output("Audio parameters applied.", data);
    }

    private static double? OptionalDouble(JsonElement arguments, string name, double minimum = double.NegativeInfinity, double maximum = double.PositiveInfinity)
    {
        if (!arguments.TryGetProperty(name, out _)) return null;
        var value = AgentToolJson.RequireFiniteDouble(arguments, name, minimum);
        if (value > maximum) throw new AgentToolInputException($"'{name}' exceeds its maximum value.");
        return value;
    }
}

public sealed class UnlinkClipsAgentTool(IAgentEditingToolBackend backend) : AgentEditingToolBase(backend)
{
    private static readonly AgentToolDescriptor ToolDescriptor = new(
        "unlink_clips", "Remove link-group relationships for explicitly named draft clips.", AgentToolAccess.Editing,
        AgentToolJson.ParseObject("""{"type":"object","properties":{"clip_ids":{"type":"array","minItems":1,"items":{"type":"string","format":"uuid"}}},"required":["clip_ids"],"additionalProperties":false}"""));
    public override AgentToolDescriptor Descriptor => ToolDescriptor;
    public override async ValueTask<AgentToolExecutionOutput> ExecuteAsync(AgentToolContext context, JsonElement arguments, CancellationToken cancellationToken)
    {
        AgentToolJson.EnsureOnlyProperties(arguments, "clip_ids");
        var ids = ToolArrayReader.RequireGuids(arguments, "clip_ids");
        var data = await Backend.UnlinkClipsAsync(context, ids, ApprovedPlanReason, cancellationToken);
        return Output("Clip links removed.", data);
    }
}

public sealed class DeleteTimelineObjectsAgentTool(IAgentEditingToolBackend backend) : AgentEditingToolBase(backend)
{
    private static readonly AgentToolDescriptor ToolDescriptor = new(
        "delete_timeline_objects", "Delete explicitly named text clips, transitions and/or markers without deleting media clips.", AgentToolAccess.Editing,
        AgentToolJson.ParseObject("""{"type":"object","properties":{"text_clip_ids":{"type":"array","items":{"type":"string","format":"uuid"}},"transition_ids":{"type":"array","items":{"type":"string","format":"uuid"}},"marker_ids":{"type":"array","items":{"type":"string","format":"uuid"}}},"required":["text_clip_ids","transition_ids","marker_ids"],"additionalProperties":false}"""));
    public override AgentToolDescriptor Descriptor => ToolDescriptor;
    public override async ValueTask<AgentToolExecutionOutput> ExecuteAsync(AgentToolContext context, JsonElement arguments, CancellationToken cancellationToken)
    {
        AgentToolJson.EnsureOnlyProperties(arguments, "text_clip_ids", "transition_ids", "marker_ids");
        var data = await Backend.DeleteTimelineObjectsAsync(context,
            ToolArrayReader.RequireGuids(arguments, "text_clip_ids", allowEmpty: true),
            ToolArrayReader.RequireGuids(arguments, "transition_ids", allowEmpty: true),
            ToolArrayReader.RequireGuids(arguments, "marker_ids", allowEmpty: true),
            ApprovedPlanReason, cancellationToken);
        return Output("Timeline objects deleted.", data);
    }
}

public sealed class AddMarkerAgentTool(IAgentEditingToolBackend backend) : AgentEditingToolBase(backend)
{
    private static readonly AgentToolDescriptor ToolDescriptor = new(
        "add_marker", "Add a neutral note marker to the Agent Draft. It does not assign a content label.", AgentToolAccess.Editing,
        AgentToolJson.ParseObject("""{"type":"object","properties":{"start_seconds":{"type":"number","minimum":0},"duration_seconds":{"type":"number","minimum":0},"title":{"type":"string"},"description":{"type":"string"}},"required":["start_seconds","duration_seconds","title","description"],"additionalProperties":false}"""));
    public override AgentToolDescriptor Descriptor => ToolDescriptor;
    public override async ValueTask<AgentToolExecutionOutput> ExecuteAsync(AgentToolContext context, JsonElement arguments, CancellationToken cancellationToken)
    {
        AgentToolJson.EnsureOnlyProperties(arguments, "start_seconds", "duration_seconds", "title", "description");
        var data = await Backend.AddMarkerAsync(context,
            AgentToolJson.RequireFiniteDouble(arguments, "start_seconds", 0),
            AgentToolJson.RequireFiniteDouble(arguments, "duration_seconds", 0),
            AgentToolJson.RequireString(arguments, "title"), AgentToolJson.RequireString(arguments, "description"),
            ApprovedPlanReason, cancellationToken);
        return Output("Note marker added.", data);
    }
}

internal static class ToolArrayReader
{
    public static Guid[] RequireGuids(JsonElement arguments, string propertyName, bool allowEmpty = false)
    {
        if (!arguments.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new AgentToolInputException($"'{propertyName}' must be an array.");
        var ids = value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String && item.TryGetGuid(out var id)
            ? id
            : throw new AgentToolInputException($"'{propertyName}' contains an invalid UUID.")).Distinct().ToArray();
        if (!allowEmpty && ids.Length == 0) throw new AgentToolInputException($"'{propertyName}' cannot be empty.");
        return ids;
    }
}
