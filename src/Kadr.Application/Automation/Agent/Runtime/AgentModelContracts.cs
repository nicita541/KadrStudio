using System.Collections.Immutable;
using System.Text.Json;
using KadrStudio.Application.Automation.Agent.Tools;
using KadrStudio.Application.Automation.Agent.Verification;

namespace KadrStudio.Application.Automation.Agent.Runtime;

public enum AgentConversationRole
{
    User,
    Assistant
}

public sealed record AgentConversationContextMessage(
    AgentConversationRole Role,
    string Text,
    DateTimeOffset CreatedAt);

public enum AgentModelActionKind
{
    UseTool,
    AskUser,
    PublishPlan,
    CompleteReadOnly
}

public sealed record AgentModelObservation(
    int Sequence,
    string ToolName,
    AgentToolResultStatus Status,
    string Summary,
    JsonElement? Data,
    string? ErrorCode,
    AgentEvidenceCapabilities EvidenceCapabilities = AgentEvidenceCapabilities.None)
{
    public static AgentModelObservation FromResult(
        int sequence,
        AgentToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new AgentModelObservation(
            sequence,
            result.ToolName,
            result.Status,
            result.Summary,
            result.Data is { } data ? data.Clone() : null,
            result.ErrorCode,
            result.EvidenceCapabilities);
    }
}

public enum AgentModelTurnDirective
{
    Investigate,
    PublishPlanFromExistingEvidence
}

public sealed record AgentModelTurnRequest(
    AgentTaskState Task,
    ImmutableArray<AgentToolDescriptor> AvailableTools,
    ImmutableArray<AgentModelObservation> Observations,
    ImmutableArray<AgentConversationContextMessage> Conversation,
    int TurnIndex,
    AgentModelTurnDirective Directive = AgentModelTurnDirective.Investigate);

public sealed record AgentTaskUnderstanding(
    AgentTaskBrief Brief,
    ImmutableArray<AgentQuestion> Questions);

public sealed record AgentPlanReviewRequest(
    AgentTaskState Task,
    AgentPlanDraft Plan,
    ImmutableArray<AgentModelObservation> Observations,
    ImmutableArray<AgentConversationContextMessage> Conversation,
    int TurnIndex);

public sealed record AgentPlanReview(
    bool Accepted,
    string Summary,
    ImmutableArray<string> Issues);

public sealed record AgentVerificationReportRequest(
    AgentTaskState Task,
    AgentDeterministicVerificationResult DeterministicResult,
    ImmutableArray<AgentModelObservation> VerificationObservations,
    int TurnIndex);

public sealed record AgentVerificationReport(
    bool Accepted,
    string Summary,
    ImmutableArray<string> Issues);

public sealed record AgentModelDecision(
    AgentModelActionKind Action,
    string Progress,
    string ToolName,
    JsonElement ToolArguments,
    string Question,
    string QuestionContext,
    AgentPlanDraft? Plan,
    string CompletionSummary)
{
    public static AgentModelDecision UseTool(
        string toolName,
        JsonElement arguments,
        string progress = "")
        => new(
            AgentModelActionKind.UseTool,
            progress ?? string.Empty,
            toolName ?? string.Empty,
            arguments.Clone(),
            string.Empty,
            string.Empty,
            null,
            string.Empty);

    public static AgentModelDecision AskUser(
        string question,
        string? context = null,
        string progress = "")
        => new(
            AgentModelActionKind.AskUser,
            progress ?? string.Empty,
            string.Empty,
            AgentToolJson.EmptyObject(),
            question ?? string.Empty,
            context ?? string.Empty,
            null,
            string.Empty);

    public static AgentModelDecision PublishPlan(
        AgentPlanDraft plan,
        string progress = "")
        => new(
            AgentModelActionKind.PublishPlan,
            progress ?? string.Empty,
            string.Empty,
            AgentToolJson.EmptyObject(),
            string.Empty,
            string.Empty,
            plan ?? throw new ArgumentNullException(nameof(plan)),
            string.Empty);

    public static AgentModelDecision CompleteReadOnly(
        string summary,
        string progress = "")
        => new(
            AgentModelActionKind.CompleteReadOnly,
            progress ?? string.Empty,
            string.Empty,
            AgentToolJson.EmptyObject(),
            string.Empty,
            string.Empty,
            null,
            summary ?? string.Empty);

}
