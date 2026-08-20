using KadrStudio.Application.Automation.Agent.Diagnostics;

namespace KadrStudio.Application.Automation.Agent.Tools;

/// <summary>
/// Safe execution boundary for agent tools.
///
/// Read-only tools are available while the task is being researched or
/// verified. Editing tools are allowed only while the agent owns a separate
/// draft sequence during execution/verification.
/// </summary>
public sealed class AgentToolExecutor
{
    private static readonly HashSet<AgentTaskPhase> AllowedReadPhases =
    [
        AgentTaskPhase.Understanding,
        AgentTaskPhase.Investigating,
        AgentTaskPhase.Planning,
        AgentTaskPhase.Executing,
        AgentTaskPhase.Verifying
    ];

    private readonly AgentToolRegistry _registry;
    private readonly AgentToolExecutorOptions _options;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly IAgentDebugLog _debugLog;

    public AgentToolExecutor(
        AgentToolRegistry registry,
        AgentToolExecutorOptions? options = null,
        Func<DateTimeOffset>? utcNow = null,
        IAgentDebugLog? debugLog = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _options = options ?? AgentToolExecutorOptions.Default;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _debugLog = debugLog ?? NullAgentDebugLog.Instance;

        if (_options.MaxObservationCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Max observation size must be positive.");
        }
    }

    public async ValueTask<AgentToolResult> ExecuteAsync(
        AgentTaskState task,
        AgentToolCall call,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(call);

        var startedAt = _utcNow();

        _debugLog.Write(new AgentDebugLogEntry(
            startedAt,
            "tool_executor",
            "tool_call_requested",
            task.Id,
            task.Phase.ToString(),
            Message: call.ToolName,
            Details: SafeJson(call.Arguments)));

        if (call.TaskId != task.Id)
        {
            return Rejected(
                task,
                call,
                "task_mismatch",
                "Tool call does not belong to the active agent task.",
                startedAt);
        }

        if (task.IsTerminal)
        {
            return Rejected(
                task,
                call,
                "task_terminal",
                "Tools cannot run after the agent task has finished.",
                startedAt);
        }

        if (!AllowedReadPhases.Contains(task.Phase))
        {
            return Rejected(
                task,
                call,
                "phase_not_executable",
                $"Tools cannot run while the agent task is in phase '{task.Phase}'.",
                startedAt);
        }

        if (!_registry.TryGet(call.ToolName, out var tool) || tool is null)
        {
            return Rejected(
                task,
                call,
                "tool_not_found",
                $"Agent tool '{call.ToolName}' is not registered.",
                startedAt);
        }

        if (tool.Descriptor.Access == AgentToolAccess.Editing)
        {
            if (task.Phase is not (AgentTaskPhase.Executing or AgentTaskPhase.Verifying))
            {
                return Rejected(
                    task,
                    call,
                    "editing_phase_required",
                    "Editing tools are available only while executing or verifying an approved agent draft.",
                    startedAt);
            }

            if (task.DraftSequenceId is not { } draftSequenceId ||
                draftSequenceId == task.SourceSequenceId)
            {
                return Rejected(
                    task,
                    call,
                    "draft_required",
                    "Editing tools require a separate agent draft sequence.",
                    startedAt);
            }
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var context = AgentToolContext.FromTask(task);
            var output = await tool.ExecuteAsync(
                context,
                call.Arguments,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(output.Summary))
            {
                return Failed(
                    task,
                    call,
                    "invalid_tool_output",
                    "Tool returned an empty summary.",
                    startedAt);
            }

            var rawData = output.Data.GetRawText();
            if (rawData.Length > _options.MaxObservationCharacters)
            {
                return Rejected(
                    task,
                    call,
                    "observation_too_large",
                    $"Tool observation exceeded the {_options.MaxObservationCharacters} character limit.",
                    startedAt);
            }

            var result = new AgentToolResult(
                call.Id,
                tool.Descriptor.Name,
                AgentToolResultStatus.Succeeded,
                output.Summary.Trim(),
                output.Data.Clone(),
                null,
                startedAt,
                _utcNow());

            LogResult(task, result);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AgentToolInputException exception)
        {
            return Rejected(
                task,
                call,
                "invalid_arguments",
                exception.Message,
                startedAt);
        }
        catch (AgentToolRejectedException exception)
        {
            return Rejected(
                task,
                call,
                exception.ErrorCode,
                exception.Message,
                startedAt);
        }
        catch (Exception exception)
        {
            _debugLog.Write(new AgentDebugLogEntry(
                _utcNow(),
                "tool_executor",
                "tool_exception",
                task.Id,
                task.Phase.ToString(),
                Message: call.ToolName,
                Details: SafeJson(call.Arguments),
                Exception: exception.ToString()));

            return Failed(
                task,
                call,
                "tool_failed",
                exception.Message,
                startedAt);
        }
    }

    private AgentToolResult Rejected(
        AgentTaskState task,
        AgentToolCall call,
        string errorCode,
        string summary,
        DateTimeOffset startedAt)
    {
        var result = new AgentToolResult(
            call.Id,
            call.ToolName,
            AgentToolResultStatus.Rejected,
            summary,
            null,
            errorCode,
            startedAt,
            _utcNow());

        LogResult(task, result);
        return result;
    }

    private AgentToolResult Failed(
        AgentTaskState task,
        AgentToolCall call,
        string errorCode,
        string summary,
        DateTimeOffset startedAt)
    {
        var result = new AgentToolResult(
            call.Id,
            call.ToolName,
            AgentToolResultStatus.Failed,
            summary,
            null,
            errorCode,
            startedAt,
            _utcNow());

        LogResult(task, result);
        return result;
    }

    private static string SafeJson(System.Text.Json.JsonElement value)
    {
        try
        {
            return value.ValueKind == System.Text.Json.JsonValueKind.Undefined
                ? "<undefined>"
                : value.GetRawText();
        }
        catch
        {
            return "<unavailable>";
        }
    }

    private void LogResult(
        AgentTaskState task,
        AgentToolResult result)
    {
        _debugLog.Write(new AgentDebugLogEntry(
            result.CompletedAt,
            "tool_executor",
            "tool_result",
            task.Id,
            task.Phase.ToString(),
            Message:
                $"{result.ToolName}: {result.Status}" +
                (string.IsNullOrWhiteSpace(result.ErrorCode)
                    ? string.Empty
                    : $" ({result.ErrorCode})"),
            Details:
                $"summary={result.Summary}\n" +
                $"duration_ms={result.Duration.TotalMilliseconds:0.###}\n" +
                $"data={(result.Data is { } data ? data.GetRawText() : "null")}"));
    }
}
