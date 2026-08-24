using System.Collections.Immutable;
using KadrStudio.Application.Automation.Agent.Runtime;

namespace KadrStudio.Application.Automation.Agent.Planning;

public sealed class TaskBriefService(IAgentTaskInterpreter? interpreter)
{
    public bool IsAvailable => interpreter is not null;

    public ValueTask<AgentTaskUnderstanding> UnderstandAsync(
        AgentModelTurnRequest request,
        CancellationToken cancellationToken = default)
        => interpreter is not null
            ? interpreter.UnderstandAsync(request, cancellationToken)
            : throw new AgentTaskTransitionException(
                "The configured planning model cannot create a task brief.");
}

public sealed class AgentInvestigationRunner(IAgentModel model)
{
    public ValueTask<AgentModelDecision> DecideAsync(
        AgentModelTurnRequest request,
        CancellationToken cancellationToken = default)
        => model.DecideAsync(request, cancellationToken);
}

public sealed record AgentPlanPublicationResult(
    AgentTaskState? PublishedState,
    string Error,
    string ErrorCode)
{
    public bool IsPublished => PublishedState is not null;

    public static AgentPlanPublicationResult Rejected(string error, string errorCode)
        => new(null, error, errorCode);
}

public sealed class AgentPlanPublisher(
    AiAgentOrchestrator orchestrator,
    AgentPlanValidator validator,
    IAgentPlanCritic? critic)
{
    public async ValueTask<AgentPlanPublicationResult> PublishAsync(
        AgentTaskState task,
        AgentPlanDraft plan,
        ImmutableArray<AgentModelObservation> observations,
        ImmutableArray<AgentConversationContextMessage> conversation,
        int turn,
        CancellationToken cancellationToken = default)
    {
        var validation = validator.Validate(task, plan, observations);
        if (!validation.IsValid)
        {
            return AgentPlanPublicationResult.Rejected(
                validation.Error,
                validation.ErrorCode);
        }

        AgentPlanReview? review = null;
        if (critic is not null)
        {
            review = await critic.ReviewPlanAsync(
                new AgentPlanReviewRequest(
                    task,
                    plan,
                    observations,
                    conversation,
                    turn),
                cancellationToken).ConfigureAwait(false);
        }

        var criticRejected = review is not null &&
                             (!review.Accepted || !review.Issues.IsDefaultOrEmpty);
        var criticIssues = !criticRejected
            ? string.Empty
            : review!.Issues.IsDefaultOrEmpty
                ? string.IsNullOrWhiteSpace(review.Summary)
                    ? "Independent plan critic rejected the plan without a usable explanation."
                    : review.Summary
                : string.IsNullOrWhiteSpace(review.Summary)
                    ? string.Join(" ", review.Issues)
                    : review.Summary + " " + string.Join(" ", review.Issues);

        if (!string.IsNullOrWhiteSpace(criticIssues))
        {
            return AgentPlanPublicationResult.Rejected(
                criticIssues,
                "plan_rejected_by_critic");
        }

        if (task.Phase == AgentTaskPhase.Investigating)
        {
            task = orchestrator.BeginPlanning(
                "Agent finished investigation and is preparing the proposed edit plan.");
        }

        var published = task.Plan is null
            ? orchestrator.PublishPlan(plan)
            : orchestrator.RevisePlan(
                plan,
                AgentPlanRevisionSource.Agent,
                "Agent updated the plan from the latest user instructions and evidence.");
        return new AgentPlanPublicationResult(published, string.Empty, string.Empty);
    }
}
