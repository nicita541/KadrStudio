using System.Collections.Immutable;
using System.Text.Json;
using KadrStudio.Application.Automation.Agent.Diagnostics;
using KadrStudio.Application.Automation.Agent.Execution;
using KadrStudio.Application.Automation.Agent.Tools;
using KadrStudio.Application.Automation.Agent.Verification;
using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Automation.Agent.Runtime;

/// <summary>
/// Executes an already approved plan on a separate Agent Draft and then verifies
/// that draft. Editing and verification tool calls are derived deterministically
/// from the approved plan; the model can only phrase the final report.
/// </summary>
public sealed class AgentExecutionLoop
{
    private readonly AiAgentOrchestrator _orchestrator;
    private readonly AgentToolRegistry _registry;
    private readonly AgentToolExecutor _toolExecutor;
    private readonly IAgentVerificationReporter? _reporter;
    private readonly AgentExecutionLoopOptions _options;
    private readonly Func<ImmutableArray<AgentModelObservation>> _seedObservationProvider;
    private readonly IAgentCheckpointStore? _checkpointStore;
    private readonly ApprovedPlanRunner _approvedPlanRunner;
    private readonly AgentVerificationEngine _verificationEngine = new();
    private readonly IAgentDebugLog _debugLog;
    private readonly SemaphoreSlim _runGate = new(1, 1);

    private readonly List<AgentModelObservation> _observations = [];
    private Guid? _memoryTaskId;
    private int _nextObservationSequence = 1;
    private int _successfulEditingActions;
    private bool _verificationEditLogObserved;
    private readonly List<string> _successfulEditingToolNames = [];
    private readonly HashSet<Guid> _executedPlanStepIds = [];

