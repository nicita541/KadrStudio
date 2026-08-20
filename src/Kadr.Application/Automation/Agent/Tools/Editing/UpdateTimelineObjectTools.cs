using System.Text.Json;

namespace KadrStudio.Application.Automation.Agent.Tools.Editing;

public sealed class UpdateTextAgentTool(IAgentEditingToolBackend backend) : AgentEditingToolBase(backend)
{
    private static readonly AgentToolDescriptor ToolDescriptor = new(
        "update_text",
        "Partially update one existing Agent Draft text clip. Omitted values remain unchanged.",
        AgentToolAccess.Editing,
        AgentToolJson.ParseObject(
            """
            {"type":"object","properties":{"text_clip_id":{"type":"string","format":"uuid"},"start_seconds":{"type":"number","minimum":0},"duration_seconds":{"type":"number","exclusiveMinimum":0},"text":{"type":"string"},"subtitle":{"type":"boolean"},"font_size":{"type":"number","minimum":8,"maximum":240},"x":{"type":"number","minimum":0,"maximum":1},"y":{"type":"number","minimum":0,"maximum":1}},"required":["text_clip_id"],"additionalProperties":false}
            """));
    public override AgentToolDescriptor Descriptor => ToolDescriptor;

    public override async ValueTask<AgentToolExecutionOutput> ExecuteAsync(
        AgentToolContext context, JsonElement arguments, CancellationToken cancellationToken)
    {
        AgentToolJson.EnsureOnlyProperties(arguments, "text_clip_id", "start_seconds", "duration_seconds", "text", "subtitle", "font_size", "x", "y");
        EnsurePatch(arguments);
        var data = await Backend.UpdateTextAsync(
            context,
            AgentToolJson.RequireGuid(arguments, "text_clip_id"),
            OptionalDouble(arguments, "start_seconds", 0),
            OptionalDouble(arguments, "duration_seconds", double.Epsilon),
            OptionalString(arguments, "text"),
            OptionalBoolean(arguments, "subtitle"),
            OptionalDouble(arguments, "font_size", 8, 240),
            OptionalDouble(arguments, "x", 0, 1),
            OptionalDouble(arguments, "y", 0, 1),
            ApprovedPlanReason,
            cancellationToken);
        return Output("Text clip updated.", data);
    }

    private static void EnsurePatch(JsonElement arguments)
    {
        if (arguments.EnumerateObject().Count() <= 1)
            throw new AgentToolInputException("At least one text property must be supplied.");
    }

    internal static double? OptionalDouble(JsonElement arguments, string name, double minimum, double maximum = double.PositiveInfinity)
    {
        if (!arguments.TryGetProperty(name, out _)) return null;
        var value = AgentToolJson.RequireFiniteDouble(arguments, name, minimum);
        if (value > maximum) throw new AgentToolInputException($"'{name}' exceeds its maximum value.");
        return value;
    }

    internal static string? OptionalString(JsonElement arguments, string name)
        => arguments.TryGetProperty(name, out _) ? AgentToolJson.RequireString(arguments, name) : null;

    internal static bool? OptionalBoolean(JsonElement arguments, string name)
        => arguments.TryGetProperty(name, out _) ? AgentToolJson.OptionalBoolean(arguments, name) : null;
}

public sealed class UpdateMarkerAgentTool(IAgentEditingToolBackend backend) : AgentEditingToolBase(backend)
{
    private static readonly AgentToolDescriptor ToolDescriptor = new(
        "update_marker",
        "Partially update one existing neutral note marker in the Agent Draft.",
        AgentToolAccess.Editing,
        AgentToolJson.ParseObject(
            """
            {"type":"object","properties":{"marker_id":{"type":"string","format":"uuid"},"start_seconds":{"type":"number","minimum":0},"duration_seconds":{"type":"number","minimum":0},"title":{"type":"string"},"description":{"type":"string"}},"required":["marker_id"],"additionalProperties":false}
            """));
    public override AgentToolDescriptor Descriptor => ToolDescriptor;

    public override async ValueTask<AgentToolExecutionOutput> ExecuteAsync(
        AgentToolContext context, JsonElement arguments, CancellationToken cancellationToken)
    {
        AgentToolJson.EnsureOnlyProperties(arguments, "marker_id", "start_seconds", "duration_seconds", "title", "description");
        if (arguments.EnumerateObject().Count() <= 1)
            throw new AgentToolInputException("At least one marker property must be supplied.");
        var data = await Backend.UpdateMarkerAsync(
            context,
            AgentToolJson.RequireGuid(arguments, "marker_id"),
            UpdateTextAgentTool.OptionalDouble(arguments, "start_seconds", 0),
            UpdateTextAgentTool.OptionalDouble(arguments, "duration_seconds", 0),
            UpdateTextAgentTool.OptionalString(arguments, "title"),
            UpdateTextAgentTool.OptionalString(arguments, "description"),
            ApprovedPlanReason,
            cancellationToken);
        return Output("Neutral marker updated.", data);
    }
}

public sealed class UpdateTransitionAgentTool(IAgentEditingToolBackend backend) : AgentEditingToolBase(backend)
{
    private static readonly AgentToolDescriptor ToolDescriptor = new(
        "update_transition",
        "Partially update kind or duration of one existing Agent Draft transition.",
        AgentToolAccess.Editing,
        AgentToolJson.ParseObject(
            """
            {"type":"object","properties":{"transition_id":{"type":"string","format":"uuid"},"kind":{"type":"string","enum":["cross_dissolve","dip_to_black","dip_to_white","wipe","slide","constant_power_audio"]},"duration_seconds":{"type":"number","exclusiveMinimum":0}},"required":["transition_id"],"additionalProperties":false}
            """));
    public override AgentToolDescriptor Descriptor => ToolDescriptor;

    public override async ValueTask<AgentToolExecutionOutput> ExecuteAsync(
        AgentToolContext context, JsonElement arguments, CancellationToken cancellationToken)
    {
        AgentToolJson.EnsureOnlyProperties(arguments, "transition_id", "kind", "duration_seconds");
        if (arguments.EnumerateObject().Count() <= 1)
            throw new AgentToolInputException("At least one transition property must be supplied.");
        var data = await Backend.UpdateTransitionAsync(
            context,
            AgentToolJson.RequireGuid(arguments, "transition_id"),
            UpdateTextAgentTool.OptionalString(arguments, "kind"),
            UpdateTextAgentTool.OptionalDouble(arguments, "duration_seconds", double.Epsilon),
            ApprovedPlanReason,
            cancellationToken);
        return Output("Transition updated.", data);
    }
}
