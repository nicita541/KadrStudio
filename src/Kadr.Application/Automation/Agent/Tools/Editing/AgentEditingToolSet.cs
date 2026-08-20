namespace KadrStudio.Application.Automation.Agent.Tools.Editing;

public static class AgentEditingToolSet
{
    public static void RegisterDefaults(
        AgentToolRegistry registry,
        IAgentEditingToolBackend backend)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(backend);

        registry.Register(new RippleDeleteRangeAgentTool(backend));
        registry.Register(new RippleDeleteRangesAgentTool(backend));
        registry.Register(new SplitClipsAgentTool(backend));
        registry.Register(new DeleteClipsAgentTool(backend));
        registry.Register(new TrimClipAgentTool(backend));
        registry.Register(new MoveClipAgentTool(backend));
        registry.Register(new SetClipVolumeAgentTool(backend));
        registry.Register(new SetClipVideoAgentTool(backend));
        registry.Register(new SetClipAudioAgentTool(backend));
        registry.Register(new UnlinkClipsAgentTool(backend));
        registry.Register(new DeleteTimelineObjectsAgentTool(backend));
        registry.Register(new AddMarkerAgentTool(backend));
        registry.Register(new AddTextAgentTool(backend));
        registry.Register(new UpdateTextAgentTool(backend));
        registry.Register(new AddTransitionAgentTool(backend));
        registry.Register(new UpdateTransitionAgentTool(backend));
        registry.Register(new UpdateMarkerAgentTool(backend));
        registry.Register(new InsertSourceRangeAgentTool(backend));
        registry.Register(new InspectAgentEditsTool(backend));
    }
}
