using System.Collections.Immutable;
using System.Text.Json;
using KadrStudio.Application.Automation.Agent.Tools;

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

public enum AgentModelTurnMode
{
    Planning,
    Execution,
    Verification
}

public enum AgentModelActionKind
{
    UseTool,
    AskUser,
    PublishPlan,
    BeginVerification,
    CompleteTask
}

public sealed record AgentModelObservation(
    int Sequence,
    string ToolName,
    AgentToolResultStatus Status,
    string Summary,
    JsonElement? Data,
    string? ErrorCode)
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
            result.ErrorCode);
    }
}

public sealed record AgentModelTurnRequest(
    AgentTaskState Task,
    ImmutableArray<AgentToolDescriptor> AvailableTools,
    ImmutableArray<AgentModelObservation> Observations,
    ImmutableArray<AgentConversationContextMessage> Conversation,
    int TurnIndex,
    AgentModelTurnMode Mode = AgentModelTurnMode.Planning);

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

    public static AgentModelDecision BeginVerification(
        string progress = "")
        => new(
            AgentModelActionKind.BeginVerification,
            progress ?? string.Empty,
            string.Empty,
            AgentToolJson.EmptyObject(),
            string.Empty,
            string.Empty,
            null,
            string.Empty);

    public static AgentModelDecision CompleteTask(
        string summary,
        string progress = "")
        => new(
            AgentModelActionKind.CompleteTask,
            progress ?? string.Empty,
            string.Empty,
            AgentToolJson.EmptyObject(),
            string.Empty,
            string.Empty,
            null,
            summary ?? string.Empty);
}
