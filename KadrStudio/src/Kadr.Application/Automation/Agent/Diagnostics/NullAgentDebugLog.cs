namespace KadrStudio.Application.Automation.Agent.Diagnostics;

public sealed class NullAgentDebugLog : IAgentDebugLog
{
    public static NullAgentDebugLog Instance { get; } = new();

    private NullAgentDebugLog()
    {
    }

    public string? CurrentLogPath => null;

    public void Write(AgentDebugLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
    }
}
