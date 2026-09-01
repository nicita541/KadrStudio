namespace KadrStudio.Application.Automation.Agent;

public sealed class AgentTaskTransitionException : InvalidOperationException
{
    public AgentTaskTransitionException(string message)
        : base(message)
    {
    }
}
