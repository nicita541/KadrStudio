using System.Collections.Immutable;
using System.Diagnostics;
using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Automation.Editorial;

public sealed class EditorialPipeline(
    IMediaUnderstandingIndexer indexer,
    IEditorialDirector director,
    IEditorialCritic critic,
    IEvidenceGapResolver gapResolver,
    IHierarchicalMediaRetriever? retriever = null,
    IMontageGraphValidatorV2? validator = null,
    IMontageGraphCompilerV2? compiler = null,
    IDraftQualityAnalyzer? qualityAnalyzer = null,
    RejectedMontageGraphLedger? rejectedGraphs = null,
    IEditorialTelemetrySink? telemetry = null,
    MontageGraphTargetResolver? targetResolver = null)
{
    private readonly IMediaUnderstandingIndexer _indexer = indexer ?? throw new ArgumentNullException(nameof(indexer));
    private readonly IEditorialDirector _director = director ?? throw new ArgumentNullException(nameof(director));
    private readonly IEditorialCritic _critic = critic ?? throw new ArgumentNullException(nameof(critic));
    private readonly IEvidenceGapResolver _gapResolver = gapResolver ?? throw new ArgumentNullException(nameof(gapResolver));
    private readonly IHierarchicalMediaRetriever _retriever = retriever ?? new HierarchicalMediaRetriever();
    private readonly IMontageGraphValidatorV2 _validator = validator ?? new MontageGraphValidatorV2();
    private readonly IMontageGraphCompilerV2 _compiler = compiler ?? new MontageGraphCompilerV2();
    private readonly IDraftQualityAnalyzer _qualityAnalyzer = qualityAnalyzer ?? new DeterministicDraftQualityAnalyzer();
    private readonly RejectedMontageGraphLedger _rejectedGraphs = rejectedGraphs ?? new RejectedMontageGraphLedger();
    private readonly IEditorialTelemetrySink _telemetry = telemetry ?? NullEditorialTelemetrySink.Instance;
    private readonly MontageGraphTargetResolver _targetResolver = targetResolver ?? new MontageGraphTargetResolver();

    public async Task<EditorialPipelineResult> RunAsync(
        EditorialPipelineRequest request,
        IProgress<EditorialPipelineProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.UserRequest))
            throw new ArgumentException("Editorial request is required.", nameof(request));
        var project = request.Project.EnsureSequenceContainer();
        var sequence = project.FindSequence(request.SourceSequenceId)
            ?? throw new InvalidOperationException("Source sequence was not found.");
        var taskId = request.TaskId is { } requestedTaskId && requestedTaskId != Guid.Empty
            ? requestedTaskId
            : Guid.NewGuid();
        var pipelineStarted = Stopwatch.GetTimestamp();

        var profile = request.Profile;
        if (profile is null)
        {
            Report(progress, EditorialPipelineStage.Indexing, 0, "Подбираю профиль анализа материала.");
            var suggested = await _director.SuggestProfileAsync(
                project, sequence.Id, request.UserRequest, cancellationToken).ConfigureAwait(false);
            profile = MontageProfileCatalog.Get(suggested);
        }

        Report(progress, EditorialPipelineStage.Indexing, 0, "Проверяю мультимодальный индекс.");
        var stageStarted = Stopwatch.GetTimestamp();
        var indexes = await _indexer.EnsureIndexesAsync(
            project, sequence.Id, profile, progress, cancellationToken).ConfigureAwait(false);
        EnsureIndexCoverage(project, sequence, indexes);
        Record(taskId, "stage_duration_ms", Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds,
            ("stage", EditorialPipelineStage.Indexing.ToString()));
        RecordCoverage(taskId, indexes);

        Report(progress, EditorialPipelineStage.Directing, 0.1, "Формирую режиссёрский замысел.");
        stageStarted = Stopwatch.GetTimestamp();
        var profileUsesExactNamedSections = profile.DefaultScopePolicy is
        {
            Intent: EditorialIntentKind.RemoveNamedSections,
            Preservation: PreservationPolicy.ExactComplement
        };
        var brief = await _director.CreateBriefAsync(
            project,
            sequence.Id,
            request.UserRequest,
            profile,
            request.RevisionFeedback,
            cancellationToken).ConfigureAwait(false);
        if (profileUsesExactNamedSections)
            brief = ConstrainExactNamedSectionBrief(brief, request.UserRequest, profile);
        if (brief.Profile.Id != profile.Id || brief.Profile.Version != profile.Version)
            throw new InvalidOperationException("Director changed the selected MontageProfile.");
        brief = brief with
        {
            ScopePolicy = brief.ScopePolicy ?? profile.DefaultScopePolicy ?? EditScopePolicy.General
        };
        if (profileUsesExactNamedSections)
        {
            var explicitTargets = !request.TargetSourceIds.IsDefaultOrEmpty;
            if (!explicitTargets && brief.TargetSourceIds.IsDefaultOrEmpty &&
                sequence.MediaClips.Where(item => item.Video is not null)
                    .Select(item => item.SourceId).Distinct().Count() > 1)
                throw new InvalidOperationException(
                    "ИИ не смог однозначно определить, какие серии указаны. Уточните запрос или выберите серии явно.");
            if (!explicitTargets && brief.TargetSelectionConfidence < 0.65)
                throw new InvalidOperationException(
                    "ИИ недостаточно уверен в выборе серий. Уточните запрос или выберите серии явно.");
            var effectiveTargets = explicitTargets ? request.TargetSourceIds : brief.TargetSourceIds;
            indexes = SelectExactEpisodeIndexes(sequence, indexes, effectiveTargets);
            brief = brief with
            {
                TargetSourceIds = indexes.Indexes.Select(item => item.SourceId).ToImmutableArray(),
                TargetSelectionConfidence = explicitTargets ? 1 : brief.TargetSelectionConfidence,
                TargetSelectionRationale = explicitTargets
                    ? "Пользователь явно выбрал область в интерфейсе."
                    : brief.TargetSelectionRationale
            };
        }
        Record(taskId, "stage_duration_ms", Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds,
            ("stage", EditorialPipelineStage.Directing.ToString()));

        stageStarted = Stopwatch.GetTimestamp();
        var roughCut = await BuildValidatedRoughCutAsync(
            project, indexes, brief, taskId, sequence, progress, cancellationToken).ConfigureAwait(false);
        var graph = roughCut.Graph;
        indexes = roughCut.Indexes;
        Record(taskId, "stage_duration_ms", Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds,
            ("stage", EditorialPipelineStage.RoughCut.ToString()));

        var patches = ImmutableArray.CreateBuilder<DraftPatch>();
        var refinementOrder = 0;
        stageStarted = Stopwatch.GetTimestamp();
        var mutationPasses = brief.ScopePolicy?.Preservation == PreservationPolicy.ExactComplement
            ? ImmutableArray<EditorialPassKind>.Empty
            : profile.Passes.Where(item => item != EditorialPassKind.QualityControl).ToImmutableArray();
        foreach (var pass in mutationPasses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, EditorialPipelineStage.BoundaryRefining,
                refinementOrder / (double)Math.Max(1, profile.Passes.Length),
                $"Монтажный проход: {pass}.");
            var workingSet = await _retriever.RetrieveAsync(
                indexes, brief, cancellationToken: cancellationToken).ConfigureAwait(false);
            var patch = await _director.RefineAsync(
                pass, brief, graph, workingSet, refinementOrder++, cancellationToken).ConfigureAwait(false);
            if (patch.MontageGraphId != graph.Id || patch.Pass != pass)
                throw new InvalidOperationException("Director returned a refinement patch for another graph or pass.");
            var supportedDecisions = patch.Decisions
                .Where(decision => IsSupportedByMeasuredCapabilities(decision, indexes))
                .ToImmutableArray();
            var degraded = patch.Decisions.Length - supportedDecisions.Length;
            if (degraded > 0)
                Record(taskId, "capability_degraded_decision_count", degraded,
                    ("pass", pass.ToString()));
            patch = patch with { Decisions = supportedDecisions };
            patches.Add(patch);

            var refinedGraph = _targetResolver.Resolve(project, ApplyPatches(graph, patches.ToImmutable()));
            var validation = _validator.Validate(project, indexes, refinedGraph);
            if (!validation.Errors.IsDefaultOrEmpty)
                throw new InvalidOperationException("Refinement produced an invalid graph: " + string.Join("; ", validation.Errors));
            if (!validation.Gaps.IsDefaultOrEmpty)
            {
                var before = indexes.EvidenceFingerprint();
                indexes = await _gapResolver.ResolveAsync(
                    project, indexes, validation.Gaps, progress, cancellationToken).ConfigureAwait(false);
                if (before.Equals(indexes.EvidenceFingerprint(), StringComparison.Ordinal))
                    throw new EvidenceResolutionException(validation.Gaps, "Evidence resolver made no measurable progress.");
                validation = _validator.Validate(project, indexes, refinedGraph);
                if (!validation.IsValid)
                    throw new EvidenceResolutionException(validation.Gaps, "Refinement still has unresolved evidence gaps.");
            }
            graph = refinedGraph;
        }
        Record(taskId, "stage_duration_ms", Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds,
            ("stage", EditorialPipelineStage.BoundaryRefining.ToString()));

        ImmutableArray<DraftQualityIssue> criticIssues;
        if (IsExactNamedSectionRemoval(brief))
        {
            criticIssues = [];
            Record(taskId, "exact_complement_deterministic_review", 1);
        }
        else
        {
            Report(progress, EditorialPipelineStage.BoundaryRefining, 1, "Готовлю независимый контекст критика.");
            var criticSet = await _retriever.RetrieveAsync(
                indexes, brief, cancellationToken: cancellationToken).ConfigureAwait(false);
            RecordWorkingSet(taskId, criticSet, "critic");
            criticIssues = await _critic.ReviewGraphAsync(
                brief, graph, criticSet, cancellationToken).ConfigureAwait(false);
        }

        Report(progress, EditorialPipelineStage.Compiling, 0, "Компилирую семантический граф в типизированные команды.");
        var compilation = _compiler.Compile(project, indexes, graph, patches.ToImmutable());
        Report(progress, EditorialPipelineStage.Verifying, 0, "Проверяю Agent Draft детерминированно.");
        var quality = _qualityAnalyzer.Analyze(
            project, taskId, compilation.Draft.Sequence, graph, criticIssues);
        Record(taskId, "quality_blocking_issue_count",
            quality.Issues.Count(item => item.IsBlocking));
        Record(taskId, "pipeline_duration_ms",
            Stopwatch.GetElapsedTime(pipelineStarted).TotalMilliseconds);
        Report(progress, EditorialPipelineStage.ReviewingDraft, 1, "Agent Draft готов к A/B-просмотру.");
        return new EditorialPipelineResult(
            profile, indexes, brief, graph, patches.ToImmutable(), compilation, quality);
    }

    private async Task<ValidatedRoughCut> BuildValidatedRoughCutAsync(
        ProjectState project,
        MediaUnderstandingCatalog indexes,
        EditorialBrief brief,
        Guid taskId,
        SequenceState sourceSequence,
        IProgress<EditorialPipelineProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (IsExactNamedSectionRemoval(brief))
        {
            var exactGraph = BuildExactNamedSectionGraph(
                indexes, brief, taskId, sourceSequence.Id, sourceSequence.Revision);
            if (exactGraph is null)
                throw new EvidenceResolutionException(
                    [],
                    "Exact named-section removal requires independently measured opening and ending boundaries.");

            exactGraph = _targetResolver.Resolve(project, exactGraph);
            var exactValidation = _validator.Validate(project, indexes, exactGraph);
            if (exactValidation.IsValid)
            {
                Record(taskId, "exact_complement_direct_graph", 1);
                return new ValidatedRoughCut(exactGraph, indexes);
            }

            if (!exactValidation.Gaps.IsDefaultOrEmpty)
            {
                var before = indexes.EvidenceFingerprint();
                indexes = await _gapResolver.ResolveAsync(
                    project, indexes, exactValidation.Gaps, progress, cancellationToken).ConfigureAwait(false);
                if (!before.Equals(indexes.EvidenceFingerprint(), StringComparison.Ordinal))
                {
                    exactGraph = BuildExactNamedSectionGraph(
                        indexes, brief, taskId, sourceSequence.Id, sourceSequence.Revision);
                    if (exactGraph is not null)
                    {
                        exactGraph = _targetResolver.Resolve(project, exactGraph);
                        exactValidation = _validator.Validate(project, indexes, exactGraph);
                        if (exactValidation.IsValid)
                        {
                            Record(taskId, "exact_complement_direct_graph", 1);
                            return new ValidatedRoughCut(exactGraph, indexes);
                        }
                    }
                }
            }

            throw new EvidenceResolutionException(
                exactValidation.Gaps,
                "Exact opening/ending boundaries require user confirmation before Draft creation.");
        }

        MontageGraph? previousRejected = null;
        var gaps = ImmutableArray<EvidenceGap>.Empty;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, EditorialPipelineStage.Retrieving, (attempt - 1) / 5d,
                "Извлекаю релевантные сцены и локальные факты.");
            var workingSet = await _retriever.RetrieveAsync(
                indexes, brief, gaps, cancellationToken).ConfigureAwait(false);
            RecordWorkingSet(taskId, workingSet, "rough_cut");
            Record(taskId, "rough_cut_attempt", attempt);
            Report(progress, EditorialPipelineStage.RoughCut, (attempt - 1) / 5d,
                "Собираю семантический rough cut.");
            var graph = await _director.CreateRoughCutAsync(
                brief,
                workingSet,
                taskId,
                sourceSequence.Id,
                sourceSequence.Revision,
                previousRejected,
                cancellationToken).ConfigureAwait(false);
            graph = _targetResolver.Resolve(project, graph);
            if (graph.TaskId != taskId || graph.SourceSequenceId != sourceSequence.Id ||
                graph.SourceSequenceRevision != sourceSequence.Revision)
                throw new InvalidOperationException("Director returned a graph for another task or source revision.");
            if (!_rejectedGraphs.CanSubmit(graph, indexes))
                throw new RepeatedMontageGraphException(graph.Fingerprint());

            var validation = _validator.Validate(project, indexes, graph);
            if (validation.IsValid) return new ValidatedRoughCut(graph, indexes);
            Record(taskId, "invalid_graph", 1,
                ("error_count", validation.Errors.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("gap_count", validation.Gaps.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            _rejectedGraphs.Reject(graph, indexes);
            previousRejected = graph;
            if (!validation.Errors.IsDefaultOrEmpty && validation.Gaps.IsDefaultOrEmpty)
            {
                gaps = [];
                continue;
            }
            gaps = validation.Gaps;
            var before = indexes.EvidenceFingerprint();
            indexes = await _gapResolver.ResolveAsync(
                project, indexes, gaps, progress, cancellationToken).ConfigureAwait(false);
            if (before.Equals(indexes.EvidenceFingerprint(), StringComparison.Ordinal))
                throw new EvidenceResolutionException(gaps, "Evidence resolver made no measurable progress.");
        }
        throw new InvalidOperationException("Director could not produce a valid MontageGraph in five bounded attempts.");
    }

    private static bool IsExactNamedSectionRemoval(EditorialBrief brief)
    {
        var scope = brief.ScopePolicy ?? brief.Profile.DefaultScopePolicy;
        return scope is
        {
            Intent: EditorialIntentKind.RemoveNamedSections,
            Preservation: PreservationPolicy.ExactComplement
        };
    }

    private static EditorialBrief ConstrainExactNamedSectionBrief(
        EditorialBrief directed,
        string userRequest,
        MontageProfile profile)
        => directed with
        {
            Goal = string.IsNullOrWhiteSpace(directed.Goal) ? userRequest : directed.Goal,
            Audience = string.IsNullOrWhiteSpace(directed.Audience) ? "viewer" : directed.Audience,
            Profile = profile,
            Style = string.IsNullOrWhiteSpace(directed.Style)
                ? "exact anime episode complement"
                : directed.Style,
            ProtectedContent = directed.ProtectedContent
                .Concat(["Episode body", "Post-credit scene", "Preview", "All audio and subtitle streams"])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray(),
            AcceptanceCriteria = directed.AcceptanceCriteria
                .Concat([
                "Remove exactly one Opening and one Ending from every selected episode.",
                "Preserve post-credit and preview content.",
                "Keep video, audio, and subtitles synchronized."
                ])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray(),
            ScopePolicy = profile.DefaultScopePolicy ?? EditScopePolicy.AnimeOpeningEndingOnly
        };

    private static MontageGraph? BuildExactNamedSectionGraph(
        MediaUnderstandingCatalog indexes,
        EditorialBrief brief,
        Guid taskId,
        Guid sourceSequenceId,
        long sourceSequenceRevision)
    {
        var scope = brief.ScopePolicy ?? brief.Profile.DefaultScopePolicy;
        if (scope is null || scope.RemovableSegmentRoles.IsDefaultOrEmpty)
            return null;

        var facts = indexes.Indexes
            .SelectMany(index => index.Facts)
            .ToDictionary(fact => fact.Id);
        var orderedIndexes = indexes.Indexes.OrderBy(item => item.SourceId).ToArray();
        if (orderedIndexes.Length == 0) return null;
        var decisions = ImmutableArray.CreateBuilder<EditDecision>(
            scope.RemovableSegmentRoles.Length * orderedIndexes.Length);
        foreach (var index in orderedIndexes)
        {
            foreach (var role in scope.RemovableSegmentRoles)
            {
                var hypothesis = index.SegmentRoleHypotheses
                    .Where(item =>
                        item.Role == role &&
                        item.Confidence >= 0.50 &&
                        item.StartBoundary.Confidence >= 0.50 &&
                        item.EndBoundary.Confidence >= 0.50 &&
                        item.EvidenceChannels.Distinct().Count() >= 2)
                    .Where(item => item.EvidenceFactIds
                        .Where(facts.ContainsKey)
                        .Select(id => facts[id].Channel)
                        .Distinct()
                        .Count() >= 2)
                    .OrderByDescending(item => Math.Min(
                        item.Confidence,
                        Math.Min(item.StartBoundary.Confidence, item.EndBoundary.Confidence)))
                    .ThenBy(item => item.SourceRange.Start)
                    .FirstOrDefault();
                if (hypothesis is null)
                    return null;

                var confidence = Math.Min(
                    hypothesis.Confidence,
                    Math.Min(hypothesis.StartBoundary.Confidence, hypothesis.EndBoundary.Confidence));
                decisions.Add(new EditDecision(
                    Guid.NewGuid(),
                    EditDecisionKind.Remove,
                    hypothesis.SourceId,
                    hypothesis.SourceRange,
                    decisions.Count,
                    $"Remove the independently measured {role} segment from the selected episode and preserve the exact complement.",
                    confidence,
                    hypothesis.EvidenceFactIds.Distinct().ToImmutableArray(),
                    ImmutableDictionary<string, string>.Empty,
                    SegmentRole: role));
            }
        }

        var now = DateTimeOffset.UtcNow;
        return new MontageGraph(
            Guid.NewGuid(),
            taskId,
            sourceSequenceId,
            sourceSequenceRevision,
            brief,
            decisions.ToImmutable(),
            1,
            now,
            now);
    }

    private static MediaUnderstandingCatalog SelectExactEpisodeIndexes(
        SequenceState sequence,
        MediaUnderstandingCatalog indexes,
        ImmutableArray<Guid> requestedSourceIds)
    {
        var episodeSourceIds = sequence.MediaClips
            .Where(item => item.Video is not null)
            .Select(item => item.SourceId)
            .Distinct()
            .ToImmutableHashSet();
        if (episodeSourceIds.Count == 0)
            throw new InvalidOperationException("Активный таймлайн не содержит видеосерий.");
        var selected = requestedSourceIds.IsDefaultOrEmpty
            ? episodeSourceIds.Count == 1
                ? episodeSourceIds
                : throw new InvalidOperationException(
                    "Не выбраны серии для безопасного удаления OP/ED.")
            : requestedSourceIds.ToImmutableHashSet();
        var outside = selected.Where(id => !episodeSourceIds.Contains(id)).ToArray();
        if (outside.Length > 0)
            throw new InvalidOperationException("Выбранная серия больше не находится на исходном таймлайне.");
        var selectedIndexes = indexes.Indexes
            .Where(item => selected.Contains(item.SourceId))
            .OrderBy(item => item.SourceId)
            .ToImmutableArray();
        if (selectedIndexes.Length != selected.Count)
            throw new InvalidOperationException("Не для всех выбранных серий построен мультимодальный индекс.");
        return indexes with { Indexes = selectedIndexes };
    }

    private static MontageGraph ApplyPatches(MontageGraph graph, ImmutableArray<DraftPatch> patches)
    {
        var decisions = graph.Decisions.ToDictionary(item => item.Id);
        foreach (var decision in patches.OrderBy(item => item.Order).SelectMany(item => item.Decisions))
            decisions[decision.Id] = decision;
        return graph with
        {
            Decisions = decisions.Values.OrderBy(item => item.Order).ThenBy(item => item.Id).ToImmutableArray(),
            Revision = graph.Revision + 1,
            UpdatedAt = DateTimeOffset.UtcNow,
            ParentFingerprint = graph.Fingerprint()
        };
    }

    private static void EnsureIndexCoverage(
        ProjectState project,
        SequenceState sequence,
        MediaUnderstandingCatalog indexes)
    {
        var sourceIds = sequence.MediaClips.Select(item => item.SourceId).Distinct().ToArray();
        var missing = sourceIds.Where(id => indexes.Find(id) is null).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException("Indexer omitted media used by the source sequence: " + string.Join(", ", missing));
        foreach (var index in indexes.Indexes)
        {
            if (!project.Sources.TryGetValue(index.SourceId, out var source))
                throw new InvalidOperationException("Indexer returned an unknown source.");
            var stable = MediaSourceFingerprint.Stable(source);
            if (!index.SourceFingerprint.Equals(stable, StringComparison.Ordinal))
                throw new InvalidOperationException($"Indexer returned stale analysis for '{source.Name}'.");
        }
    }

    private static void Report(
        IProgress<EditorialPipelineProgress>? progress,
        EditorialPipelineStage stage,
        double value,
        string message)
        => progress?.Report(new EditorialPipelineProgress(stage, Math.Clamp(value, 0, 1), message));

    private void RecordCoverage(Guid taskId, MediaUnderstandingCatalog indexes)
    {
        foreach (var index in indexes.Indexes)
        foreach (var channel in index.Coverage.Channels)
            Record(taskId, "coverage_measured_seconds", UnionDurationSeconds(channel.Value),
                ("source_id", index.SourceId.ToString("N")),
                ("channel", channel.Key.ToString()));
    }

    private void RecordWorkingSet(Guid taskId, EditorialWorkingSet set, string role)
    {
        Record(taskId, "retrieval_node_count", set.NodeCount, ("role", role));
        Record(taskId, "retrieval_fact_count", set.Facts.Length, ("role", role));
        Record(taskId, "retrieval_hypothesis_count", set.Hypotheses.Length, ("role", role));
        Record(taskId, "retrieval_counterevidence_count", set.CounterEvidence.Length, ("role", role));
    }

    private void Record(Guid taskId, string metric, double value, params (string Key, string Value)[] dimensions)
    {
        try
        {
            _telemetry.Record(new EditorialTelemetryEvent(
                DateTimeOffset.UtcNow,
                taskId,
                metric,
                value,
                dimensions.ToImmutableDictionary(item => item.Key, item => item.Value)));
        }
        catch
        {
            // Local metrics are diagnostic only and must never fail an edit.
        }
    }

    private static double UnionDurationSeconds(ImmutableArray<CoverageInterval> intervals)
    {
        var ordered = (intervals.IsDefault ? [] : intervals)
            .OrderBy(item => item.Range.Start)
            .ToArray();
        if (ordered.Length == 0) return 0;
        var totalTicks = 0L;
        var start = ordered[0].Range.Start;
        var end = ordered[0].Range.End;
        foreach (var interval in ordered.Skip(1))
        {
            if (interval.Range.Start <= end)
            {
                if (interval.Range.End > end) end = interval.Range.End;
                continue;
            }
            totalTicks += (end - start).Ticks;
            start = interval.Range.Start;
            end = interval.Range.End;
        }
        totalTicks += (end - start).Ticks;
        return new TimelineTime(totalTicks).TotalSeconds;
    }

    private static bool IsSupportedByMeasuredCapabilities(
        EditDecision decision,
        MediaUnderstandingCatalog indexes)
    {
        var index = indexes.Find(decision.SourceId);
        if (index is null) return false;
        bool Has(CoverageChannel channel) =>
            index.Coverage.Channels.TryGetValue(channel, out var intervals) && !intervals.IsDefaultOrEmpty;
        return decision.Kind switch
        {
            EditDecisionKind.ApplyDialogueCut or EditDecisionKind.ApplyJlCut =>
                Has(CoverageChannel.Audio) && Has(CoverageChannel.Transcript) && !index.TranscriptWords.IsDefaultOrEmpty,
            EditDecisionKind.ApplyAudioMix => Has(CoverageChannel.Audio),
            EditDecisionKind.ApplyCaptionTrack => Has(CoverageChannel.Transcript),
            EditDecisionKind.AutoReframe => Has(CoverageChannel.Frames),
            EditDecisionKind.Retime => Has(CoverageChannel.Motion),
            _ => true
        };
    }

    private sealed record ValidatedRoughCut(
        MontageGraph Graph,
        MediaUnderstandingCatalog Indexes);
}

public sealed class RepeatedMontageGraphException(string fingerprint)
    : InvalidOperationException($"Rejected MontageGraph '{fingerprint}' was repeated without new evidence.")
{
    public string Fingerprint { get; } = fingerprint;
}

public sealed class EvidenceResolutionException(
    ImmutableArray<EvidenceGap> gaps,
    string message) : InvalidOperationException(message)
{
    public ImmutableArray<EvidenceGap> Gaps { get; } = gaps.IsDefault ? [] : gaps;
}
