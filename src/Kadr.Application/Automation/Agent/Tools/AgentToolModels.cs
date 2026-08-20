using System.Collections.Immutable;
using System.Text.Json;

namespace KadrStudio.Application.Automation.Agent.Tools;

public sealed record AgentToolDescriptor(
    string Name,
    string Description,
    AgentToolAccess Access,
    JsonElement InputSchema);

public sealed record AgentToolCall(
    Guid Id,
    Guid TaskId,
    string ToolName,
    JsonElement Arguments,
    DateTimeOffset RequestedAt)
{
    public static AgentToolCall Create(
        Guid taskId,
        string toolName,
        JsonElement arguments)
    {
        if (taskId == Guid.Empty)
        {
            throw new ArgumentException("Task id cannot be empty.", nameof(taskId));
        }

        if (string.IsNullOrWhiteSpace(toolName))
        {
            throw new ArgumentException("Tool name cannot be empty.", nameof(toolName));
        }

        return new AgentToolCall(
            Guid.NewGuid(),
            taskId,
            toolName.Trim(),
            arguments.Clone(),
            DateTimeOffset.UtcNow);
    }

    public static AgentToolCall Create(
        Guid taskId,
        string toolName)
        => Create(taskId, toolName, AgentToolJson.EmptyObject());
}

public sealed record AgentToolExecutionOutput(
    string Summary,
    JsonElement Data)
{
    public static AgentToolExecutionOutput From<T>(
        string summary,
        T data)
        => new(summary, AgentToolJson.ToElement(data));
}

public sealed record AgentToolResult(
    Guid CallId,
    string ToolName,
    AgentToolResultStatus Status,
    string Summary,
    JsonElement? Data,
    string? ErrorCode,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt)
{
    public bool IsSuccess => Status == AgentToolResultStatus.Succeeded;

    public TimeSpan Duration => CompletedAt - StartedAt;
}

public sealed record AgentToolContext(
    Guid TaskId,
    Guid ProjectId,
    Guid SourceSequenceId,
    Guid? DraftSequenceId,
    AgentTaskPhase Phase)
{
    public Guid DefaultReadSequenceId =>
        DraftSequenceId ?? SourceSequenceId;

    public static AgentToolContext FromTask(AgentTaskState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new AgentToolContext(
            state.Id,
            state.ProjectId,
            state.SourceSequenceId,
            state.DraftSequenceId,
            state.Phase);
    }
}

public sealed record AgentToolExecutorOptions(
    int MaxObservationCharacters = 48_000)
{
    public static AgentToolExecutorOptions Default { get; } = new();
}