    public AgentExecutionLoop(
        AiAgentOrchestrator orchestrator,
        AgentToolRegistry registry,
        AgentToolExecutor toolExecutor,
        IAgentVerificationReporter? reporter,
        AgentExecutionLoopOptions? options = null,
        Func<ImmutableArray<AgentModelObservation>>? seedObservationProvider = null,
        IAgentCheckpointStore? checkpointStore = null,
        IAgentDebugLog? debugLog = null)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _toolExecutor = toolExecutor ?? throw new ArgumentNullException(nameof(toolExecutor));
        _reporter = reporter;
        _options = options ?? AgentExecutionLoopOptions.Default;
        _seedObservationProvider =
            seedObservationProvider ?? (() => ImmutableArray<AgentModelObservation>.Empty);
        _checkpointStore = checkpointStore;
        _approvedPlanRunner = new ApprovedPlanRunner(
            _orchestrator,
            _registry,
            _toolExecutor);
        _debugLog = debugLog ?? NullAgentDebugLog.Instance;
        _options.Validate();
    }

    public ImmutableArray<AgentModelObservation> Observations
    {
        get
        {
            lock (_observations)
            {
                return _observations.ToImmutableArray();
            }
        }
    }

    public async Task<AgentTaskState> RunUntilPauseAsync(
        CancellationToken cancellationToken = default)
    {
        await _runGate.WaitAsync(cancellationToken);
        try
        {
            var task = RequireCurrentTask();
            EnsureMemoryFor(task.Id);

            Log(
                task,
                "run_started",
                message: "Execution/verification loop started or resumed.");

            if (task.IsTerminal ||
                task.Phase == AgentTaskPhase.WaitingForUserInput)
            {
                return task;
            }

            if (task.Phase is not (
                    AgentTaskPhase.Executing or
                    AgentTaskPhase.Verifying))
            {
                throw new AgentTaskTransitionException(
                    "Execution loop requires an executing or verifying Agent Draft.");
            }

            if (!HasDeterministicEditingPlan(task))
            {
                return FailTask(
                    "Approved plan has no complete deterministic editing actions.");
            }

            RestoreCommittedSteps(task);
            if (task.Phase == AgentTaskPhase.Executing)
            {
                var execution = await _approvedPlanRunner.RunAsync(
                    task,
                    _executedPlanStepIds,
                    cancellationToken).ConfigureAwait(false);
                foreach (var result in execution.Results)
                {
                    AddObservation(AgentModelObservation.FromResult(
                        _nextObservationSequence++,
                        result));
                    if (result.IsSuccess)
                    {
                        _successfulEditingActions++;
                        _successfulEditingToolNames.Add(result.ToolName);
                    }
                }
                foreach (var stepId in execution.CompletedStepIds)
                {
                    _executedPlanStepIds.Add(stepId);
                }
                if (!execution.IsSuccess)
                {
                    return FailTask(execution.Error!);
                }

                _verificationEditLogObserved = false;
                task = _orchestrator.BeginVerification(
                    "Утверждённые действия выполнены один раз; запускаю обязательную проверку.");
                _checkpointStore?.SetStatus(task, AgentDraftExecutionStatus.Verifying);
            }

            var verificationError = await RunAutomaticVerificationAsync(
                task,
                cancellationToken).ConfigureAwait(false);
            if (verificationError is not null)
            {
                return FailTask(verificationError);
            }

            var deterministic = _verificationEngine.Verify(
                task,
                task.DraftSequenceId is { } checkpointDraftId
                    ? _checkpointStore?.Read(checkpointDraftId)
                    : null,
                GetObservationContext());
            if (!deterministic.IsValid)
            {
                return FailTask(
                    $"Проверка Agent Draft не пройдена: {string.Join(" ", deterministic.Issues)}");
            }

            var report = await BuildVerificationReportAsync(
                task,
                deterministic,
                cancellationToken).ConfigureAwait(false);
            var summary = string.IsNullOrWhiteSpace(report.Summary)
                ? deterministic.Summary
                : report.Summary;
            var completed = _orchestrator.Complete(LimitText(summary, 4_000));
            _checkpointStore?.SetStatus(completed, AgentDraftExecutionStatus.Completed);
            return completed;
        }
        finally
        {
            _runGate.Release();
        }
    }

    private static bool HasDeterministicEditingPlan(AgentTaskState task)
    {
        var editingSteps = task.Plan?.Steps
            .Where(step => !string.IsNullOrWhiteSpace(step.ExpectedEditingTool))
            .ToArray() ?? [];
        return editingSteps.Length > 0 &&
               editingSteps.All(step =>
                   step.ExpectedEditingArguments is { ValueKind: JsonValueKind.Object });
    }

    private void RestoreCommittedSteps(AgentTaskState task)
    {
        if (_checkpointStore is null || task.DraftSequenceId is not { } draftId)
        {
            return;
        }

        var checkpoint = _checkpointStore.Read(draftId);
        if (checkpoint is null || task.Plan is null ||
            checkpoint.TaskId != task.Id ||
            checkpoint.PlanId != task.Plan.Id ||
            checkpoint.PlanVersion != task.Plan.Version ||
            !string.Equals(
                checkpoint.PlanFingerprint,
                AgentPlanFingerprint.Create(task.Plan),
                StringComparison.Ordinal))
        {
            return;
        }

        foreach (var receipt in checkpoint.Receipts.OrderBy(receipt => receipt.Order))
        {
            if (_executedPlanStepIds.Add(receipt.StepId))
            {
                _successfulEditingActions++;
                _successfulEditingToolNames.Add(receipt.ToolName);
            }
        }
    }

    private async Task<string?> RunAutomaticVerificationAsync(
        AgentTaskState task,
        CancellationToken cancellationToken)
    {
        if (task.DraftSequenceId is not { } draftId)
        {
            return "Agent Draft disappeared before verification.";
        }

        var checks = new (string ToolName, JsonElement Arguments)[]
        {
            ("inspect_agent_edits", AgentToolJson.EmptyObject()),
            ("inspect_timeline_integrity", AgentToolJson.ToElement(new { sequence_id = draftId })),
            ("compare_sequences", AgentToolJson.ToElement(new
            {
                source_sequence_id = task.SourceSequenceId,
                draft_sequence_id = draftId
            }))
        };

        foreach (var check in checks)
        {
            if (!_registry.TryGet(check.ToolName, out var tool) || tool is null)
            {
                return $"Required verification tool '{check.ToolName}' is unavailable.";
            }

            var observation = await ExecuteVerificationToolAsync(
                check.ToolName,
                check.Arguments,
                cancellationToken).ConfigureAwait(false);
            if (observation is null ||
                !string.Equals(observation.ToolName, check.ToolName, StringComparison.OrdinalIgnoreCase) ||
                observation.Status != AgentToolResultStatus.Succeeded)
            {
                return $"Required verification '{check.ToolName}' failed: {observation?.Summary ?? "no result"}";
            }

            if (string.Equals(check.ToolName, "compare_sequences", StringComparison.OrdinalIgnoreCase) &&
                task.SourceSequenceRevision is { } expectedRevision &&
                observation.Data is { } data &&
                data.TryGetProperty("source_revision", out var sourceRevision) &&
                sourceRevision.TryGetInt64(out var actualRevision) &&
                actualRevision != expectedRevision)
            {
                return $"Source sequence changed during agent execution (expected revision {expectedRevision}, found {actualRevision}).";
            }
        }

        if (task.Plan is { } plan)
        {
            foreach (var probe in AgentVerificationProbePlanner.Create(plan))
            {
                var detail = ProbeDetail(probe.RequiredCapabilities);
                var observation = await ExecuteVerificationToolAsync(
                    "inspect_boundary",
                    AgentToolJson.ToElement(new
                    {
                        target_kind = "sequence",
                        target_id = draftId,
                        at_seconds = probe.DraftBoundarySeconds,
                        window_seconds = probe.WindowSeconds,
                        detail,
                        query = probe.Query
                    }),
                    cancellationToken).ConfigureAwait(false);
                if (observation is null ||
                    !string.Equals(observation.ToolName, "inspect_boundary", StringComparison.OrdinalIgnoreCase) ||
                    observation.Status != AgentToolResultStatus.Succeeded)
                {
                    return $"Multichannel verification failed for approved step {probe.StepOrder}: {observation?.Summary ?? "no result"}";
                }
            }
        }

        if (!_verificationEditLogObserved)
        {
            return "Agent edit log does not exactly match the approved actions.";
        }

        return null;
    }

    private async Task<AgentVerificationReport> BuildVerificationReportAsync(
        AgentTaskState task,
        AgentDeterministicVerificationResult deterministic,
        CancellationToken cancellationToken)
    {
        var observations = GetObservationContext()
            .Where(observation =>
                string.Equals(observation.ToolName, "inspect_agent_edits", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(observation.ToolName, "inspect_timeline_integrity", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(observation.ToolName, "compare_sequences", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(observation.ToolName, "inspect_boundary", StringComparison.OrdinalIgnoreCase))
            .ToImmutableArray();
        if (_reporter is not null)
        {
            try
            {
                return await _reporter.ReportVerificationAsync(
                    new AgentVerificationReportRequest(task, deterministic, observations, 1),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return new AgentVerificationReport(
                    true,
                    "Утверждённые действия выполнены; детерминированная проверка Agent Draft пройдена.",
                    ImmutableArray.Create($"Model report unavailable: {exception.Message}"));
            }
        }

        return new AgentVerificationReport(
            true,
            $"Выполнено утверждённых действий: {_successfulEditingActions}. Agent Draft проверен; исходная последовательность не изменена.",
            ImmutableArray<string>.Empty);
    }

    private static string ProbeDetail(AgentEvidenceCapabilities required)
    {
        if ((required & AgentEvidenceCapabilities.Frames) != 0 &&
            (required & (AgentEvidenceCapabilities.Audio | AgentEvidenceCapabilities.Transcript)) == 0)
        {
            return "frames";
        }
        if ((required & AgentEvidenceCapabilities.Transcript) != 0 &&
            (required & (AgentEvidenceCapabilities.Frames | AgentEvidenceCapabilities.Audio)) == 0)
        {
            return "transcript";
        }
        if ((required & AgentEvidenceCapabilities.Audio) != 0 &&
            (required & (AgentEvidenceCapabilities.Frames | AgentEvidenceCapabilities.Transcript)) == 0)
        {
            return "audio";
        }

        return "all";
    }

    private async Task<AgentModelObservation?> ExecuteVerificationToolAsync(
        string toolName,
        JsonElement toolArguments,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            AddSyntheticObservation(
                string.Empty,
                AgentToolResultStatus.Rejected,
                "Deterministic verification requested a tool without a name.",
                "invalid_verification_tool_call");
            return GetObservationContext().LastOrDefault();
        }

        if (toolArguments.ValueKind != JsonValueKind.Object)
        {
            AddSyntheticObservation(
                toolName,
                AgentToolResultStatus.Rejected,
                "Deterministic verification tool arguments must be a JSON object.",
                "invalid_verification_tool_call");
            return GetObservationContext().LastOrDefault();
        }

        var task = RequireCurrentTask();
        if (!_registry.TryGet(toolName, out var requestedTool) ||
            requestedTool is null ||
            requestedTool.Descriptor.Access != AgentToolAccess.ReadOnly)
        {
            AddSyntheticObservation(
                toolName,
                AgentToolResultStatus.Rejected,
                "Deterministic verification can execute read-only tools only.",
                "verification_read_only_required");
            return GetObservationContext().LastOrDefault();
        }

        var call = AgentToolCall.Create(
            task.Id,
            toolName,
            toolArguments);

        var result = await _toolExecutor.ExecuteAsync(
            task,
            call,
            cancellationToken);

        var resultObservation = AgentModelObservation.FromResult(
            _nextObservationSequence++,
            result);
        AddObservation(resultObservation);

        if (task.Phase == AgentTaskPhase.Verifying &&
            result.IsSuccess &&
            _registry.TryGet(result.ToolName, out var tool) &&
            tool is not null)
        {
            if (tool.Descriptor.Access == AgentToolAccess.ReadOnly)
            {
                if (string.Equals(
                        result.ToolName,
                        "inspect_agent_edits",
                        StringComparison.OrdinalIgnoreCase))
                {
                    _verificationEditLogObserved = EditLogMatchesSuccessfulActions(result);
                    if (!_verificationEditLogObserved)
                    {
                        AddSyntheticObservation(
                            result.ToolName,
                            AgentToolResultStatus.Rejected,
                            "The Agent Draft edit log does not match the editing actions that succeeded in this run.",
                            "verification_edit_log_mismatch");
                    }
                }
            }
        }

        return resultObservation;
    }

    private ImmutableArray<AgentModelObservation> GetObservationContext()
    {
        lock (_observations)
        {
            return _observations.ToImmutableArray();
        }
    }

    private void EnsureMemoryFor(Guid taskId)
    {
        if (_memoryTaskId == taskId)
        {
            return;
        }

        lock (_observations)
        {
            _observations.Clear();
            _nextObservationSequence = 1;

            foreach (var observation in _seedObservationProvider())
            {
                _observations.Add(observation with
                {
                    Sequence = _nextObservationSequence++
                });
            }

            TrimObservationContextLocked();
        }

        _memoryTaskId = taskId;
        _successfulEditingActions = 0;
        _verificationEditLogObserved = false;
        _successfulEditingToolNames.Clear();
        _executedPlanStepIds.Clear();
    }

    private bool EditLogMatchesSuccessfulActions(AgentToolResult result)
    {
        if (result.Data is not { } data ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("edit_count", out var countElement) ||
            !countElement.TryGetInt32(out var editCount) ||
            !data.TryGetProperty("edits", out var editsElement) ||
            editsElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var loggedTools = editsElement.EnumerateArray()
            .Select(edit => edit.TryGetProperty("toolName", out var toolName)
                ? toolName.GetString()
                : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToArray();
        return editCount == _successfulEditingToolNames.Count &&
               loggedTools.SequenceEqual(
                   _successfulEditingToolNames,
                   StringComparer.OrdinalIgnoreCase);
    }

    private void AddSyntheticObservation(
        string toolName,
        AgentToolResultStatus status,
        string summary,
        string errorCode)
    {
        AddObservation(new AgentModelObservation(
            _nextObservationSequence++,
            toolName,
            status,
            summary,
            null,
            errorCode));
    }

    private void AddObservation(AgentModelObservation observation)
    {
        lock (_observations)
        {
            _observations.Add(observation);
            TrimObservationContextLocked();
        }
    }

    private void TrimObservationContextLocked()
    {
        AgentObservationRetention.Trim(
            _observations,
            RequireCurrentTask(),
            _options.MaxObservationCount,
            _options.MaxObservationContextCharacters);
    }

    private AgentTaskState RequireCurrentTask()
        => _orchestrator.CurrentTask
           ?? throw new AgentTaskTransitionException(
               "There is no active AI agent task.");

    private AgentTaskState FailTask(string message)
    {
        var task = RequireCurrentTask();
        if (task.IsTerminal)
        {
            return task;
        }

        Log(
            task,
            "task_failed",
            message: message);

        _checkpointStore?.SetStatus(task, AgentDraftExecutionStatus.Interrupted);
        return _orchestrator.Fail(LimitText(message, 4_000));
    }

    private void Log(
        AgentTaskState task,
        string eventName,
        int? turn = null,
        string? message = null,
        string? details = null)
    {
        _debugLog.Write(new AgentDebugLogEntry(
            DateTimeOffset.UtcNow,
            "execution_loop",
            eventName,
            task.Id,
            task.Phase.ToString(),
            turn,
            message,
            details));
    }

    private static string LimitText(string value, int maximumCharacters)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= maximumCharacters
            ? trimmed
            : trimmed[..maximumCharacters].TrimEnd() + "…";
    }
}
