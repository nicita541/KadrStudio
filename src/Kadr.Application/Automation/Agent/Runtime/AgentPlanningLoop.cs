using System.Collections.Immutable;
using System.Text.Json;
using KadrStudio.Application.Automation.Agent.Diagnostics;
using KadrStudio.Application.Automation.Agent.Planning;
using KadrStudio.Application.Automation.Agent.Tools;
using KadrStudio.Application.Automation.Agent.Tools.ReadOnly;

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
    private const double DetailedLeadingContextSeconds = 30d;
    private const double DetailedTrailingContextSeconds = 60d;
    private const double MaximumDetailedWindowSeconds = 120d;

    private readonly AiAgentOrchestrator _orchestrator;
    private readonly AgentToolRegistry _registry;
    private readonly AgentToolExecutor _toolExecutor;
    private readonly IAgentModel _model;
    private readonly AgentPlanningLoopOptions _options;
    private readonly TaskBriefService _taskBriefService;
    private readonly AgentInvestigationRunner _investigationRunner;
    private readonly AgentPlanPublisher _planPublisher;
    private readonly Func<ImmutableArray<AgentConversationContextMessage>> _conversationProvider;
    private readonly IAgentDebugLog _debugLog;
    private readonly SemaphoreSlim _runGate = new(1, 1);

    private readonly List<AgentModelObservation> _observations = [];
    private readonly Dictionary<string, int> _boundaryInspectionCounts = new(StringComparer.Ordinal);
    private Guid? _memoryTaskId;
    private long? _memorySourceSequenceRevision;
    private int _nextObservationSequence = 1;
    private string? _lastToolSignature;
    private int _consecutiveIdenticalToolCalls;
    private bool _publishFromExistingEvidence;
    private int _lastRejectedPublicationEvidenceSequence;

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
        _taskBriefService = new TaskBriefService(_model as IAgentTaskInterpreter);
        _investigationRunner = new AgentInvestigationRunner(_model);
        _planPublisher = new AgentPlanPublisher(
            _orchestrator,
            new AgentPlanValidator(_registry),
            _model as IAgentPlanCritic);
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
                if (task.Brief is null && _taskBriefService.IsAvailable)
                {
                    _orchestrator.RecordProgress("Модель размышляет и формирует JSON понимания задачи…");
                    await SeedUnderstandingObservationsAsync(cancellationToken)
                        .ConfigureAwait(false);

                    var understanding = await _taskBriefService.UnderstandAsync(
                        new AgentModelTurnRequest(
                            task,
                            // The brief only needs the seeded editor/project facts.
                            // Full tool schemas are introduced on the investigation
                            // turn where the model can actually choose among them.
                            ImmutableArray<AgentToolDescriptor>.Empty,
                            GetObservationContext(),
                            GetConversationContext(),
                            0),
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

            task = RequireCurrentTask();
            if (task.Phase == AgentTaskPhase.Investigating &&
                task.Brief?.InvestigationStrategy == AgentInvestigationStrategy.ContentDiscovery)
            {
                if (TryReadSourceDuration(task.SourceSequenceId) is null)
                {
                    // Runtime observations are intentionally not persisted. On
                    // recovery, refresh only the read-only project metadata needed
                    // to map persisted evidence windows to the complete duration.
                    await SeedUnderstandingObservationsAsync(cancellationToken)
                        .ConfigureAwait(false);
                    task = RequireCurrentTask();
                }
                await EnsureDeterministicDiscoveryCoverageAsync(
                    task,
                    cancellationToken).ConfigureAwait(false);
                task = RequireCurrentTask();
            }
            await EnsureDeterministicObservedTextProbeAsync(
                task,
                cancellationToken).ConfigureAwait(false);

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

                RequirePublicationWhenInvestigationBudgetIsFull(task);

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
                        _publishFromExistingEvidence
                            ? AgentModelTurnDirective.PublishPlanFromExistingEvidence
                            : AgentModelTurnDirective.Investigate);

                    _orchestrator.RecordProgress("Модель размышляет и формирует JSON следующего исследовательского шага…");
                    decision = await _investigationRunner.DecideAsync(
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

                var safeProgress = BuildSafeProgress(decision);
                if (!string.IsNullOrWhiteSpace(safeProgress))
                {
                    _orchestrator.RecordProgress(
                        LimitText(safeProgress, _options.MaxProgressCharacters));
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

        if (TryCreateBoundaryInspectionKey(
                decision.ToolName,
                decision.ToolArguments,
                out var boundaryKey))
        {
            _boundaryInspectionCounts.TryGetValue(boundaryKey, out var equivalentCount);
            if (equivalentCount >= _options.MaxEquivalentBoundaryInspections)
            {
                AddSyntheticObservation(
                    decision.ToolName,
                    AgentToolResultStatus.Rejected,
                    "This candidate boundary was already measured enough times on the same channel. " +
                    "Reuse those observations, inspect a different part of the material, or publish a plan.",
                    "repeated_boundary_inspection");
                return;
            }

            _boundaryInspectionCounts[boundaryKey] = equivalentCount + 1;
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
            if (Observations.Any(observation =>
                    observation.Status == AgentToolResultStatus.Succeeded &&
                    observation.ToolName.Equals(toolName, StringComparison.OrdinalIgnoreCase) &&
                    !(observation.Data is { ValueKind: JsonValueKind.Object } restoredData &&
                      restoredData.TryGetProperty(
                          "restored_from_evidence_ledger",
                          out var restoredFlag) &&
                      restoredFlag.ValueKind == JsonValueKind.True)))
            {
                continue;
            }
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

    private async Task EnsureDeterministicDiscoveryCoverageAsync(
        AgentTaskState task,
        CancellationToken cancellationToken)
    {
        if (task.Brief?.InvestigationStrategy != AgentInvestigationStrategy.ContentDiscovery ||
            !_registry.TryGet("inspect_range", out var rangeTool) ||
            rangeTool is null ||
            rangeTool.Descriptor.Access != AgentToolAccess.ReadOnly ||
            TryReadSourceDuration(task.SourceSequenceId) is not { } duration ||
            duration <= 0.1)
        {
            return;
        }

        var reusableFrameEvidence = task.Evidence
            .Where(item =>
                item.TargetId == task.SourceSequenceId &&
                item.SourceRevision == task.SourceSequenceRevision &&
                (item.Capabilities & AgentEvidenceCapabilities.Frames) != 0 &&
                item.StartSeconds is not null &&
                item.EndSeconds is not null &&
                item.ToolName is "inspect_content_overview" or
                    "inspect_content_sample" or
                    "inspect_range")
            .ToArray();
        if (reusableFrameEvidence.Any(item =>
                item.StartSeconds!.Value <= 0.25d &&
                item.EndSeconds!.Value >= duration - 0.25d))
        {
            return;
        }

        const double maximumWindowSeconds = 120;
        const int maximumWindows = 16;
        var windows = BuildDiscoveryWindows(
            duration,
            maximumWindowSeconds,
            maximumWindows);
        _orchestrator.RecordProgress(
            $"Выполняю обзорное покрытие материала: {windows.Length} независимых диапазона по кадрам.");

        var samples = new List<DiscoveryCoverageSample>(windows.Length);
        var startedAt = DateTimeOffset.UtcNow;
        for (var index = 0; index < windows.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = windows[index];
            var reusable = reusableFrameEvidence
                .Where(item =>
                    item.StartSeconds!.Value <= window.Start + 0.25d &&
                    item.EndSeconds!.Value >= window.End - 0.25d)
                .OrderBy(item => item.EndSeconds!.Value - item.StartSeconds!.Value)
                .ThenByDescending(item => item.Sequence)
                .FirstOrDefault();
            if (reusable is not null)
            {
                samples.Add(new DiscoveryCoverageSample(
                    window.Start,
                    window.End,
                    "succeeded",
                    AgentRangeEvidenceSummary.BuildDiscoveryDigest(
                        reusable.Summary,
                        data: null),
                    null,
                    0,
                    HasFrames: true,
                    HasAudio:
                        (reusable.Capabilities & AgentEvidenceCapabilities.Audio) != 0));
                continue;
            }

            var arguments = AgentToolJson.ToElement(new
            {
                target_kind = "sequence",
                target_id = task.SourceSequenceId,
                start_seconds = window.Start,
                end_seconds = window.End,
                detail = "frames",
                query =
                    $"Coarse coverage {index + 1}/{windows.Length}. " +
                    "Опиши только видимые события, надписи, титры, повторяющиеся визуальные признаки " +
                    $"и смены контекста с абсолютными таймкодами. Контекст задачи: {task.Brief.Goal}"
            });
            AgentToolResult? result = null;
            var attemptCount = 0;
            for (var attempt = 1;
                 attempt <= _options.MaxDiscoveryCoverageAttemptsPerWindow;
                 attempt++)
            {
                attemptCount = attempt;
                result = await _toolExecutor.ExecuteAsync(
                    task,
                    AgentToolCall.Create(task.Id, "inspect_range", arguments),
                    cancellationToken).ConfigureAwait(false);
                if (result.IsSuccess &&
                    (result.EvidenceCapabilities & AgentEvidenceCapabilities.Frames) != 0)
                {
                    break;
                }
            }

            if (result is null)
            {
                throw new InvalidOperationException(
                    "Discovery coverage did not execute its frame sensor.");
            }
            samples.Add(new DiscoveryCoverageSample(
                window.Start,
                window.End,
                result.Status.ToString().ToLowerInvariant(),
                AgentRangeEvidenceSummary.BuildDiscoveryDigest(
                    result.Summary,
                    result.Data),
                result.ErrorCode,
                attemptCount,
                result.IsSuccess &&
                (result.EvidenceCapabilities & AgentEvidenceCapabilities.Frames) != 0,
                result.IsSuccess &&
                (result.EvidenceCapabilities & AgentEvidenceCapabilities.Audio) != 0));
        }

        var complete = samples.All(sample => sample.HasFrames);
        var completeAudio = complete && samples.All(sample => sample.HasAudio);
        var summary = complete
            ? $"Deterministic content overview covered 0-{duration:0.###}s in {samples.Count} independent frame-sensor windows."
            : $"Deterministic content overview was incomplete: " +
              $"{samples.Count(sample => sample.HasFrames)}/{samples.Count} frame-sensor windows succeeded.";
        var detailedSummary = summary + "\n" + string.Join(
            "\n",
            samples.Select(sample =>
                $"{sample.Start:0.###}-{sample.End:0.###}s [{sample.Status}]: {sample.Summary}"));
        var data = AgentToolJson.ToElement(new
        {
            channel = "frames",
            sequence_id = task.SourceSequenceId,
            source_revision = task.SourceSequenceRevision,
            start_seconds = 0,
            end_seconds = duration,
            coverage_complete = complete,
            samples = samples.Select(sample => new
            {
                start_seconds = sample.Start,
                end_seconds = sample.End,
                status = sample.Status,
                error_code = sample.ErrorCode,
                    attempt_count = sample.AttemptCount,
                    frames_available = sample.HasFrames,
                    audio_available = sample.HasAudio
                }).ToArray()
        });

        // A single transient sensor failure must not erase every other successful
        // frame observation. Do not claim one continuous range in that case: retain
        // each successful window as its own typed, correctly bounded observation.
        if (!complete)
        {
            foreach (var sample in samples.Where(sample => sample.HasFrames))
            {
                if (RequireCurrentTask().Evidence.Any(item =>
                        item.ToolName.Equals(
                            "inspect_content_sample",
                            StringComparison.OrdinalIgnoreCase) &&
                        item.SourceRevision == task.SourceSequenceRevision &&
                        item.TargetId == task.SourceSequenceId &&
                        (item.Capabilities & AgentEvidenceCapabilities.Frames) != 0 &&
                        item.StartSeconds is { } existingStart &&
                        item.EndSeconds is { } existingEnd &&
                        existingStart <= sample.Start + 0.25d &&
                        existingEnd >= sample.End - 0.25d))
                {
                    continue;
                }
                var sampleData = AgentToolJson.ToElement(new
                {
                    channel = "frames",
                    sequence_id = task.SourceSequenceId,
                    source_revision = task.SourceSequenceRevision,
                    start_seconds = sample.Start,
                    end_seconds = sample.End,
                    coverage_window = true,
                    attempt_count = sample.AttemptCount
                });
                var partial = new AgentToolResult(
                    Guid.NewGuid(),
                    "inspect_content_sample",
                    AgentToolResultStatus.Succeeded,
                    $"Content overview sample {sample.Start:0.###}-{sample.End:0.###}s. {sample.Summary}",
                    sampleData,
                    null,
                    startedAt,
                    DateTimeOffset.UtcNow,
                    AgentEvidenceCapabilities.Frames |
                    (sample.HasAudio
                        ? AgentEvidenceCapabilities.Audio
                        : AgentEvidenceCapabilities.None));
                AddObservation(AgentModelObservation.FromResult(
                    _nextObservationSequence++,
                    partial));
            }
        }

        var aggregate = new AgentToolResult(
            Guid.NewGuid(),
            "inspect_content_overview",
            complete ? AgentToolResultStatus.Succeeded : AgentToolResultStatus.Failed,
            detailedSummary,
            data,
            complete ? null : "content_overview_incomplete",
            startedAt,
            DateTimeOffset.UtcNow,
            complete
                ? AgentEvidenceCapabilities.Frames |
                  (completeAudio
                      ? AgentEvidenceCapabilities.Audio
                      : AgentEvidenceCapabilities.None)
                : AgentEvidenceCapabilities.None);
        AddObservation(AgentModelObservation.FromResult(
            _nextObservationSequence++,
            aggregate));
        if (!complete)
        {
            _orchestrator.RecordProgress(summary);
        }
    }

    private async Task EnsureDeterministicObservedTextProbeAsync(
        AgentTaskState task,
        CancellationToken cancellationToken)
    {
        var isContentDiscovery =
            task.Brief?.InvestigationStrategy == AgentInvestigationStrategy.ContentDiscovery ||
            task.Evidence.Any(item => item.ToolName.Equals(
                "inspect_content_overview",
                StringComparison.OrdinalIgnoreCase));
        if (!isContentDiscovery ||
            !_registry.TryGet("inspect_range", out var rangeTool) ||
            rangeTool is null ||
            rangeTool.Descriptor.Access != AgentToolAccess.ReadOnly)
        {
            return;
        }

        var duration = TryReadSourceDuration(task.SourceSequenceId)
                       ?? task.Evidence
                           .Where(item => item.TargetId == task.SourceSequenceId)
                           .Select(item => item.EndSeconds)
                           .Where(value => value is not null)
                           .Select(value => value!.Value)
                           .DefaultIfEmpty(0)
                           .Max();
        if (duration <= 0.1)
        {
            return;
        }

        var unprobed = AgentObservedTextActivityIndexer.Build(task.Evidence)
            .Where(region => !HasDetailedTextActivityProbe(task, region, duration))
            .ToArray();
        if (unprobed.Length == 0)
        {
            return;
        }

        // Investigate the densest factual OCR region and, independently, the
        // latest edge region. This is content-agnostic: it does not call either
        // one an opening/ending, but prevents a long file's tail from being
        // reduced to a few coarse samples or omitted from the planner context.
        var candidates = new List<(AgentObservedTextActivityRegion Region, bool IsTailEdge)>();
        foreach (var anchor in unprobed.Take(2))
        {
            AddCandidate(
                ExpandWithNearbyObservedTextRegions(anchor, unprobed),
                isTailEdge: false);
        }
        var latestEdge = unprobed
            .Where(region => region.StartSeconds >= duration * 0.75)
            .OrderByDescending(region => region.EndSeconds)
            .FirstOrDefault();
        if (latestEdge is not null)
        {
            AddCandidate(
                ExpandWithNearbyObservedTextRegions(latestEdge, unprobed),
                isTailEdge: true);
        }

        void AddCandidate(
            AgentObservedTextActivityRegion region,
            bool isTailEdge)
        {
            var existing = candidates.FindIndex(candidate =>
                Math.Abs(candidate.Region.StartSeconds - region.StartSeconds) < 0.5 &&
                Math.Abs(candidate.Region.EndSeconds - region.EndSeconds) < 0.5);
            if (existing < 0)
            {
                candidates.Add((region, isTailEdge));
            }
            else if (isTailEdge && !candidates[existing].IsTailEdge)
            {
                candidates[existing] = (candidates[existing].Region, true);
            }
        }

        const double minimumProbeSeconds = 45d;
        foreach (var selectedCandidate in candidates)
        {
            var candidate = selectedCandidate.Region;
            var start = Math.Max(0, candidate.StartSeconds - DetailedLeadingContextSeconds);
            var end = Math.Min(duration, candidate.EndSeconds + DetailedTrailingContextSeconds);
            if (selectedCandidate.IsTailEdge)
            {
                // A late factual text region can begin after the visual/audio
                // transition that introduced it. Measure the complete final
                // sensor-sized window without assuming what that content means.
                start = Math.Min(start, Math.Max(0, duration - MaximumDetailedWindowSeconds));
                end = duration;
            }
            var center = (candidate.StartSeconds + candidate.EndSeconds) / 2d;
            if (end - start < minimumProbeSeconds)
            {
                start = Math.Max(0, center - minimumProbeSeconds / 2d);
                end = Math.Min(duration, start + minimumProbeSeconds);
                start = Math.Max(0, end - minimumProbeSeconds);
            }

            var windows = BuildExactCoverageWindows(start, end, MaximumDetailedWindowSeconds);
            var hasMeasuredAudio = HasCurrentCapability(
                RequireCurrentTask(),
                AgentEvidenceCapabilities.Audio);
            var preferredDetail = hasMeasuredAudio ? "all" : "frames";
            _orchestrator.RecordProgress(
                $"Подробно проверяю весь фактический OCR-регион {start:0.###}–{end:0.###}с " +
                $"в {windows.Length} независимых окнах; использую все реально доступные каналы.");
            foreach (var window in windows)
            {
                AgentToolResult result;
                async Task<AgentToolResult> InspectAsync(string detail)
                {
                    var currentTask = RequireCurrentTask();
                    var arguments = AgentToolJson.ToElement(new
                    {
                        target_kind = "sequence",
                        target_id = currentTask.SourceSequenceId,
                        start_seconds = window.Start,
                        end_seconds = window.End,
                        detail,
                        query =
                            "Deterministic detail probe selected from observed OCR density. " +
                            "Return neutral measured facts from the requested channels only."
                    });
                    return await _toolExecutor.ExecuteAsync(
                        currentTask,
                        AgentToolCall.Create(currentTask.Id, "inspect_range", arguments),
                        cancellationToken).ConfigureAwait(false);
                }

                result = await InspectAsync(preferredDetail).ConfigureAwait(false);
                AddObservation(AgentModelObservation.FromResult(
                    _nextObservationSequence++,
                    result));
                if (!result.IsSuccess &&
                    preferredDetail == "all" &&
                    result.ErrorCode is "media_has_no_audio" or "transcript_evidence_incomplete")
                {
                    // A universal agent must remain useful for silent media and for a
                    // temporarily unavailable transcript sensor. Frames still carry
                    // technical audio capability when an audio stream was measured.
                    result = await InspectAsync("frames").ConfigureAwait(false);
                    AddObservation(AgentModelObservation.FromResult(
                        _nextObservationSequence++,
                        result));
                }
                if (result.IsSuccess)
                {
                    await InspectObservedTransitionCandidatesAsync(
                        RequireCurrentTask(),
                        candidate,
                        duration,
                        result.Data,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static AgentObservedTextActivityRegion ExpandWithNearbyObservedTextRegions(
        AgentObservedTextActivityRegion anchor,
        IReadOnlyCollection<AgentObservedTextActivityRegion> regions)
    {
        const double nearbyGapSeconds = 45d;
        var nearby = regions
            .Where(region =>
                region.EndSeconds >= anchor.StartSeconds - nearbyGapSeconds &&
                region.StartSeconds <= anchor.EndSeconds + nearbyGapSeconds)
            .ToArray();
        if (nearby.Length <= 1)
        {
            return anchor;
        }

        return new AgentObservedTextActivityRegion(
            nearby.Min(region => region.StartSeconds),
            nearby.Max(region => region.EndSeconds),
            nearby
                .SelectMany(region => region.Facts)
                .Distinct()
                .OrderBy(fact => fact.Seconds)
                .ToImmutableArray());
    }

    private async Task InspectObservedTransitionCandidatesAsync(
        AgentTaskState task,
        AgentObservedTextActivityRegion region,
        double durationSeconds,
        JsonElement? detailedObservation,
        CancellationToken cancellationToken)
    {
        if (!_registry.TryGet("inspect_boundary", out var boundaryTool) ||
            boundaryTool is null ||
            boundaryTool.Descriptor.Access != AgentToolAccess.ReadOnly)
        {
            return;
        }

        foreach (var candidate in AgentObservedTransitionCandidateIndexer.Build(
                     detailedObservation,
                     region,
                     durationSeconds))
        {
            await InspectObservedTransitionCandidateAsync(
                candidate,
                cancellationToken).ConfigureAwait(false);
        }

        var edgeThreshold = Math.Max(90d, durationSeconds * 0.1d);
        foreach (var edgeRegion in AgentObservedTextActivityIndexer.Build(
                         RequireCurrentTask().Evidence)
                     .Where(item => item.StartSeconds >= durationSeconds - edgeThreshold)
                     .Take(2))
        {
            foreach (var seconds in new[]
                     {
                         Math.Max(0, edgeRegion.StartSeconds - 20d),
                         edgeRegion.StartSeconds,
                         edgeRegion.StartSeconds + 20d,
                         edgeRegion.StartSeconds + 40d,
                         edgeRegion.StartSeconds + 60d
                     })
            {
                if (seconds >= durationSeconds)
                {
                    continue;
                }
                await InspectObservedTransitionCandidateAsync(
                    new AgentObservedTransitionCandidate(
                        seconds,
                        "near_end_text_activity_onset"),
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task InspectObservedTransitionCandidateAsync(
        AgentObservedTransitionCandidate candidate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var task = RequireCurrentTask();
        if (task.Evidence.Any(item =>
                item.SourceRevision == task.SourceSequenceRevision &&
                item.TargetId == task.SourceSequenceId &&
                item.ToolName.Equals("inspect_boundary", StringComparison.OrdinalIgnoreCase) &&
                item.StartSeconds is { } existingStart &&
                item.EndSeconds is { } existingEnd &&
                Math.Abs(
                    (item.BoundarySeconds ?? (existingStart + existingEnd) / 2d) -
                    candidate.Seconds) <= 1d))
        {
            return;
        }

        _orchestrator.RecordProgress(
            $"Проверяю наблюдаемый переход около {candidate.Seconds:0.###}с ({candidate.Signal}).");
        var arguments = AgentToolJson.ToElement(new
        {
            target_kind = "sequence",
            target_id = task.SourceSequenceId,
            at_seconds = candidate.Seconds,
            window_seconds = 15,
            detail = HasCurrentCapability(task, AgentEvidenceCapabilities.Transcript)
                ? "all"
                : "frames",
            query =
                $"Deterministic boundary probe for factual signal '{candidate.Signal}'. " +
                "Describe neutral differences and continuity on both sides."
        });
        var result = await _toolExecutor.ExecuteAsync(
            task,
            AgentToolCall.Create(task.Id, "inspect_boundary", arguments),
            cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            result = result with
            {
                Summary =
                    $"Deterministic candidate provenance: {candidate.Signal} at " +
                    $"{candidate.Seconds:0.###}s. This is a factual search signal, not a semantic label.\n" +
                    result.Summary
            };
        }
        AddObservation(AgentModelObservation.FromResult(
            _nextObservationSequence++,
            result));
    }

    private static bool HasCurrentCapability(
        AgentTaskState task,
        AgentEvidenceCapabilities capability)
        => task.Evidence.Any(evidence =>
            evidence.TargetId == task.SourceSequenceId &&
            evidence.SourceRevision == task.SourceSequenceRevision &&
            (evidence.Capabilities & capability) == capability);

    private static bool HasDetailedTextActivityProbe(
        AgentTaskState task,
        AgentObservedTextActivityRegion region,
        double durationSeconds)
    {
        var expectedStart = Math.Max(
            0,
            region.StartSeconds - DetailedLeadingContextSeconds);
        var expectedEnd = Math.Min(
            durationSeconds,
            region.EndSeconds + DetailedTrailingContextSeconds);
        if (region.StartSeconds >= durationSeconds * 0.75d)
        {
            expectedStart = Math.Min(
                expectedStart,
                Math.Max(0, durationSeconds - MaximumDetailedWindowSeconds));
            expectedEnd = durationSeconds;
        }
        var covered = task.Evidence
            .Where(item =>
                item.SourceRevision == task.SourceSequenceRevision &&
                item.TargetId == task.SourceSequenceId &&
                item.ToolName.Equals("inspect_range", StringComparison.OrdinalIgnoreCase) &&
                (item.Capabilities & AgentEvidenceCapabilities.Frames) != 0 &&
                item.StartSeconds is not null &&
                item.EndSeconds is not null)
            .Select(item => (
                Math.Max(expectedStart, item.StartSeconds!.Value),
                Math.Min(expectedEnd, item.EndSeconds!.Value)));
        return MergeCoveredSeconds(covered) >=
               Math.Max(0, expectedEnd - expectedStart - 0.5);
    }

    private static ImmutableArray<(double Start, double End)> BuildExactCoverageWindows(
        double start,
        double end,
        double maximumWindowSeconds)
    {
        var duration = Math.Max(0, end - start);
        if (duration <= 0)
        {
            return [];
        }

        var count = Math.Max(1, (int)Math.Ceiling(duration / maximumWindowSeconds));
        var width = duration / count;
        var result = ImmutableArray.CreateBuilder<(double Start, double End)>(count);
        for (var index = 0; index < count; index++)
        {
            result.Add((
                start + index * width,
                index == count - 1 ? end : start + (index + 1) * width));
        }
        return result.ToImmutable();
    }

    private double? TryReadSourceDuration(Guid sourceSequenceId)
    {
        foreach (var observation in Observations
                     .Where(item => item.Status == AgentToolResultStatus.Succeeded)
                     .OrderByDescending(item => item.Sequence))
        {
            if (observation.Data is not { ValueKind: JsonValueKind.Object } data)
            {
                continue;
            }
            if (data.TryGetProperty("sequence_id", out var sequenceId) &&
                sequenceId.TryGetGuid(out var directId) &&
                directId == sourceSequenceId &&
                TryReadDouble(data, "duration_seconds") is { } directDuration)
            {
                return directDuration;
            }
            if (!data.TryGetProperty("sequences", out var sequences) ||
                sequences.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (var sequence in sequences.EnumerateArray())
            {
                if (TryReadGuid(sequence, "id") == sourceSequenceId &&
                    TryReadDouble(sequence, "duration_seconds") is { } duration)
                {
                    return duration;
                }
            }
        }
        return null;
    }

    private static ImmutableArray<(double Start, double End)> BuildDiscoveryWindows(
        double duration,
        double maximumWindowSeconds,
        int maximumWindows)
    {
        var required = Math.Max(1, (int)Math.Ceiling(duration / maximumWindowSeconds));
        var count = Math.Min(required, maximumWindows);
        var result = ImmutableArray.CreateBuilder<(double Start, double End)>(count);
        if (required <= maximumWindows)
        {
            var window = duration / count;
            for (var index = 0; index < count; index++)
            {
                result.Add((index * window, index == count - 1 ? duration : (index + 1) * window));
            }
        }
        else
        {
            var lastStart = Math.Max(0, duration - maximumWindowSeconds);
            for (var index = 0; index < count; index++)
            {
                var start = count == 1 ? 0 : lastStart * index / (count - 1);
                result.Add((start, Math.Min(duration, start + maximumWindowSeconds)));
            }
        }
        return result.ToImmutable();
    }

    private static double MergeCoveredSeconds(
        IEnumerable<(double Start, double End)> source)
    {
        var ranges = source
            .Where(item => item.End > item.Start)
            .OrderBy(item => item.Start)
            .ToArray();
        if (ranges.Length == 0)
        {
            return 0;
        }

        var total = 0d;
        var start = ranges[0].Start;
        var end = ranges[0].End;
        foreach (var range in ranges.Skip(1))
        {
            if (range.Start <= end)
            {
                end = Math.Max(end, range.End);
                continue;
            }
            total += end - start;
            start = range.Start;
            end = range.End;
        }
        return total + end - start;
    }

    /// <summary>
    /// Converts a retry after a malformed model response into a bounded plan
    /// publication attempt when this task already owns reusable, typed evidence.
    /// This is task-agnostic: no edit type, tool name, range, or media position is
    /// inferred here. If validation rejects the plan, normal investigation is
    /// reopened on the following turn.
    /// </summary>
    public bool PrepareRetryFromExistingEvidence()
    {
        var task = RequireCurrentTask();
        EnsureMemoryFor(task);

        var hasTypedEvidence = task.Brief is { Kind: AgentTaskKind.Edit or AgentTaskKind.Mixed } &&
                               task.Evidence.Any(item =>
                                   item.SourceRevision == task.SourceSequenceRevision &&
                                   item.Capabilities != AgentEvidenceCapabilities.None);
        if (hasTypedEvidence &&
            (task.Brief?.InvestigationStrategy == AgentInvestigationStrategy.ContentDiscovery ||
             task.Evidence.Any(item => item.ToolName.Equals(
                 "inspect_content_overview",
                 StringComparison.OrdinalIgnoreCase))) &&
            !task.Evidence.Any(item =>
                item.SourceRevision == task.SourceSequenceRevision &&
                item.ToolName.Equals("inspect_boundary", StringComparison.OrdinalIgnoreCase) &&
                item.Capabilities != AgentEvidenceCapabilities.None))
        {
            // A coarse overview is reusable after restart, but it cannot produce
            // exact semantic edit coordinates on its own. Reopen investigation
            // instead of forcing the model to fabricate a publishable plan.
            hasTypedEvidence = false;
        }
        if (!hasTypedEvidence)
        {
            _publishFromExistingEvidence = false;
            return false;
        }

        lock (_observations)
        {
            hasTypedEvidence = _observations.Any(item =>
                item.Status == AgentToolResultStatus.Succeeded &&
                item.EvidenceCapabilities != AgentEvidenceCapabilities.None);
        }

        _publishFromExistingEvidence = hasTypedEvidence;
        return hasTypedEvidence;
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

        var publication = await _planPublisher.PublishAsync(
            RequireCurrentTask(),
            decision.Plan,
            GetObservationContext(),
            GetConversationContext(),
            turn,
            cancellationToken).ConfigureAwait(false);
        if (publication.IsPublished)
        {
            _publishFromExistingEvidence = false;
            return publication.PublishedState;
        }

        // A deterministic validator or independent critic found a real gap.
        // Only plan-shape mistakes can be republished without more research;
        // every evidence or semantic rejection reopens the read-only catalog.
        _lastRejectedPublicationEvidenceSequence = RequireCurrentTask().Evidence
            .Select(item => item.Sequence)
            .DefaultIfEmpty(0)
            .Max();
        var currentTask = RequireCurrentTask();
        _publishFromExistingEvidence =
            publication.ErrorCode == "plan_invalid" &&
            currentTask.Evidence.Any(item =>
                item.SourceRevision == currentTask.SourceSequenceRevision &&
                item.ToolName.Equals("inspect_boundary", StringComparison.OrdinalIgnoreCase) &&
                item.Capabilities != AgentEvidenceCapabilities.None);

        _orchestrator.RecordProgress(
            "План отклонён проверкой: " + LimitText(publication.Error, 450));

        Log(
            RequireCurrentTask(),
            "plan_publication_rejected",
            turn,
            "Plan publication was rejected by deterministic validation or independent review.",
            $"error_code={publication.ErrorCode}; error={LimitText(publication.Error, 8_000)}");

        AddSyntheticObservation(
            publication.ErrorCode == "plan_rejected_by_critic"
                ? "review_plan"
                : "publish_plan",
            AgentToolResultStatus.Rejected,
            LimitText(publication.Error, 8_000),
            publication.ErrorCode);
        return null;
    }

    private static string BuildSafeProgress(AgentModelDecision decision)
        => decision.Action switch
        {
            AgentModelActionKind.UseTool =>
                $"Выполняю read-only измерение: {decision.ToolName}.",
            AgentModelActionKind.PublishPlan =>
                "Формирую и проверяю точный монтажный план по собранным доказательствам.",
            AgentModelActionKind.AskUser =>
                "Найден блокирующий вопрос, который нельзя доказать инструментами.",
            AgentModelActionKind.CompleteReadOnly =>
                "Проверяю доказанный ответ перед завершением.",
            _ => string.Empty
        };

    private ImmutableArray<AgentToolDescriptor> GetPlanningToolDescriptors()
        => _registry.Descriptors
            .Where(descriptor => !string.Equals(
                                     descriptor.Name,
                                     "inspect_agent_edits",
                                     StringComparison.OrdinalIgnoreCase))
            .Where(descriptor => !_publishFromExistingEvidence ||
                                 descriptor.Access == AgentToolAccess.Editing)
            .ToImmutableArray();

    private void RequirePublicationWhenInvestigationBudgetIsFull(AgentTaskState task)
    {
        if (_publishFromExistingEvidence ||
            task.Brief is not { Kind: AgentTaskKind.Edit or AgentTaskKind.Mixed })
        {
            return;
        }

        var successful = task.Evidence
            .Where(item =>
                item.SourceRevision == task.SourceSequenceRevision &&
                item.Capabilities != AgentEvidenceCapabilities.None &&
                (item.Capabilities &
                 (AgentEvidenceCapabilities.Project |
                  AgentEvidenceCapabilities.EditorContext)) == 0)
            .ToArray();

        if (successful.Length < _options.MaxSuccessfulInvestigationsBeforePublication)
        {
            return;
        }

        var latestSequence = successful.Max(item => item.Sequence);
        if (latestSequence <= _lastRejectedPublicationEvidenceSequence)
        {
            return;
        }

        _publishFromExistingEvidence = true;
    }

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
        var retainedObservation = CompactObservationForMemory(observation);
        lock (_observations)
        {
            _observations.Add(retainedObservation);
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
                TryReadInt64(data, "source_revision")
                ?? TryReadInt64(data, "sequence_revision")
                ?? task.SourceSequenceRevision,
                TryReadDouble(data, "start_seconds"),
                TryReadDouble(data, "end_seconds"),
                observation.Summary,
                ImmutableArray.Create(observation.Summary),
                TryReadString(data, "artifact_reference"),
                DateTimeOffset.UtcNow,
                observation.EvidenceCapabilities,
                TryReadDouble(data, "boundary_at_seconds"));
            _orchestrator.ReplaceEvidenceLedger(
                task.Evidence
                    .Where(item => item.Sequence != observation.Sequence)
                    .Append(record));
        }
    }

    private static AgentModelObservation CompactObservationForMemory(
        AgentModelObservation observation)
    {
        if (observation.Data is not { ValueKind: JsonValueKind.Object } data ||
            data.GetRawText().Length <= 20_000)
        {
            return observation;
        }

        var retained = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var name in new[]
                 {
                     "channel", "project_revision", "sequence_id", "sequence_revision",
                     "revision", "source_revision", "draft_revision", "target",
                     "start_seconds", "end_seconds", "detail", "truncated",
                     "boundary_at_seconds", "requested_window_seconds",
                     "artifact_reference", "recommended_next_inspection", "next_cursor",
                     "total_matches", "gap_count", "overlap_count", "link_issue_count",
                     "edit_count"
                 })
        {
            if (data.TryGetProperty(name, out var property))
            {
                retained[name] = property.Clone();
            }
        }

        retained["observation_data_compacted"] = true;
        retained["omitted_character_count"] = data.GetRawText().Length;
        return observation with { Data = AgentToolJson.ToElement(retained) };
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
           property.ValueKind == JsonValueKind.String &&
           property.TryGetGuid(out var result)
            ? result
            : null;

    private static long? TryReadInt64(JsonElement? data, string propertyName)
        => data is { ValueKind: JsonValueKind.Object } value &&
           value.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.Number &&
           property.TryGetInt64(out var result)
            ? result
            : null;

    private static double? TryReadDouble(JsonElement? data, string propertyName)
        => data is { ValueKind: JsonValueKind.Object } value &&
           value.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.Number &&
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
            foreach (var evidence in task.Evidence
                         .Where(item => task.SourceSequenceRevision is null ||
                                        item.SourceRevision == task.SourceSequenceRevision)
                         .GroupBy(item => item.Sequence)
                         .Select(group => group.Last())
                         .OrderBy(item => item.Sequence))
            {
                var restoredData = AgentToolJson.ToElement(new
                {
                    target_id = evidence.TargetId,
                    source_revision = evidence.SourceRevision,
                    start_seconds = evidence.StartSeconds,
                    end_seconds = evidence.EndSeconds,
                    boundary_at_seconds = evidence.BoundarySeconds,
                    channel = evidence.Channel.ToString().ToLowerInvariant(),
                    artifact_reference = evidence.ArtifactReference,
                    restored_from_evidence_ledger = true
                });
                _observations.Add(new AgentModelObservation(
                    evidence.Sequence,
                    evidence.ToolName,
                    AgentToolResultStatus.Succeeded,
                    $"Restored typed evidence E{evidence.Sequence}; see evidence_ledger.",
                    restoredData,
                    null,
                    evidence.Capabilities));
            }

            AgentObservationRetention.Trim(
                _observations,
                task,
                _options.MaxObservationCount,
                _options.MaxObservationContextCharacters);
        }

        _memoryTaskId = task.Id;
        _memorySourceSequenceRevision = task.SourceSequenceRevision;
        _nextObservationSequence = task.Evidence
            .Select(item => item.Sequence)
            .DefaultIfEmpty(0)
            .Max() + 1;
        _lastToolSignature = null;
        _consecutiveIdenticalToolCalls = 0;
        _boundaryInspectionCounts.Clear();
        _publishFromExistingEvidence = false;
        _lastRejectedPublicationEvidenceSequence = 0;
    }

    private AgentTaskState RequireCurrentTask()
        => _orchestrator.CurrentTask
           ?? throw new AgentTaskTransitionException(
               "There is no active AI agent task.");

    private static bool TryCreateBoundaryInspectionKey(
        string toolName,
        JsonElement arguments,
        out string key)
    {
        key = string.Empty;
        if (!string.Equals(toolName, "inspect_boundary", StringComparison.OrdinalIgnoreCase) ||
            arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("at_seconds", out var atValue) ||
            !atValue.TryGetDouble(out var atSeconds) ||
            !double.IsFinite(atSeconds))
        {
            return false;
        }

        var targetKind = TryReadString(arguments, "target_kind") ?? "sequence";
        var targetId = TryReadGuid(arguments, "target_id")?.ToString("N") ?? "default";
        var detail = TryReadString(arguments, "detail") ?? "all";
        var quarterSecond = Math.Round(
            atSeconds * 4,
            MidpointRounding.AwayFromZero);
        key = $"{targetKind.Trim().ToLowerInvariant()}|{targetId}|" +
              $"{detail.Trim().ToLowerInvariant()}|{quarterSecond:0}";
        return true;
    }

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

    private sealed record DiscoveryCoverageSample(
        double Start,
        double End,
        string Status,
        string Summary,
        string? ErrorCode,
        int AttemptCount,
        bool HasFrames,
        bool HasAudio);

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
