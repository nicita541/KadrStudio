namespace KadrStudio.Application.Automation.Agent.Tools.ReadOnly;

public static class AgentReadOnlyToolSet
{
    public static AgentToolRegistry Create(
        IAgentReadOnlyToolBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);

        var registry = new AgentToolRegistry();
        RegisterDefaults(registry, backend);
        return registry;
    }

    public static void RegisterDefaults(
        AgentToolRegistry registry,
        IAgentReadOnlyToolBackend backend)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(backend);

        registry.Register(new InspectEditorContextTool(backend));
        registry.Register(new InspectProjectTool(backend));
        registry.Register(new InspectTimelineTool(backend));
        registry.Register(new InspectTimelineIntegrityTool(backend));
        registry.Register(new InspectMediaTool(backend));
        registry.Register(new InspectRangeTool(backend));
        registry.Register(new InspectBoundaryTool(backend));
        registry.Register(new InspectObjectsTool(backend));
        registry.Register(new SearchTimelineTool(backend));
        registry.Register(new InspectSequenceOverviewTool(backend));
        registry.Register(new CompareMediaRangesTool(backend));
        registry.Register(new CompareSequencesTool(backend));
    }
}
