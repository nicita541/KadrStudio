using System.Collections.Immutable;
using System.Text.Json;
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
    private readonly SemaphoreSlim _runGate = new(1, 1);

    private readonly List<AgentModelObservation> _observations = [];
    private Guid? _memoryTaskId;
    private int _nextObservationSequence = 1;
    private string? _lastToolSignature;
    private int _consecutiveIdenticalToolCalls;

    public AgentPlanningLoop(
        AiAgentOrchestrator orchestrator,
        AgentToolRegistry registry,
        AgentToolExecutor toolExecutor,
        IAgentModel model,
        AgentPlanningLoopOptions? options = null,
        Func<ImmutableArray<AgentConversationContextMessage>>? conversationProvider = null)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _toolExecutor = toolExecutor ?? throw new ArgumentNullException(nameof(toolExecutor));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _options = options ?? AgentPlanningLoopOptions.Default;
        _conversationProvider =
            conversationProvider ?? (() => ImmutableArray<AgentConversationContextMessage>.Empty);
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
            EnsureMemoryFor(task.Id);

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
                    var request = new AgentModelTurnRequest(
                        task,
                        GetPlanningToolDescriptors(),
                        GetObservationContext(),
                        GetConversationContext(),
                        turn);

                    decision = await _model.DecideAsync(
                        request,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
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
                        return HandlePlanDecision(decision);

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

    private AgentTaskState HandlePlanDecision(
        AgentModelDecision decision)
    {
        if (decision.Plan is null)
        {
            return FailTask(
                "Agent model selected publish_plan without a plan.");
        }

        var task = RequireCurrentTask();
        if (task.Phase == AgentTaskPhase.Investigating)
        {
            _orchestrator.BeginPlanning(
                "Agent finished investigation and is preparing the proposed edit plan.");
        }

        return _orchestrator.PublishPlan(decision.Plan);
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

    private void EnsureMemoryFor(Guid taskId)
    {
        if (_memoryTaskId == taskId)
        {
            return;
        }

        lock (_observations)
        {
            _observations.Clear();
        }

        _memoryTaskId = taskId;
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

        return _orchestrator.Fail(message);
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
