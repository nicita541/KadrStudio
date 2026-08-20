using System.Text.Json;

namespace KadrStudio.Application.Automation.Agent.Tools.ReadOnly;

public sealed class InspectRangeTool : IAgentTool
{
    private static readonly AgentToolDescriptor ToolDescriptor = new(
        "inspect_range",
        "Inspect only a requested time range of media or a sequence. summary returns structure only and cannot answer semantic questions. Use frames/audio/transcript/all as evidence and narrow large ranges before semantic analysis.",
        AgentToolAccess.ReadOnly,
        AgentToolJson.ParseObject(
            """
            {
              "type": "object",
              "properties": {
                "target_kind": {
                  "type": "string",
                  "enum": ["media", "sequence"]
                },
                "target_id": {
                  "type": "string",
                  "format": "uuid"
                },
                "start_seconds": {
                  "type": "number",
                  "minimum": 0
                },
                "end_seconds": {
                  "type": "number",
                  "exclusiveMinimum": 0
                },
                "detail": {
                  "type": "string",
                  "enum": ["summary", "frames", "audio", "transcript", "all"],
                  "default": "summary"
                },
                "query": {
                  "type": "string",
                  "maxLength": 2000,
                  "description": "Optional semantic question for this exact range. Keep it task-specific and concise."
                }
              },
              "required": [
                "target_kind",
                "target_id",
                "start_seconds",
                "end_seconds"
              ],
              "additionalProperties": false
            }
            """));

    private readonly IAgentReadOnlyToolBackend _backend;

    public InspectRangeTool(IAgentReadOnlyToolBackend backend)
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
            "target_kind",
            "target_id",
            "start_seconds",
            "end_seconds",
            "detail",
            "query");

        var targetKind = ParseTargetKind(
            AgentToolJson.RequireString(arguments, "target_kind"));
        var targetId = AgentToolJson.RequireGuid(
            arguments,
            "target_id");
        var startSeconds = AgentToolJson.RequireFiniteDouble(
            arguments,
            "start_seconds",
            0);
        var endSeconds = AgentToolJson.RequireFiniteDouble(
            arguments,
            "end_seconds",
            0);

        if (endSeconds <= startSeconds)
        {
            throw new AgentToolInputException(
                "'end_seconds' must be greater than 'start_seconds'.");
        }

        var detail = ParseDetail(
            AgentToolJson.OptionalString(arguments, "detail"));
        var query = AgentToolJson.OptionalString(arguments, "query") ?? string.Empty;
        if (detail == AgentRangeInspectionDetail.Summary &&
            !string.IsNullOrWhiteSpace(query))
        {
            throw new AgentToolInputException(
                "'summary' returns structure only and cannot answer a semantic query. Narrow the range and choose frames, audio, transcript or all.");
        }

        var request = new AgentRangeInspectionRequest(
            targetKind,
            targetId,
            startSeconds,
            endSeconds,
            detail,
            query);

        var data = await _backend.InspectRangeAsync(
            context,
            request,
            cancellationToken);

        return new AgentToolExecutionOutput(
            $"Range inspection completed for {targetKind.ToString().ToLowerInvariant()} '{targetId}' from {startSeconds:0.###}s to {endSeconds:0.###}s.",
            data.Clone());
    }

    private static AgentRangeTargetKind ParseTargetKind(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "media" => AgentRangeTargetKind.Media,
            "sequence" => AgentRangeTargetKind.Sequence,
            _ => throw new AgentToolInputException(
                "'target_kind' must be 'media' or 'sequence'.")
        };

    private static AgentRangeInspectionDetail ParseDetail(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            null or "" => AgentRangeInspectionDetail.Summary,
            "summary" => AgentRangeInspectionDetail.Summary,
            "frames" => AgentRangeInspectionDetail.Frames,
            "audio" => AgentRangeInspectionDetail.Audio,
            "transcript" => AgentRangeInspectionDetail.Transcript,
            "all" => AgentRangeInspectionDetail.All,
            _ => throw new AgentToolInputException(
                "'detail' must be summary, frames, audio, transcript or all.")
        };
}
