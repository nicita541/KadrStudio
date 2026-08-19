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

public enum AgentModelActionKind
{
    UseTool,
    AskUser,
    PublishPlan
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
    int TurnIndex);

public sealed record AgentModelDecision(
    AgentModelActionKind Action,
    string Progress,
    string ToolName,
    JsonElement ToolArguments,
    string Question,
    string QuestionContext,
    AgentPlanDraft? Plan)
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
            null);

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
            null);

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
            plan ?? throw new ArgumentNullException(nameof(plan)));
}
