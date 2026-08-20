using System.Text.Json;

namespace KadrStudio.Application.Automation.Agent.Tools.ReadOnly;

public sealed class InspectBoundaryTool(IAgentReadOnlyToolBackend backend) : IAgentTool
{
    private static readonly AgentToolDescriptor ToolDescriptor = new(
        "inspect_boundary",
        "Inspect a narrow window around a candidate boundary. Returns measured evidence on both sides; it does not decide whether the boundary should be edited.",
        AgentToolAccess.ReadOnly,
        AgentToolJson.ParseObject(
            """
            {
              "type":"object",
              "properties":{
                "target_kind":{"type":"string","enum":["media","sequence"]},
                "target_id":{"type":"string","format":"uuid"},
                "at_seconds":{"type":"number","minimum":0},
                "window_seconds":{"type":"number","exclusiveMinimum":0,"maximum":120,"default":8},
                "detail":{"type":"string","enum":["frames","audio","transcript","all"],"default":"all"},
                "query":{"type":"string","maxLength":2000}
              },
              "required":["target_kind","target_id","at_seconds"],
              "additionalProperties":false
            }
            """));

    public AgentToolDescriptor Descriptor => ToolDescriptor;

    public async ValueTask<AgentToolExecutionOutput> ExecuteAsync(
        AgentToolContext context,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        AgentToolJson.EnsureOnlyProperties(
            arguments, "target_kind", "target_id", "at_seconds", "window_seconds", "detail", "query");
        var targetKind = AgentToolJson.RequireString(arguments, "target_kind").ToLowerInvariant() switch
        {
            "media" => AgentRangeTargetKind.Media,
            "sequence" => AgentRangeTargetKind.Sequence,
            _ => throw new AgentToolInputException("'target_kind' must be media or sequence.")
        };
        var targetId = AgentToolJson.RequireGuid(arguments, "target_id");
        var at = AgentToolJson.RequireFiniteDouble(arguments, "at_seconds", 0);
        var window = arguments.TryGetProperty("window_seconds", out _)
            ? AgentToolJson.RequireFiniteDouble(arguments, "window_seconds", double.Epsilon)
            : 8;
        if (window > 120)
        {
            throw new AgentToolInputException("'window_seconds' cannot exceed 120 seconds.");
        }

        var detail = (AgentToolJson.OptionalString(arguments, "detail") ?? "all").ToLowerInvariant() switch
        {
            "frames" => AgentRangeInspectionDetail.Frames,
            "audio" => AgentRangeInspectionDetail.Audio,
            "transcript" => AgentRangeInspectionDetail.Transcript,
            "all" => AgentRangeInspectionDetail.All,
            _ => throw new AgentToolInputException("'detail' must be frames, audio, transcript or all.")
        };
        var request = new AgentRangeInspectionRequest(
            targetKind,
            targetId,
            Math.Max(0, at - window),
            at + window,
            detail,
            AgentToolJson.OptionalString(arguments, "query") ??
            "Describe only measured differences and continuity immediately before and after the candidate boundary.");
        var data = await backend.InspectRangeAsync(context, request, cancellationToken);
        return new AgentToolExecutionOutput(
            $"Boundary at {at:0.###}s inspected within a ±{window:0.###}s window.",
            data.Clone());
    }
}
