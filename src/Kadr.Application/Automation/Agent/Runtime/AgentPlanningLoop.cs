using System.Collections.Immutable;
using System.Text.Json;
using KadrStudio.Application.Automation.Agent.Diagnostics;
using KadrStudio.Application.Automation.Agent.Tools;

namespace KadrStudio.Application.Automation.Agent.Runtime;

/// <summary>
/// Read-only Model -> Tool -> Observation loop used while the agent researches a
/// task and prepares a user-approvable plan.
///
/// The loop never edits a timeline. Editing tools remain blocked by
/// <see cref="AgentToolExecutor"/> and a plan always pauses at
/// <see cref="AgentTaskPhase.WaitingForApproval"/>.
/// </summary>
public sealed class AgentPlanningLoop
{
    private readonly AiAgentOrchestrator _orchestrator;
    private readonly AgentToolRegistry _registry;
    private readonly AgentToolExecutor _toolExecutor;
    private readonly IAgentModel _model;
    private readonly AgentPlanningLoopOptions _options;
    private readonly Func<ImmutableArray<AgentConversationContextMessage>> _conversationProvider;
    private readonly IAgentDebugLog _debugLog;
    private readonly SemaphoreSlim _runGate = new(1, 1);

    private readonly List<AgentModelObservation> _observations = [];
    private Guid? _memoryTaskId;
    private long? _memorySourceSequenceRevision;
    private int _nextObservationSequence = 1;
    private string? _lastToolSignature;
    private int _consecutiveIdenticalToolCalls;

    public AgentPlanningLoop(
        AiAgentOrchestrator orchestrator,
        AgentToolRegistry registry,
        AgentToolExecutor toolExecutor,
        IAgentModel model,
        AgentPlanningLoopOptions? options = null,
        Func<ImmutableArray<AgentConversationContextMessage>>? conversationProvider = null,
        IAgentDebugLog? debugLog = null)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _toolExecutor = toolExecutor ?? throw new ArgumentNullException(nameof(toolExecutor));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _options = options ?? AgentPlanningLoopOptions.Default;
        _conversationProvider =
            conversationProvider ?? (() => ImmutableArray<AgentConversationContextMessage>.Empty);
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
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var task = RequireCurrentTask();
            EnsureMemoryFor(task);

            Log(
                task,
                "run_started",
                message: "Planning loop started or resumed.");

            if (task.IsTerminal ||
                task.Phase is AgentTaskPhase.WaitingForUserInput or
                    AgentTaskPhase.WaitingForApproval or
                    AgentTaskPhase.Approved)
            {
                return task;
            }

            if (task.Phase is AgentTaskPhase.Executing or AgentTaskPhase.Verifying)
            {
                throw new AgentTaskTransitionException(
                    "The planning loop cannot run after draft execution has started.");
            }

            if (task.Phase == AgentTaskPhase.Understanding)
            {
                task = _orchestrator.BeginInvestigation(
                    "Agent started task-driven investigation.");
            }

            for (var turn = 1; turn <= _options.MaxModelTurns; turn++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                task = RequireCurrentTask();

                if (task.Phase is not (
                        AgentTaskPhase.Investigating or
                        AgentTaskPhase.Planning))
                {
                    return task;
                }

                AgentModelDecision decision;
                try
                {
                    var tools = GetPlanningToolDescriptors();
                    var observations = GetObservationContext();
                    var conversation = GetConversationContext();

                    Log(
                        task,
                        "model_turn_requested",
                        turn,
                        $"Preparing planning model turn {turn}.",
                        $"tools={tools.Length}; observations={observations.Length}; conversation_messages={conversation.Length}");

                    var request = new AgentModelTurnRequest(
                        task,
                        tools,
                        observations,
                        conversation,
                        turn,
                        AgentModelTurnMode.Planning);

                    decision = await _model.DecideAsync(
                        request,
                        cancellationToken).ConfigureAwait(false);

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
                        "Planning model turn failed before a valid decision was produced.");

                    return FailTask(
                        $"Agent model failed while preparing the plan: {exception.Message}");
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
                            cancellationToken).ConfigureAwait(false);
                        break;

