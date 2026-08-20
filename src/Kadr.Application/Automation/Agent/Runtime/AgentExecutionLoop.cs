using System.Collections.Immutable;
using System.Text.Json;
using KadrStudio.Application.Automation.Agent.Diagnostics;
using KadrStudio.Application.Automation.Agent.Tools;

namespace KadrStudio.Application.Automation.Agent.Runtime;

/// <summary>
/// Executes an already approved plan on a separate Agent Draft and then verifies
/// that draft. The model can use only registered safe tools; the tool executor
/// enforces draft ownership for every editing call.
/// </summary>
public sealed class AgentExecutionLoop
{
    private readonly AiAgentOrchestrator _orchestrator;
    private readonly AgentToolRegistry _registry;
    private readonly AgentToolExecutor _toolExecutor;
    private readonly IAgentModel _model;
    private readonly AgentExecutionLoopOptions _options;
    private readonly Func<ImmutableArray<AgentConversationContextMessage>> _conversationProvider;
    private readonly Func<ImmutableArray<AgentModelObservation>> _seedObservationProvider;
    private readonly IAgentDebugLog _debugLog;
    private readonly SemaphoreSlim _runGate = new(1, 1);

    private readonly List<AgentModelObservation> _observations = [];
    private Guid? _memoryTaskId;
    private int _nextObservationSequence = 1;
    private string? _lastToolSignature;
    private int _consecutiveIdenticalToolCalls;
    private int _successfulVerificationReads;
    private int _successfulEditingActions;
    private int _prematureTerminalDecisions;
    private bool _verificationEditLogObserved;
    private bool _verificationIntegrityObserved;
    private readonly List<string> _successfulEditingToolNames = [];
    private readonly HashSet<Guid> _executedPlanStepIds = [];

    public AgentExecutionLoop(
        AiAgentOrchestrator orchestrator,
        AgentToolRegistry registry,
        AgentToolExecutor toolExecutor,
        IAgentModel model,
        AgentExecutionLoopOptions? options = null,
        Func<ImmutableArray<AgentConversationContextMessage>>? conversationProvider = null,
        Func<ImmutableArray<AgentModelObservation>>? seedObservationProvider = null,
        IAgentDebugLog? debugLog = null)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _toolExecutor = toolExecutor ?? throw new ArgumentNullException(nameof(toolExecutor));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _options = options ?? AgentExecutionLoopOptions.Default;
        _conversationProvider =
            conversationProvider ?? (() => ImmutableArray<AgentConversationContextMessage>.Empty);
        _seedObservationProvider =
            seedObservationProvider ?? (() => ImmutableArray<AgentModelObservation>.Empty);
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

            if (task.Phase == AgentTaskPhase.Executing &&
                HasDeterministicEditingPlan(task))
            {
                var executionError = await ExecuteApprovedPlanAsync(
                    task,
                    cancellationToken).ConfigureAwait(false);
                if (executionError is not null)
                {
                    return FailTask(executionError);
                }

                _successfulVerificationReads = 0;
                _verificationEditLogObserved = false;
                _verificationIntegrityObserved = false;
                _prematureTerminalDecisions = 0;
                task = _orchestrator.BeginVerification(
                    "Утверждённые действия выполнены один раз; запускаю обязательную проверку.");

                var verificationError = await RunAutomaticVerificationAsync(
                    task,
                    cancellationToken).ConfigureAwait(false);
                if (verificationError is not null)
                {
                    return FailTask(verificationError);
                }

                var report = await BuildVerificationReportAsync(
                    task,
                    cancellationToken).ConfigureAwait(false);
                if (!report.Accepted)
                {
                    var issues = report.Issues.IsDefaultOrEmpty
                        ? report.Summary
                        : string.Join(" ", report.Issues);
                    return FailTask($"Проверка Agent Draft не принята: {issues}");
                }

                return _orchestrator.Complete(LimitText(report.Summary, 4_000));
            }

