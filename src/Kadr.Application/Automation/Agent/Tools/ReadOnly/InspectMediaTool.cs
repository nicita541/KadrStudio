using System.Text.Json;

namespace KadrStudio.Application.Automation.Agent.Tools.ReadOnly;

public sealed class InspectMediaTool : IAgentTool
{
    private static readonly AgentToolDescriptor ToolDescriptor = new(
        "inspect_media",
        "Read metadata and existing analysis facts for one media item. Use ids returned by project or timeline inspection. This tool never edits media or the project.",
        AgentToolAccess.ReadOnly,
        AgentToolJson.ParseObject(
            """
            {
              "type": "object",
              "properties": {
                "media_id": {
                  "type": "string",
                  "format": "uuid"
                }
              },
              "required": ["media_id"],
              "additionalProperties": false
            }
            """));

    private readonly IAgentReadOnlyToolBackend _backend;

    public InspectMediaTool(IAgentReadOnlyToolBackend backend)
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
            "media_id");

        var mediaId = AgentToolJson.RequireGuid(
            arguments,
            "media_id");

        var data = await _backend.InspectMediaAsync(
            context,
            mediaId,
            cancellationToken);

        return new AgentToolExecutionOutput(
            $"Media inspection completed for '{mediaId}'.",
            data.Clone());
    }
}
