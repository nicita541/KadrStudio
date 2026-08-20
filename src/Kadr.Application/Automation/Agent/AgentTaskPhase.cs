namespace KadrStudio.Application.Automation.Agent;

public enum AgentTaskPhase
{
    Understanding,
    Investigating,
    WaitingForUserInput,
    Planning,
    WaitingForApproval,
    Approved,
    Executing,
    Verifying,
    Completed,
    Failed,
    Stopped
}
