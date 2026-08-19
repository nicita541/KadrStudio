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

    public AgentToolExecutor(
        AgentToolRegistry registry,
        AgentToolExecutorOptions? options = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _options = options ?? AgentToolExecutorOptions.Default;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

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

        if (call.TaskId != task.Id)
        {
            return Rejected(
                call,
                "task_mismatch",
                "Tool call does not belong to the active agent task.",
                startedAt);
        }

        if (task.IsTerminal)
        {
            return Rejected(
                call,
                "task_terminal",
                "Tools cannot run after the agent task has finished.",
                startedAt);
        }

        if (!AllowedReadPhases.Contains(task.Phase))
        {
            return Rejected(
                call,
                "phase_not_executable",
                $"Tools cannot run while the agent task is in phase '{task.Phase}'.",
                startedAt);
        }

        if (!_registry.TryGet(call.ToolName, out var tool) || tool is null)
        {
            return Rejected(
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
                    call,
                    "editing_phase_required",
                    "Editing tools are available only while executing or verifying an approved agent draft.",
                    startedAt);
            }

            if (task.DraftSequenceId is not { } draftSequenceId ||
                draftSequenceId == task.SourceSequenceId)
            {
                return Rejected(
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
                    call,
                    "invalid_tool_output",
                    "Tool returned an empty summary.",
                    startedAt);
            }

            var rawData = output.Data.GetRawText();
            if (rawData.Length > _options.MaxObservationCharacters)
            {
                return Rejected(
                    call,
                    "observation_too_large",
                    $"Tool observation exceeded the {_options.MaxObservationCharacters} character limit.",
                    startedAt);
            }

            return new AgentToolResult(
                call.Id,
                tool.Descriptor.Name,
                AgentToolResultStatus.Succeeded,
                output.Summary.Trim(),
                output.Data.Clone(),
                null,
                startedAt,
                _utcNow());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AgentToolInputException exception)
        {
            return Rejected(
                call,
                "invalid_arguments",
                exception.Message,
                startedAt);
        }
        catch (AgentToolRejectedException exception)
        {
            return Rejected(
                call,
                exception.ErrorCode,
                exception.Message,
                startedAt);
        }
        catch (Exception exception)
        {
            return Failed(
                call,
                "tool_failed",
                exception.Message,
                startedAt);
        }
    }

    private AgentToolResult Rejected(
        AgentToolCall call,
        string errorCode,
        string summary,
        DateTimeOffset startedAt)
        => new(
            call.Id,
            call.ToolName,
            AgentToolResultStatus.Rejected,
            summary,
            null,
            errorCode,
            startedAt,
            _utcNow());

    private AgentToolResult Failed(
        AgentToolCall call,
        string errorCode,
        string summary,
        DateTimeOffset startedAt)
        => new(
            call.Id,
            call.ToolName,
            AgentToolResultStatus.Failed,
            summary,
            null,
            errorCode,
            startedAt,
            _utcNow());
}
