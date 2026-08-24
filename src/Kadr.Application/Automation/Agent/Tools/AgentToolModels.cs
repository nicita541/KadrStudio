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
    DateTimeOffset RequestedAt,
    Guid? PlanId = null,
    int? PlanVersion = null,
    Guid? PlanStepId = null,
    int? PlanStepOrder = null,
    string? PlanFingerprint = null,
    string? ArgumentsFingerprint = null)
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

    public static AgentToolCall CreateApprovedStep(
        AgentTaskState task,
        AgentPlanStep step)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(step);
        var plan = task.Plan
            ?? throw new AgentTaskTransitionException(
                "Approved editing step requires a plan.");
        if (step.ExpectedEditingArguments is not { ValueKind: JsonValueKind.Object } arguments ||
            string.IsNullOrWhiteSpace(step.ExpectedEditingTool))
        {
            throw new AgentTaskTransitionException(
                "Approved editing step requires a tool and exact arguments.");
        }

        return new AgentToolCall(
            Guid.NewGuid(),
            task.Id,
            step.ExpectedEditingTool.Trim(),
            arguments.Clone(),
            DateTimeOffset.UtcNow,
            plan.Id,
            plan.Version,
            step.Id,
            step.Order,
            AgentPlanFingerprint.Create(plan),
            AgentPlanFingerprint.CreateArguments(
                step.ExpectedEditingTool,
                arguments));
    }

    public static AgentToolCall Create(
        Guid taskId,
        string toolName)
        => Create(taskId, toolName, AgentToolJson.EmptyObject());
}

public sealed record AgentToolExecutionOutput(
    string Summary,
    JsonElement Data,
    AgentEvidenceCapabilities EvidenceCapabilities = AgentEvidenceCapabilities.None)
{
    public static AgentToolExecutionOutput From<T>(
        string summary,
        T data,
        AgentEvidenceCapabilities evidenceCapabilities = AgentEvidenceCapabilities.None)
        => new(summary, AgentToolJson.ToElement(data), evidenceCapabilities);
}

public sealed record AgentToolResult(
    Guid CallId,
    string ToolName,
    AgentToolResultStatus Status,
    string Summary,
    JsonElement? Data,
    string? ErrorCode,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    AgentEvidenceCapabilities EvidenceCapabilities = AgentEvidenceCapabilities.None)
{
    public bool IsSuccess => Status == AgentToolResultStatus.Succeeded;

    public TimeSpan Duration => CompletedAt - StartedAt;
}

public sealed record AgentToolContext(
    Guid TaskId,
    Guid ProjectId,
    Guid SourceSequenceId,
    Guid? DraftSequenceId,
    AgentTaskPhase Phase,
    Guid? PlanId = null,
    int? PlanVersion = null,
    Guid? PlanStepId = null,
    int? PlanStepOrder = null,
    string? PlanFingerprint = null,
    string? ArgumentsFingerprint = null)
{
    public Guid DefaultReadSequenceId =>
        DraftSequenceId ?? SourceSequenceId;

    public bool IsApprovedEditingStep =>
        PlanId.HasValue &&
        PlanVersion.HasValue &&
        PlanStepId.HasValue &&
        PlanStepOrder.HasValue &&
        !string.IsNullOrWhiteSpace(PlanFingerprint) &&
        !string.IsNullOrWhiteSpace(ArgumentsFingerprint);

    public static AgentToolContext FromTask(
        AgentTaskState state,
        AgentToolCall? call = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new AgentToolContext(
            state.Id,
            state.ProjectId,
            state.SourceSequenceId,
            state.DraftSequenceId,
            state.Phase,
            call?.PlanId,
            call?.PlanVersion,
            call?.PlanStepId,
            call?.PlanStepOrder,
            call?.PlanFingerprint,
            call?.ArgumentsFingerprint);
    }
}

public sealed record AgentToolExecutorOptions(
    int MaxObservationCharacters = 48_000)
{
    public static AgentToolExecutorOptions Default { get; } = new();
}