            for (var turn = 1; turn <= _options.MaxModelTurns; turn++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                task = RequireCurrentTask();

                if (task.IsTerminal ||
                    task.Phase == AgentTaskPhase.WaitingForUserInput)
                {
                    return task;
                }

                if (task.Phase is not (
                        AgentTaskPhase.Executing or
                        AgentTaskPhase.Verifying))
                {
                    return task;
                }

                AgentModelDecision decision;
                try
                {
                    var tools = GetExecutionToolDescriptors();
                    var observations = GetObservationContext();
                    var conversation = GetConversationContext();
                    var mode = task.Phase == AgentTaskPhase.Verifying
                        ? AgentModelTurnMode.Verification
                        : AgentModelTurnMode.Execution;

                    Log(
                        task,
                        "model_turn_requested",
                        turn,
                        $"Preparing {mode} model turn {turn}.",
                        $"tools={tools.Length}; observations={observations.Length}; conversation_messages={conversation.Length}");

                    var request = new AgentModelTurnRequest(
                        task,
                        tools,
                        observations,
                        conversation,
                        turn,
                        mode);

                    decision = await _model.DecideAsync(
                        request,
                        cancellationToken);

                    Log(
                        task,
                        "model_decision",
                        turn,
                        $"Model selected action '{decision.Action}'.",
                        DescribeDecision(decision));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    LogException(
                        task,
                        "model_turn_failed",
                        turn,
                        exception,
                        "Execution/verification model turn failed before a valid decision was produced.");

                    return FailTask(
                        $"Agent model failed while executing the approved plan: {exception.Message}");
                }

                if (!string.IsNullOrWhiteSpace(decision.Progress))
                {
                    _orchestrator.RecordProgress(
                        LimitText(decision.Progress, _options.MaxProgressCharacters));
                }

                switch (decision.Action)
                {
                    case AgentModelActionKind.UseTool:
                        await HandleToolDecisionAsync(
                            decision,
                            cancellationToken);
                        break;

                    case AgentModelActionKind.AskUser:
                        return HandleQuestionDecision(decision);

                    case AgentModelActionKind.BeginVerification:
                        if (task.Phase != AgentTaskPhase.Executing)
                        {
                            AddSyntheticObservation(
                                string.Empty,
                                AgentToolResultStatus.Rejected,
                                "Verification has already started.",
                                "verification_already_started");
                            break;
                        }

                        if (_successfulEditingActions <= 0)
                        {
                            if (RejectPrematureTerminalDecision(
                                    "Verification cannot start before at least one approved editing action succeeds.",
                                    "successful_edit_required"))
                            {
                                return FailTask(
                                    "Agent repeatedly tried to verify a draft without making any approved edit. The task was stopped instead of claiming changes that did not happen.");
                            }
                            break;
                        }

                        var incompleteSteps = GetIncompleteEditingSteps(task);
                        if (incompleteSteps.Length > 0)
                        {
                            AddSyntheticObservation(
                                string.Empty,
                                AgentToolResultStatus.Rejected,
                                $"Verification cannot start because {incompleteSteps.Length} approved editing action(s) have not succeeded yet.",
                                "approved_edits_incomplete");
                            break;
                        }

                        _successfulVerificationReads = 0;
                        _verificationEditLogObserved = false;
                        _verificationIntegrityObserved = false;
                        _prematureTerminalDecisions = 0;
                        _orchestrator.BeginVerification(
                            string.IsNullOrWhiteSpace(decision.Progress)
                                ? "Agent started checking the finished draft."
                                : LimitText(decision.Progress, _options.MaxProgressCharacters));
                        break;

                    case AgentModelActionKind.CompleteTask:
                        if (task.Phase != AgentTaskPhase.Verifying)
                        {
                            AddSyntheticObservation(
                                string.Empty,
                                AgentToolResultStatus.Rejected,
                                "The agent must enter verification before completing the task.",
                                "verification_required");
                            break;
                        }

                        if (!_verificationEditLogObserved)
                        {
                            if (RejectPrematureTerminalDecision(
                                    "Completion requires a successful inspect_agent_edits observation matching the successful editing actions.",
                                    "verification_edit_log_required",
                                    "inspect_agent_edits"))
                            {
                                return FailTask(
                                    "Agent repeatedly tried to complete the task without a matching edit log. No unverified completion was accepted.");
                            }
                            break;
                        }

                        if (_successfulVerificationReads <= 0)
                        {
                            if (RejectPrematureTerminalDecision(
                                    "Completion requires at least one successful read-only inspection of the final draft in addition to the edit log.",
                                    "verification_observation_required"))
                            {
                                return FailTask(
                                    "Agent repeatedly tried to complete the task before inspecting the final Agent Draft.");
                            }
                            break;
                        }

                        if (HasTool("inspect_timeline_integrity") &&
                            !_verificationIntegrityObserved)
                        {
                            if (RejectPrematureTerminalDecision(
                                    "Completion requires inspect_timeline_integrity for the final Agent Draft.",
                                    "verification_integrity_required",
                                    "inspect_timeline_integrity"))
                            {
                                return FailTask(
                                    "Agent repeatedly tried to complete the task without checking gaps, overlaps and linked-clip synchronization.");
                            }
                            break;
                        }

                        if (string.IsNullOrWhiteSpace(decision.CompletionSummary))
                        {
                            return FailTask(
                                "Agent model completed the task without a completion summary.");
                        }

                        return _orchestrator.Complete(
                            LimitText(decision.CompletionSummary, 4_000));

                    case AgentModelActionKind.PublishPlan:
                        return FailTask(
                            "The agent tried to replace the approved plan during execution.");

                    default:
                        return FailTask(
                            $"Agent model returned unsupported execution action '{decision.Action}'.");
                }
            }

