using System.Text.Json;

namespace KadrStudio.Application.Automation.Agent.Tools.ReadOnly;

public sealed class InspectProjectTool : IAgentTool
{
    private static readonly AgentToolDescriptor ToolDescriptor = new(
        "inspect_project",
        "Read project-level facts needed to understand the task: project identity, sequences, media references and basic state. This tool never edits the project.",
        AgentToolAccess.ReadOnly,
        AgentToolJson.ParseObject(
            """
            {
              "type": "object",
              "properties": {},
              "additionalProperties": false
            }
            """));

    private readonly IAgentReadOnlyToolBackend _backend;

    public InspectProjectTool(IAgentReadOnlyToolBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public AgentToolDescriptor Descriptor => ToolDescriptor;

    public async ValueTask<AgentToolExecutionOutput> ExecuteAsync(
        AgentToolContext context,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        AgentToolJson.EnsureOnlyProperties(arguments);

        var data = await _backend.InspectProjectAsync(
            context,
            cancellationToken);

        return new AgentToolExecutionOutput(
            "Project inspection completed.",
            data.Clone());
    }
}
