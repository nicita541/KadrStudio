namespace KadrStudio.Application.Automation.Agent.Runtime;

public interface IAgentModel
{
    ValueTask<AgentModelDecision> DecideAsync(
        AgentModelTurnRequest request,
        CancellationToken cancellationToken);
}
