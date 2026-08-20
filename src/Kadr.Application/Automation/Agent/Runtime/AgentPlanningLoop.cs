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
                if (task.Brief is null && _model is IAgentTaskInterpreter interpreter)
                {
                    _orchestrator.RecordProgress("Модель размышляет и формирует JSON понимания задачи…");
                    await SeedUnderstandingObservationsAsync(cancellationToken)
                        .ConfigureAwait(false);

                    var understanding = await interpreter.UnderstandAsync(
                        new AgentModelTurnRequest(
                            task,
                            // The brief only needs the seeded editor/project facts.
                            // Full tool schemas are introduced on the investigation
                            // turn where the model can actually choose among them.
                            ImmutableArray<AgentToolDescriptor>.Empty,
                            GetObservationContext(),
                            GetConversationContext(),
                            0,
                            AgentModelTurnMode.Planning),
                        cancellationToken).ConfigureAwait(false);

                    task = _orchestrator.SetTaskBrief(understanding.Brief);
                    if (!understanding.Questions.IsDefaultOrEmpty)
                    {
                        return _orchestrator.AskQuestions(understanding.Questions);
                    }
                }

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

                    _orchestrator.RecordProgress("Модель размышляет и формирует JSON следующего исследовательского шага…");
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
                        var planState = await HandlePlanDecisionAsync(
                            decision,
                            turn,
                            cancellationToken).ConfigureAwait(false);
                        if (planState is not null)
                        {
                            return planState;
                        }
                        break;

                    case AgentModelActionKind.CompleteReadOnly:
                        if (task.Brief?.Kind != AgentTaskKind.ReadOnly ||
                            string.IsNullOrWhiteSpace(decision.CompletionSummary))
                        {
                            return FailTask(
                                "Only a proven read-only task can complete without a plan.");
                        }
                        return _orchestrator.CompleteReadOnly(
                            LimitText(decision.CompletionSummary, 4_000));

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

        if (!_registry.TryGet(decision.ToolName, out var requestedTool) ||
            requestedTool is null)
        {
            AddSyntheticObservation(
                decision.ToolName,
                AgentToolResultStatus.Rejected,
                "The requested tool is not available.",
                "tool_not_found");
            return;
        }

        if (requestedTool.Descriptor.Access != AgentToolAccess.ReadOnly)
        {
            AddSyntheticObservation(
                decision.ToolName,
                AgentToolResultStatus.Rejected,
                "Editing tools are visible only for plan construction and cannot run during investigation.",
                "editing_tool_requires_approved_plan");
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

    private async Task SeedUnderstandingObservationsAsync(
        CancellationToken cancellationToken)
    {
        foreach (var toolName in new[] { "inspect_editor_context", "inspect_project" })
        {
            if (!_registry.TryGet(toolName, out var tool) ||
                tool is null ||
                tool.Descriptor.Access != AgentToolAccess.ReadOnly)
            {
                continue;
            }

            var task = RequireCurrentTask();
            var call = AgentToolCall.Create(
                task.Id,
                toolName,
                AgentToolJson.EmptyObject());
            var result = await _toolExecutor.ExecuteAsync(
                task,
                call,
                cancellationToken).ConfigureAwait(false);
            AddObservation(AgentModelObservation.FromResult(
                _nextObservationSequence++,
                result));
        }
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

    private async Task<AgentTaskState?> HandlePlanDecisionAsync(
        AgentModelDecision decision,
        int turn,
        CancellationToken cancellationToken)
    {
        if (decision.Plan is null)
        {
            return FailTask(
                "Agent model selected publish_plan without a plan.");
        }

        var task = RequireCurrentTask();
        if (!ValidateMachineCheckablePlan(decision.Plan, out var validationError))
        {
            AddSyntheticObservation(
                "publish_plan",
                AgentToolResultStatus.Rejected,
                validationError,
                "plan_evidence_required");
            return null;
        }

        if (_model is IAgentPlanCritic critic)
        {
            var review = await critic.ReviewPlanAsync(
                new AgentPlanReviewRequest(
                    task,
                    decision.Plan,
                    GetObservationContext(),
                    GetConversationContext(),
                    turn),
                cancellationToken).ConfigureAwait(false);
            if (!review.Accepted)
            {
                var issues = review.Issues.IsDefaultOrEmpty
                    ? review.Summary
                    : review.Summary + " " + string.Join(" ", review.Issues);
                AddSyntheticObservation(
                    "review_plan",
                    AgentToolResultStatus.Rejected,
                    LimitText(issues, 8_000),
                    "plan_rejected_by_critic");
                return null;
            }
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

        var rippleDeleteSteps = editingSteps.Count(step =>
            step.ExpectedEditingTool is "ripple_delete_range" or "ripple_delete_ranges");
        if (rippleDeleteSteps > 1)
        {
            error = "Multiple ripple-delete actions would invalidate later coordinates. Use one ripple_delete_ranges action for ranges measured on the same sequence revision.";
            return false;
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

            if (step.ExpectedEditingArguments is not { ValueKind: JsonValueKind.Object })
            {
                error = $"Plan step '{step.Title}' has no exact editing arguments.";
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

            if (!referenced.Any(item => SatisfiesEvidenceRequirement(
                    item,
                    step.EvidenceRequirement)))
            {
                error = $"Plan step '{step.Title}' needs successful {step.EvidenceRequirement.ToString().ToLowerInvariant()} evidence. The referenced observations do not provide that channel.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool SatisfiesEvidenceRequirement(
        AgentModelObservation observation,
        AgentEvidenceRequirement requirement)
    {
        if (observation.Status != AgentToolResultStatus.Succeeded)
        {
            return false;
        }

        if (requirement == AgentEvidenceRequirement.Timeline)
        {
            return observation.ToolName is "inspect_timeline" or
                "inspect_timeline_integrity" or "inspect_project" or "inspect_range";
        }

        return IsRangeEvidence(observation, requirement);
    }

    private static bool IsRangeEvidence(
        AgentModelObservation observation,
        AgentEvidenceRequirement requirement)
    {
        if (observation.ToolName is not ("inspect_range" or "inspect_boundary") ||
            observation.Data is not { } data ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("detail", out var detailElement))
        {
            return false;
        }

        var detail = detailElement.GetString()?.ToLowerInvariant();
        var detailMatches = requirement switch
        {
            AgentEvidenceRequirement.Frames => detail is "frames" or "all",
            AgentEvidenceRequirement.Audio => detail is "audio" or "all",
            AgentEvidenceRequirement.Transcript => detail is "transcript" or "all",
            AgentEvidenceRequirement.All => detail == "all",
            _ => true
        };
        if (!detailMatches)
        {
            return false;
        }

        if (data.TryGetProperty("analysis_deferred", out var deferredElement) &&
            deferredElement.ValueKind == JsonValueKind.True)
        {
            return false;
        }

        return requirement switch
        {
            AgentEvidenceRequirement.Frames => HasEvidenceProperty(data, "vision", "observations"),
            AgentEvidenceRequirement.Transcript => HasEvidenceProperty(data, "transcript", "cues"),
            AgentEvidenceRequirement.Audio => HasAudioEvidence(data),
            AgentEvidenceRequirement.All =>
                HasEvidenceProperty(data, "vision", "observations") && HasAudioEvidence(data),
            _ => true
        };
    }

    private static bool HasEvidenceProperty(
        JsonElement data,
        string propertyName,
        string collectionName)
    {
        if (data.TryGetProperty(propertyName, out var evidence) &&
            evidence.ValueKind == JsonValueKind.Object &&
            evidence.TryGetProperty(collectionName, out var values) &&
            values.ValueKind == JsonValueKind.Array &&
            values.GetArrayLength() > 0)
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
                   HasEvidenceProperty(nested, propertyName, collectionName));
    }

    private static bool HasAudioEvidence(JsonElement data)
        => (data.TryGetProperty("analysis", out var analysis) &&
            analysis.ValueKind == JsonValueKind.Object) ||
           (data.TryGetProperty("analyses", out var analyses) &&
            analyses.ValueKind == JsonValueKind.Array &&
            analyses.EnumerateArray().Any(item =>
                item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("status", out var status) &&
                string.Equals(status.GetString(), "succeeded", StringComparison.OrdinalIgnoreCase) &&
                item.TryGetProperty("observation", out var nested) &&
                nested.ValueKind == JsonValueKind.Object &&
                HasAudioEvidence(nested)));

    private ImmutableArray<AgentToolDescriptor> GetPlanningToolDescriptors()
        => _registry.Descriptors
            .Where(descriptor => !string.Equals(
                                     descriptor.Name,
                                     "inspect_agent_edits",
                                     StringComparison.OrdinalIgnoreCase))
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
            AgentObservationRetention.Trim(
                _observations,
                RequireCurrentTask(),
                _options.MaxObservationCount,
                _options.MaxObservationContextCharacters);
        }

        if (observation.Status == AgentToolResultStatus.Succeeded)
        {
            var task = RequireCurrentTask();
            var existing = task.Evidence.FirstOrDefault(item => item.Sequence == observation.Sequence);
            var data = observation.Data;
            var targetId = TryReadGuid(data, "sequence_id")
                           ?? TryReadGuid(data, "media_id")
                           ?? TryReadGuid(data, "target_id")
                           ?? task.SourceSequenceId;
            var record = new AgentEvidenceRecord(
                existing?.Id ?? Guid.NewGuid(),
                observation.Sequence,
                ToEvidenceChannel(observation.ToolName, data),
                observation.ToolName,
                targetId,
                TryReadInt64(data, "revision")
                ?? TryReadInt64(data, "source_revision")
                ?? task.SourceSequenceRevision,
                TryReadDouble(data, "start_seconds"),
                TryReadDouble(data, "end_seconds"),
                observation.Summary,
                ImmutableArray.Create(observation.Summary),
                TryReadString(data, "artifact_reference"),
                DateTimeOffset.UtcNow);
            _orchestrator.ReplaceEvidenceLedger(
                task.Evidence
                    .Where(item => item.Sequence != observation.Sequence)
                    .Append(record));
        }
    }

    private static AgentEvidenceChannel ToEvidenceChannel(
        string toolName,
        JsonElement? data)
    {
        var reportedChannel = TryReadString(data, "channel")?.ToLowerInvariant();
        if (reportedChannel is not null)
        {
            return reportedChannel switch
            {
                "editor_context" => AgentEvidenceChannel.EditorContext,
                "project" => AgentEvidenceChannel.Project,
                "timeline" => AgentEvidenceChannel.Timeline,
                "integrity" => AgentEvidenceChannel.Integrity,
                "frames" or "vision" or "all" => AgentEvidenceChannel.Frames,
                "audio" => AgentEvidenceChannel.Audio,
                "transcript" => AgentEvidenceChannel.Transcript,
                "recurrence" or "comparison" => AgentEvidenceChannel.Recurrence,
                "sequence_diff" => AgentEvidenceChannel.SequenceDiff,
                "edit_log" => AgentEvidenceChannel.EditLog,
                _ => AgentEvidenceChannel.Timeline
            };
        }

        return toolName.ToLowerInvariant() switch
        {
            "inspect_editor_context" => AgentEvidenceChannel.EditorContext,
            "inspect_project" => AgentEvidenceChannel.Project,
            "inspect_timeline_integrity" => AgentEvidenceChannel.Integrity,
            "compare_media_ranges" => AgentEvidenceChannel.Recurrence,
            "compare_sequences" => AgentEvidenceChannel.SequenceDiff,
            "inspect_agent_edits" => AgentEvidenceChannel.EditLog,
            "inspect_range" or "inspect_boundary" => AgentEvidenceChannel.Frames,
            _ => AgentEvidenceChannel.Timeline
        };
    }

    private static Guid? TryReadGuid(JsonElement? data, string propertyName)
        => data is { ValueKind: JsonValueKind.Object } value &&
           value.TryGetProperty(propertyName, out var property) &&
           property.TryGetGuid(out var result)
            ? result
            : null;

    private static long? TryReadInt64(JsonElement? data, string propertyName)
        => data is { ValueKind: JsonValueKind.Object } value &&
           value.TryGetProperty(propertyName, out var property) &&
           property.TryGetInt64(out var result)
            ? result
            : null;

    private static double? TryReadDouble(JsonElement? data, string propertyName)
        => data is { ValueKind: JsonValueKind.Object } value &&
           value.TryGetProperty(propertyName, out var property) &&
           property.TryGetDouble(out var result)
            ? result
            : null;

    private static string? TryReadString(JsonElement? data, string propertyName)
        => data is { ValueKind: JsonValueKind.Object } value &&
           value.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

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
