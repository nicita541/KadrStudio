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
    private bool _verificationEditLogObserved;

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

                        _successfulVerificationReads = 0;
                        _verificationEditLogObserved = false;
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
                            AddSyntheticObservation(
                                "inspect_agent_edits",
                                AgentToolResultStatus.Rejected,
                                "Completion requires a successful inspect_agent_edits observation after the final edit.",
                                "verification_edit_log_required");
                            break;
                        }

                        if (_successfulVerificationReads <= 0)
                        {
                            AddSyntheticObservation(
                                string.Empty,
                                AgentToolResultStatus.Rejected,
                                "Completion requires at least one successful read-only inspection of the final draft in addition to the edit log.",
                                "verification_observation_required");
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
                    _verificationEditLogObserved = true;
                }
                else if (IsFinalDraftInspection(task, result))
                {
                    _successfulVerificationReads++;
                }
            }
            else
            {
                // Any corrective edit invalidates all previous verification evidence.
                // The final draft must be inspected again after the correction.
                _successfulVerificationReads = 0;
                _verificationEditLogObserved = false;
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
        => _registry.Descriptors;

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
        _verificationEditLogObserved = false;
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
        while (_observations.Count > _options.MaxObservationCount)
        {
            _observations.RemoveAt(0);
        }

        while (_observations.Count > 1 &&
               EstimateObservationCharacters(_observations) >
               _options.MaxObservationContextCharacters)
        {
            _observations.RemoveAt(0);
        }
    }

    private static int EstimateObservationCharacters(
        IEnumerable<AgentModelObservation> observations)
    {
        var total = 0;
        foreach (var observation in observations)
        {
            total += observation.ToolName.Length;
            total += observation.Summary.Length;
            total += observation.ErrorCode?.Length ?? 0;
            total += observation.Data?.GetRawText().Length ?? 0;
        }

        return total;
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
