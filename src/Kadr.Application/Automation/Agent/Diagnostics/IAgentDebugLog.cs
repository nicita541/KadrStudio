namespace KadrStudio.Application.Automation.Agent.Diagnostics;

public interface IAgentDebugLog
{
    string? CurrentLogPath { get; }

    void Write(AgentDebugLogEntry entry);
}