            return FailTask(
                $"Agent execution exceeded the {_options.MaxModelTurns} model-turn safety limit.");
        }
        finally
        {
            _runGate.Release();
        }
    }

    private static bool HasDeterministicEditingPlan(AgentTaskState task)
    {
        if (task.Brief is null)
        {
            // Legacy persisted plans and compatibility tests keep the old
            // model-driven runner. Every newly interpreted task has a brief.
            return false;
        }

        var editingSteps = task.Plan?.Steps
            .Where(step => !string.IsNullOrWhiteSpace(step.ExpectedEditingTool))
            .ToArray() ?? [];
        return editingSteps.Length > 0 &&
               editingSteps.All(step =>
                   step.ExpectedEditingArguments is { ValueKind: JsonValueKind.Object });
    }

    private async Task<string?> ExecuteApprovedPlanAsync(
        AgentTaskState task,
        CancellationToken cancellationToken)
    {
        foreach (var step in task.Plan!.Steps
                     .Where(step => !string.IsNullOrWhiteSpace(step.ExpectedEditingTool)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_executedPlanStepIds.Contains(step.Id))
            {
                continue;
            }

            if (!_registry.TryGet(step.ExpectedEditingTool!, out var tool) ||
                tool is null ||
                tool.Descriptor.Access != AgentToolAccess.Editing)
            {
                return $"Approved editing tool '{step.ExpectedEditingTool}' is no longer available.";
            }

            var result = await _toolExecutor.ExecuteAsync(
                RequireCurrentTask(),
                AgentToolCall.Create(
                    task.Id,
                    step.ExpectedEditingTool!,
                    step.ExpectedEditingArguments!.Value),
                cancellationToken).ConfigureAwait(false);
            AddObservation(AgentModelObservation.FromResult(
                _nextObservationSequence++,
                result));
            if (!result.IsSuccess)
            {
                return $"Approved action '{step.Title}' failed: {result.Summary}";
            }

            _executedPlanStepIds.Add(step.Id);
            _successfulEditingActions++;
            _successfulEditingToolNames.Add(result.ToolName);
            _orchestrator.RecordProgress($"Выполнено: {step.Title}");
        }

        return null;
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

            await HandleToolDecisionAsync(
                AgentModelDecision.UseTool(check.ToolName, check.Arguments),
                cancellationToken).ConfigureAwait(false);
            var observation = GetObservationContext().LastOrDefault();
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

        if (!_verificationEditLogObserved)
        {
            return "Agent edit log does not exactly match the approved actions.";
        }

        return null;
    }

    private async Task<AgentVerificationReport> BuildVerificationReportAsync(
        AgentTaskState task,
        CancellationToken cancellationToken)
    {
        var observations = GetObservationContext()
            .Where(observation =>
                string.Equals(observation.ToolName, "inspect_agent_edits", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(observation.ToolName, "inspect_timeline_integrity", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(observation.ToolName, "compare_sequences", StringComparison.OrdinalIgnoreCase))
            .ToImmutableArray();
        if (_model is IAgentVerificationReporter reporter)
        {
            try
            {
                return await reporter.ReportVerificationAsync(
                    new AgentVerificationReportRequest(task, observations, 1),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return new AgentVerificationReport(
                    false,
                    "Не удалось сформировать структурированный итоговый отчёт.",
                    ImmutableArray.Create(exception.Message));
            }
        }

        return new AgentVerificationReport(
            true,
            $"Выполнено утверждённых действий: {_successfulEditingActions}. Agent Draft проверен; исходная последовательность не изменена.",
            ImmutableArray<string>.Empty);
    }

    private async Task HandleToolDecisionAsync(
        AgentModelDecision decision,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(decision.ToolName))
        {
            AddSyntheticObservation(
                string.Empty,
                AgentToolResultStatus.Rejected,
                "Agent model requested a tool without a tool name.",
                "invalid_model_tool_call");
            return;
        }

        if (decision.ToolArguments.ValueKind != JsonValueKind.Object)
        {
            AddSyntheticObservation(
                decision.ToolName,
                AgentToolResultStatus.Rejected,
                "Agent model tool arguments must be a JSON object.",
                "invalid_model_tool_call");
            return;
        }

        var signature =
            decision.ToolName.Trim().ToLowerInvariant() + "\n" +
            decision.ToolArguments.GetRawText();

        if (string.Equals(signature, _lastToolSignature, StringComparison.Ordinal))
        {
            _consecutiveIdenticalToolCalls++;
        }
        else
        {
            _lastToolSignature = signature;
            _consecutiveIdenticalToolCalls = 1;
        }

        if (_consecutiveIdenticalToolCalls >
            _options.MaxConsecutiveIdenticalToolCalls)
        {
            AddSyntheticObservation(
                decision.ToolName,
                AgentToolResultStatus.Rejected,
                "This exact tool call was already repeated. Reuse the existing result or change the request.",
                "repeated_tool_call");
            return;
        }

        var task = RequireCurrentTask();
        AgentPlanStep? approvedStep = null;
        if (_registry.TryGet(decision.ToolName, out var requestedTool) &&
            requestedTool is not null &&
            requestedTool.Descriptor.Access == AgentToolAccess.Editing)
        {
            approvedStep = FindApprovedStep(task, decision);
            if (approvedStep is null)
            {
                AddSyntheticObservation(
                    decision.ToolName,
                    AgentToolResultStatus.Rejected,
                    $"Editing action '{decision.ToolName}' with these arguments is not an unexecuted action in the approved plan. Revise the plan before changing the tool or its ranges, clip IDs, or parameters.",
                    "editing_arguments_not_approved");
                return;
            }
        }

        var call = AgentToolCall.Create(
            task.Id,
            decision.ToolName,
            decision.ToolArguments);

        var result = await _toolExecutor.ExecuteAsync(
            task,
            call,
            cancellationToken);

        AddObservation(AgentModelObservation.FromResult(
            _nextObservationSequence++,
            result));

        if (result.IsSuccess &&
            _registry.TryGet(result.ToolName, out var executedTool) &&
            executedTool is not null &&
            executedTool.Descriptor.Access == AgentToolAccess.Editing)
        {
            if (approvedStep is not null)
            {
                _executedPlanStepIds.Add(approvedStep.Id);
            }
            _successfulEditingActions++;
            _successfulEditingToolNames.Add(result.ToolName);
            _prematureTerminalDecisions = 0;
        }

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
                else if (IsFinalDraftInspection(task, result))
                {
                    _successfulVerificationReads++;
                    if (string.Equals(
                            result.ToolName,
                            "inspect_timeline_integrity",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        _verificationIntegrityObserved = true;
                    }
                }
            }
            else
            {
                // Any corrective edit invalidates all previous verification evidence.
                // The final draft must be inspected again after the correction.
                _successfulVerificationReads = 0;
                _verificationEditLogObserved = false;
                _verificationIntegrityObserved = false;
            }
        }
    }

    private static bool IsFinalDraftInspection(
        AgentTaskState task,
        AgentToolResult result)
    {
        if (task.DraftSequenceId is not { } draftSequenceId ||
            result.Data is not { } data ||
            data.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!string.Equals(
                result.ToolName,
                "inspect_timeline",
                StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(
                result.ToolName,
                "inspect_range",
                StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(
                result.ToolName,
                "inspect_timeline_integrity",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return data.TryGetProperty("sequence_id", out var sequenceIdElement) &&
               sequenceIdElement.ValueKind == JsonValueKind.String &&
               sequenceIdElement.TryGetGuid(out var observedSequenceId) &&
               observedSequenceId == draftSequenceId;
    }

    private AgentTaskState HandleQuestionDecision(
        AgentModelDecision decision)
    {
        if (string.IsNullOrWhiteSpace(decision.Question))
        {
            return FailTask(
                "Agent model requested user input but returned an empty question.");
        }

        return _orchestrator.AskQuestion(
            LimitText(decision.Question, 2_000),
            string.IsNullOrWhiteSpace(decision.QuestionContext)
                ? null
                : LimitText(decision.QuestionContext, 4_000));
    }

    private ImmutableArray<AgentToolDescriptor> GetExecutionToolDescriptors()
        => RequireCurrentTask().Phase == AgentTaskPhase.Verifying
            ? _registry.Descriptors
                .Where(descriptor => descriptor.Access == AgentToolAccess.ReadOnly &&
                                     !string.Equals(
                                         descriptor.Name,
                                         "inspect_agent_edits",
                                         StringComparison.OrdinalIgnoreCase))
                .ToImmutableArray()
            : _registry.Descriptors;

    private ImmutableArray<AgentModelObservation> GetObservationContext()
    {
        lock (_observations)
        {
            return _observations.ToImmutableArray();
        }
    }

    private ImmutableArray<AgentConversationContextMessage> GetConversationContext()
    {
        var source = _conversationProvider();
        if (source.IsDefaultOrEmpty)
        {
            return ImmutableArray<AgentConversationContextMessage>.Empty;
        }

        var selected = new List<AgentConversationContextMessage>();
        var characters = 0;

        for (var index = source.Length - 1;
             index >= 0 && selected.Count < _options.MaxConversationMessages;
             index--)
        {
            var item = source[index];
            if (string.IsNullOrWhiteSpace(item.Text))
            {
                continue;
            }

            var text = item.Text.Trim();
            if (text.Length > _options.MaxConversationCharacters)
            {
                text = text[.._options.MaxConversationCharacters].TrimEnd() + "…";
            }

            if (selected.Count > 0 &&
                characters + text.Length > _options.MaxConversationCharacters)
            {
                break;
            }

            selected.Add(item with { Text = text });
            characters += text.Length;
        }

        selected.Reverse();
        return selected.ToImmutableArray();
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
        _lastToolSignature = null;
        _consecutiveIdenticalToolCalls = 0;
        _successfulVerificationReads = 0;
        _successfulEditingActions = 0;
        _prematureTerminalDecisions = 0;
        _verificationEditLogObserved = false;
        _verificationIntegrityObserved = false;
        _successfulEditingToolNames.Clear();
        _executedPlanStepIds.Clear();
    }

    private AgentPlanStep? FindApprovedStep(
        AgentTaskState task,
        AgentModelDecision decision)
        => task.Plan?.Steps
            .Where(step => !_executedPlanStepIds.Contains(step.Id))
            .Where(step => !string.IsNullOrWhiteSpace(step.ExpectedEditingTool))
            .FirstOrDefault(step => step.ExpectedEditingArguments is { ValueKind: JsonValueKind.Object } expected
                ? AgentActionApproval.Matches(
                    step.ExpectedEditingTool!,
                    expected,
                    decision.ToolName,
                    decision.ToolArguments)
                : string.Equals(
                    step.ExpectedEditingTool,
                    decision.ToolName,
                    StringComparison.OrdinalIgnoreCase));

    private AgentPlanStep[] GetIncompleteEditingSteps(AgentTaskState task)
        => task.Plan?.Steps
            .Where(step => !string.IsNullOrWhiteSpace(step.ExpectedEditingTool))
            .Where(step => !_executedPlanStepIds.Contains(step.Id))
            .ToArray() ?? [];

    private bool HasTool(string name)
        => _registry.TryGet(name, out var tool) && tool is not null;

    private bool RejectPrematureTerminalDecision(
        string summary,
        string errorCode,
        string toolName = "")
    {
        _prematureTerminalDecisions++;
        AddSyntheticObservation(
            toolName,
            AgentToolResultStatus.Rejected,
            summary,
            errorCode);
        return _prematureTerminalDecisions >= _options.MaxPrematureTerminalDecisions;
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
            // Lightweight test backends and old persisted sessions may not expose
            // the structured log yet; they still cannot pass without a successful edit.
            return _successfulEditingActions > 0;
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

    private void LogException(
        AgentTaskState task,
        string eventName,
        int? turn,
        Exception exception,
        string? message = null)
    {
        _debugLog.Write(new AgentDebugLogEntry(
            DateTimeOffset.UtcNow,
            "execution_loop",
            eventName,
            task.Id,
            task.Phase.ToString(),
            turn,
            message ?? exception.Message,
            Exception: exception.ToString()));
    }

    private static string DescribeDecision(AgentModelDecision decision)
    {
        var toolArguments = decision.ToolArguments.ValueKind == JsonValueKind.Object
            ? decision.ToolArguments.GetRawText()
            : "{}";

        return
            $"action={decision.Action}; " +
            $"progress={LimitTextForLog(decision.Progress, 2_000)}; " +
            $"tool_name={decision.ToolName}; " +
            $"tool_arguments={LimitTextForLog(toolArguments, 12_000)}; " +
            $"question={LimitTextForLog(decision.Question, 4_000)}; " +
            $"completion={LimitTextForLog(decision.CompletionSummary, 4_000)}";
    }

    private static string LimitTextForLog(string? value, int maximumCharacters)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim();
        return normalized.Length <= maximumCharacters
            ? normalized
            : normalized[..maximumCharacters] + "…";
    }

    private static string LimitText(string value, int maximumCharacters)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= maximumCharacters
            ? trimmed
            : trimmed[..maximumCharacters].TrimEnd() + "…";
    }
}