                    case AgentModelActionKind.AskUser:
                        return HandleQuestionDecision(decision);

                    case AgentModelActionKind.PublishPlan:
                        var planState = HandlePlanDecision(decision);
                        if (planState is not null)
                        {
                            return planState;
                        }
                        break;

                    default:
                        return FailTask(
                            $"Agent model returned unsupported action '{decision.Action}'.");
                }
            }

            return FailTask(
                $"Agent planning exceeded the {_options.MaxModelTurns} model-turn safety limit.");
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

        if (string.Equals(
                signature,
                _lastToolSignature,
                StringComparison.Ordinal))
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
                "This exact tool call was already repeated. Reuse the existing evidence, narrow the request, or choose another tool.",
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
            cancellationToken).ConfigureAwait(false);

        AddObservation(AgentModelObservation.FromResult(
            _nextObservationSequence++,
            result));
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

    private AgentTaskState? HandlePlanDecision(
        AgentModelDecision decision)
    {
        if (decision.Plan is null)
        {
            return FailTask(
                "Agent model selected publish_plan without a plan.");
        }

        var task = RequireCurrentTask();
        if (!ValidateMachineCheckablePlan(task, decision.Plan, out var validationError))
        {
            AddSyntheticObservation(
                "publish_plan",
                AgentToolResultStatus.Rejected,
                validationError,
                "plan_evidence_required");
            return null;
        }

        if (task.Phase == AgentTaskPhase.Investigating)
        {
            task = _orchestrator.BeginPlanning(
                "Agent finished investigation and is preparing the proposed edit plan.");
        }

        return task.Plan is null
            ? _orchestrator.PublishPlan(decision.Plan)
            : _orchestrator.RevisePlan(
                decision.Plan,
                AgentPlanRevisionSource.Agent,
                "Agent updated the plan from the latest user instructions and evidence.");
    }

    private bool ValidateMachineCheckablePlan(
        AgentTaskState task,
        AgentPlanDraft plan,
        out string error)
    {
        var editingSteps = plan.Steps
            .Where(step => !string.IsNullOrWhiteSpace(step.ExpectedEditingTool))
            .ToArray();
        if (editingSteps.Length == 0)
        {
            // Plans created by older persisted tasks remain readable. New model plans
            // include these fields because the response schema requires them.
            error = string.Empty;
            return true;
        }

        var observations = GetObservationContext();
        foreach (var step in editingSteps)
        {
            if (!_registry.TryGet(step.ExpectedEditingTool!, out var tool) ||
                tool is null ||
                tool.Descriptor.Access != AgentToolAccess.Editing)
            {
                error = $"Plan step '{step.Title}' names an unavailable editing action '{step.ExpectedEditingTool}'.";
                return false;
            }

            if (step.EvidenceObservationSequences.IsDefaultOrEmpty)
            {
                error = $"Plan step '{step.Title}' has no evidence observation references.";
                return false;
            }

            var referenced = observations
                .Where(item => step.EvidenceObservationSequences.Contains(item.Sequence))
                .ToArray();
            if (referenced.Length != step.EvidenceObservationSequences.Distinct().Count() ||
                referenced.Any(item => item.Status != AgentToolResultStatus.Succeeded))
            {
                error = $"Plan step '{step.Title}' references missing or unsuccessful observations.";
                return false;
            }

            if (RequiresVisualBoundaryEvidence(task.UserRequest) &&
                !referenced.Any(IsVisualRangeEvidence))
            {
                error = $"Plan step '{step.Title}' needs a successful inspect_range observation with frames/all evidence; inspect_timeline or summary is not enough to identify opening/ending boundaries.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool RequiresVisualBoundaryEvidence(string request)
        => new[] { "опенинг", "эндинг", "opening", "ending", "intro", "outro" }
            .Any(value => request.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static bool IsVisualRangeEvidence(AgentModelObservation observation)
    {
        if (!string.Equals(observation.ToolName, "inspect_range", StringComparison.OrdinalIgnoreCase) ||
            observation.Data is not { } data ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("detail", out var detailElement))
        {
            return false;
        }

        var detail = detailElement.GetString();
        if (!string.Equals(detail, "frames", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(detail, "all", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (data.TryGetProperty("analysis_deferred", out var deferredElement) &&
            deferredElement.ValueKind == JsonValueKind.True)
        {
            return false;
        }

        return HasVisualObservations(data);
    }

    private static bool HasVisualObservations(JsonElement data)
    {
        if (data.TryGetProperty("vision", out var vision) &&
            vision.ValueKind == JsonValueKind.Object &&
            vision.TryGetProperty("available", out var available) &&
            available.ValueKind == JsonValueKind.True &&
            vision.TryGetProperty("observations", out var observations) &&
            observations.ValueKind == JsonValueKind.Array &&
            observations.GetArrayLength() > 0)
        {
            return true;
        }

        return data.TryGetProperty("analyses", out var analyses) &&
               analyses.ValueKind == JsonValueKind.Array &&
               analyses.EnumerateArray().Any(item =>
                   item.ValueKind == JsonValueKind.Object &&
                   item.TryGetProperty("status", out var status) &&
                   string.Equals(status.GetString(), "succeeded", StringComparison.OrdinalIgnoreCase) &&
                   item.TryGetProperty("observation", out var nested) &&
                   nested.ValueKind == JsonValueKind.Object &&
                   HasVisualObservations(nested));
    }

    private ImmutableArray<AgentToolDescriptor> GetPlanningToolDescriptors()
        => _registry.Descriptors
            .Where(descriptor => descriptor.Access == AgentToolAccess.ReadOnly)
            .ToImmutableArray();

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

            if (observation.Data is { } data)
            {
                total += data.GetRawText().Length;
            }
        }

        return total;
    }

    private void EnsureMemoryFor(AgentTaskState task)
    {
        if (_memoryTaskId == task.Id &&
            _memorySourceSequenceRevision == task.SourceSequenceRevision)
        {
            return;
        }

        // Evidence collected for another source revision can no longer be treated
        // as current. Keep it only when the user revises the plan without changing
        // the underlying source sequence.
        lock (_observations)
        {
            _observations.Clear();
        }

        _memoryTaskId = task.Id;
        _memorySourceSequenceRevision = task.SourceSequenceRevision;
        _nextObservationSequence = 1;
        _lastToolSignature = null;
        _consecutiveIdenticalToolCalls = 0;
    }

    private AgentTaskState RequireCurrentTask()
        => _orchestrator.CurrentTask
           ?? throw new AgentTaskTransitionException(
               "There is no active AI agent task.");

    private AgentTaskState FailTask(string message)
    {
        var current = RequireCurrentTask();
        if (current.IsTerminal)
        {
            return current;
        }

        Log(
            current,
            "task_failed",
            message: message);

        return _orchestrator.Fail(message);
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
            "planning_loop",
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
            "planning_loop",
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

        var plan = decision.Plan is null
            ? string.Empty
            : $"plan_objective={decision.Plan.Objective}; plan_steps={decision.Plan.Steps.Length}; ";

        return
            $"action={decision.Action}; " +
            $"progress={LimitTextForLog(decision.Progress, 2_000)}; " +
            $"tool_name={decision.ToolName}; " +
            $"tool_arguments={LimitTextForLog(toolArguments, 12_000)}; " +
            $"question={LimitTextForLog(decision.Question, 4_000)}; " +
            plan +
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

    private static string LimitText(
        string value,
        int maximumCharacters)
    {
        var normalized = value.Trim();
        if (normalized.Length <= maximumCharacters)
        {
            return normalized;
        }

        return normalized[..maximumCharacters].TrimEnd() + "…";
    }
}
