namespace KadrStudio.Application.Automation.Agent.Runtime;

public interface IAgentModel
{
    ValueTask<AgentModelDecision> DecideAsync(
        AgentModelTurnRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Optional first-stage contract. Implementations convert the free-form request
/// and editor facts into a stable task brief before planning starts.
/// </summary>
public interface IAgentTaskInterpreter
{
    ValueTask<AgentTaskUnderstanding> UnderstandAsync(
        AgentModelTurnRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Optional isolated critic. It can reject a proposed plan but cannot execute
/// tools or silently rewrite the plan.
/// </summary>
public interface IAgentPlanCritic
{
    ValueTask<AgentPlanReview> ReviewPlanAsync(
        AgentPlanReviewRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Optional final reporting stage. It receives deterministic verifier results
/// and can describe them, but cannot select or execute tools.
/// </summary>
public interface IAgentVerificationReporter
{
    ValueTask<AgentVerificationReport> ReportVerificationAsync(
        AgentVerificationReportRequest request,
        CancellationToken cancellationToken);
}
