using System.Collections.Immutable;
using KadrStudio.Application.Automation.Editorial;
using KadrStudio.Core.Domain;

namespace KadrStudio.Core.Tests;

public sealed class EditorialPipelineTests
{
    [Fact]
    public void ValidatorReturnsTypedExactEvidenceGap()
    {
        var fixture = CreateFixture(withCoverage: false);
        var result = new MontageGraphValidatorV2().Validate(
            fixture.Project, fixture.Catalog, fixture.Graph);

        Assert.False(result.IsValid);
        var gap = Assert.Single(result.Gaps, item => item.Channel == CoverageChannel.Frames);
        Assert.Equal(fixture.Source.Id, gap.SourceId);
        Assert.Equal(fixture.Range, gap.SourceRange);
        Assert.Equal("video-understanding", gap.RecommendedAnalyzer);
        Assert.True(gap.MinimumDensityHz > 0);
    }

    [Fact]
    public void SemanticHypothesisCannotAuthorizeEditorialDecisionWithoutMeasuredFact()
    {
        var fixture = CreateFixture();
        var hypothesis = new SemanticHypothesis(
            Guid.NewGuid(), fixture.Source.Id, fixture.Range,
            "This might be an opening.", 0.8, [fixture.Index.Facts[0].Id],
            "embedding", "2");
        var index = fixture.Index with { SemanticHypotheses = [hypothesis] };
        var graph = fixture.Graph with
        {
            Decisions = [fixture.Graph.Decisions[0] with { EvidenceFactIds = [hypothesis.Id] }]
        };

        var validation = new MontageGraphValidatorV2().Validate(
            fixture.Project,
            new MediaUnderstandingCatalog([index]),
            graph);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("missing facts", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectedFingerprintRequiresNewMeasuredEvidence()
    {
        var fixture = CreateFixture();
        var ledger = new RejectedMontageGraphLedger();
        ledger.Reject(fixture.Graph, fixture.Catalog);

        Assert.False(ledger.CanSubmit(fixture.Graph, fixture.Catalog));

        var index = fixture.Index with
        {
            Facts = fixture.Index.Facts.Add(new TemporalFact(
                Guid.NewGuid(), fixture.Source.Id, fixture.Range,
                CoverageChannel.Frames, TemporalFactKind.VisualDescription,
                "new independent probe", 0.9, "video-understanding", "2",
                ImmutableDictionary<string, string>.Empty)),
            UpdatedAt = fixture.Index.UpdatedAt.AddSeconds(1)
        };
        Assert.True(ledger.CanSubmit(
            fixture.Graph,
            new MediaUnderstandingCatalog([index])));
    }

    [Fact]
    public void CompilerBuildsSeparateDraftAndPreservesSourceSequence()
    {
        var fixture = CreateFixture();
        var original = fixture.Project.FindSequence(fixture.Graph.SourceSequenceId)!;

        var result = new MontageGraphCompilerV2().Compile(
            fixture.Project, fixture.Catalog, fixture.Graph, []);

        Assert.Equal(SequenceStatus.Draft, result.Draft.Sequence.Status);
        Assert.Equal(original.Id, result.Draft.Sequence.ParentSequenceId);
        Assert.NotEqual(original.Id, result.Draft.Sequence.Id);
        Assert.Equal(original.MediaClips, fixture.Project.FindSequence(original.Id)!.MediaClips);
        Assert.NotEmpty(result.Draft.Sequence.MediaClips);
    }

    [Fact]
    public void QualityControlBlocksSemanticOperationsThatWereNotApplied()
    {
        var fixture = CreateFixture();
        var retime = fixture.Graph.Decisions[0] with
        {
            Id = Guid.NewGuid(),
            Kind = EditDecisionKind.Retime,
            Order = 1
        };
        var graph = fixture.Graph with { Decisions = fixture.Graph.Decisions.Add(retime) };
        var compilation = new MontageGraphCompilerV2().Compile(
            fixture.Project, fixture.Catalog, graph, []);

        var report = new DeterministicDraftQualityAnalyzer().Analyze(
            fixture.Project,
            graph.TaskId,
            compilation.Draft.Sequence,
            graph,
            []);

        Assert.Equal(DraftQualityStatus.Failed, report.Status);
        Assert.Contains(report.Issues, issue => issue.Code == "draft.capability_unavailable" && issue.IsBlocking);
    }

    [Fact]
    public void AnimeExactComplementRemovesOnlyOpeningAndEndingAcrossEveryStream()
    {
        var fixture = CreateAnimeFixture();
        var resolved = new MontageGraphTargetResolver().Resolve(fixture.Project, fixture.Graph);
        var validation = new MontageGraphValidatorV2().Validate(fixture.Project, fixture.Catalog, resolved);

        Assert.True(validation.IsValid, string.Join("; ", validation.Errors.Concat(validation.Gaps.Select(item => item.Message))));
        Assert.Equal(2, resolved.Decisions.Length);
        Assert.All(resolved.Decisions, decision =>
        {
            Assert.Equal(EditDecisionKind.Remove, decision.Kind);
            Assert.NotNull(decision.Target);
            Assert.Equal(5, decision.Target!.TimelineObjectIds.Length);
        });

        var sourceSequence = fixture.Project.ActiveSequence!;
        var compilation = new MontageGraphCompilerV2().Compile(fixture.Project, fixture.Catalog, resolved, []);
        var draft = compilation.Draft.Sequence;
        Assert.Equal(2, compilation.Receipts.Length);
        Assert.Equal(2, compilation.RemovedTimelineRanges.Length);
        Assert.Equal(1245.144, draft.Duration.TotalSeconds, 3);
        Assert.Equal(sourceSequence.MediaClips, fixture.Project.ActiveSequence!.MediaClips);
        Assert.Equal(sourceSequence.SubtitleClips, fixture.Project.ActiveSequence!.SubtitleClips);
        Assert.Equal(6, draft.MediaClips.Count(item => item.Audio is not null));
        Assert.Equal([1, 2], draft.MediaClips.Where(item => item.Audio is not null)
            .Select(item => item.StreamIndex!.Value).Distinct().Order().ToArray());
        Assert.Equal(6, draft.SubtitleClips.Length);
        Assert.Equal([3, 4], draft.SubtitleClips.Select(item => item.StreamIndex).Distinct().Order().ToArray());
        Assert.All(draft.SubtitleClips, item => Assert.True(item.PreserveAssStyling));
        Assert.Contains(draft.MediaClips, item => item.SourceIn == TimelineTime.FromSeconds(1380));
        Assert.Contains(draft.Markers, item => item.Kind == MarkerKind.PostCredits);
        Assert.Contains(draft.Markers, item => item.Kind == MarkerKind.Preview);
        var transition = Assert.Single(draft.Transitions);
        Assert.Equal(TimelineTime.FromSeconds(1217), transition.Start);
        Assert.Contains(draft.MediaClips, item => item.Id == transition.FromClipId && item.End == TimelineTime.FromSeconds(1218));

        var quality = new DeterministicDraftQualityAnalyzer().Analyze(
            fixture.Project, resolved.TaskId, draft, resolved, []);
        Assert.Equal(DraftQualityStatus.Passed, quality.Status);
        Assert.DoesNotContain(quality.Issues, item => item.IsBlocking);

        var unlinked = draft with
        {
            MediaClips = draft.MediaClips.Select(item => item with { LinkGroupId = null }).ToImmutableArray(),
            SubtitleClips = draft.SubtitleClips.Select(item => item with { LinkGroupId = null }).ToImmutableArray()
        };
        var brokenLinks = new DeterministicDraftQualityAnalyzer().Analyze(
            fixture.Project, resolved.TaskId, unlinked, resolved, []);
        Assert.Equal(DraftQualityStatus.Failed, brokenLinks.Status);
        Assert.Contains(brokenLinks.Issues, item => item.Code == "exact.links" && item.IsBlocking);
    }

    [Fact]
    public async Task AnimeExactComplementBuildsDraftFromMultimodalBoundariesWithoutPlannerRoughCut()
    {
        var fixture = CreateAnimeFixture();
        var reasoner = new FakeReasoner(fixture.Graph.Decisions);
        var indexer = new FakeIndexer(fixture.Catalog);
        var pipeline = new EditorialPipeline(indexer, reasoner, reasoner, indexer);

        var result = await pipeline.RunAsync(new EditorialPipelineRequest(
            fixture.Project,
            fixture.Graph.SourceSequenceId,
            "удали опенинг и эндинг",
            MontageProfileCatalog.Get(MontageProfileKind.AnimeEpisode),
            TaskId: fixture.Graph.TaskId));

        Assert.Equal(0, reasoner.RoughCutCalls);
        Assert.Equal(0, reasoner.CriticCalls);
        Assert.Equal(1, reasoner.BriefCalls);
        Assert.Equal([SegmentRole.Opening, SegmentRole.Ending],
            result.Graph.Decisions.OrderBy(item => item.Order).Select(item => item.SegmentRole).ToArray());
        Assert.Equal(1245.144, result.Compilation.Draft.Sequence.Duration.TotalSeconds, 3);
        Assert.Equal(DraftQualityStatus.Passed, result.QualityReport.Status);
    }

    [Fact]
    public async Task AnimeBatchRemovesOneOpeningAndEndingFromEverySelectedEpisode()
    {
        var fixture = CreateMultiEpisodeFixture();
        var reasoner = new FakeReasoner([]);
        var indexer = new FakeIndexer(fixture.Catalog);
        var pipeline = new EditorialPipeline(indexer, reasoner, reasoner, indexer);

        var result = await pipeline.RunAsync(new EditorialPipelineRequest(
            fixture.Project,
            fixture.Project.ActiveSequence!.Id,
            "удали опенинг и эндинг на всех сериях",
            MontageProfileCatalog.Get(MontageProfileKind.AnimeEpisode),
            TaskId: Guid.NewGuid(),
            TargetSourceIds: [fixture.FirstSourceId, fixture.SecondSourceId]));

        Assert.Equal(4, result.Graph.Decisions.Length);
        Assert.All(result.Graph.Decisions.GroupBy(item => item.SourceId), group =>
            Assert.Equal([SegmentRole.Opening, SegmentRole.Ending],
                group.Select(item => item.SegmentRole).Distinct().Order().ToArray()));
        Assert.Equal(1080, result.Compilation.Draft.Sequence.Duration.TotalSeconds, 3);
        Assert.Equal(DraftQualityStatus.Passed, result.QualityReport.Status);
    }

    [Fact]
    public async Task AnimeBatchCanTargetOneEpisodeAndRefusesAnIncompleteAllEpisodesDraft()
    {
        var fixture = CreateMultiEpisodeFixture();
        var reasoner = new FakeReasoner([]);
        var indexer = new FakeIndexer(fixture.Catalog);
        var pipeline = new EditorialPipeline(indexer, reasoner, reasoner, indexer);

        var selected = await pipeline.RunAsync(new EditorialPipelineRequest(
            fixture.Project,
            fixture.Project.ActiveSequence!.Id,
            "удали опенинг и эндинг только во второй серии",
            MontageProfileCatalog.Get(MontageProfileKind.AnimeEpisode),
            TaskId: Guid.NewGuid(),
            TargetSourceIds: [fixture.SecondSourceId]));

        Assert.Equal(2, selected.Graph.Decisions.Length);
        Assert.All(selected.Graph.Decisions, item => Assert.Equal(fixture.SecondSourceId, item.SourceId));
        Assert.Equal(1140, selected.Compilation.Draft.Sequence.Duration.TotalSeconds, 3);

        var incompleteSecond = fixture.Catalog.Find(fixture.SecondSourceId)! with
        {
            SegmentRoleHypotheses = fixture.Catalog.Find(fixture.SecondSourceId)!.SegmentRoleHypotheses
                .Where(item => item.Role != SegmentRole.Ending)
                .ToImmutableArray()
        };
        var incompleteCatalog = fixture.Catalog with
        {
            Indexes = fixture.Catalog.Indexes
                .Select(item => item.SourceId == fixture.SecondSourceId ? incompleteSecond : item)
                .ToImmutableArray()
        };
        var incompleteIndexer = new FakeIndexer(incompleteCatalog);
        var incompletePipeline = new EditorialPipeline(incompleteIndexer, reasoner, reasoner, incompleteIndexer);
        await Assert.ThrowsAsync<EvidenceResolutionException>(() => incompletePipeline.RunAsync(
            new EditorialPipelineRequest(
                fixture.Project,
                fixture.Project.ActiveSequence!.Id,
                "удали на всех",
                MontageProfileCatalog.Get(MontageProfileKind.AnimeEpisode),
                TaskId: Guid.NewGuid(),
                TargetSourceIds: [fixture.FirstSourceId, fixture.SecondSourceId])));
    }

    [Fact]
    public async Task DirectorSemanticTargetSelectionControlsEpisodesAndAmbiguityIsRejected()
    {
        var fixture = CreateMultiEpisodeFixture();
        var selectedReasoner = new FakeReasoner([], selectedTargets: [fixture.SecondSourceId]);
        var indexer = new FakeIndexer(fixture.Catalog);
        var selectedPipeline = new EditorialPipeline(indexer, selectedReasoner, selectedReasoner, indexer);

        var selected = await selectedPipeline.RunAsync(new EditorialPipelineRequest(
            fixture.Project,
            fixture.Project.ActiveSequence!.Id,
            "сделай это с той серией, о которой я говорю в запросе",
            MontageProfileCatalog.Get(MontageProfileKind.AnimeEpisode),
            TaskId: Guid.NewGuid()));

        Assert.Equal(fixture.SecondSourceId, Assert.Single(selected.Brief.TargetSourceIds));
        Assert.All(selected.Graph.Decisions, item => Assert.Equal(fixture.SecondSourceId, item.SourceId));

        var ambiguousReasoner = new FakeReasoner([], selectedTargets: [], targetConfidence: 0.2);
        var ambiguousPipeline = new EditorialPipeline(indexer, ambiguousReasoner, ambiguousReasoner, indexer);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ambiguousPipeline.RunAsync(
            new EditorialPipelineRequest(
                fixture.Project,
                fixture.Project.ActiveSequence!.Id,
                "неоднозначный запрос",
                MontageProfileCatalog.Get(MontageProfileKind.AnimeEpisode),
                TaskId: Guid.NewGuid())));
        Assert.Contains("однозначно определить", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MultimodalCandidateBoundaryIsValidWithoutSyntheticShotBoundaryFact()
    {
        var fixture = CreateAnimeFixture();
        var index = fixture.Index with
        {
            Facts = fixture.Index.Facts
                .Select(item => item.Kind == TemporalFactKind.ShotBoundary
                    ? item with { Kind = TemporalFactKind.VisualDescription }
                    : item)
                .ToImmutableArray()
        };
        var resolved = new MontageGraphTargetResolver().Resolve(fixture.Project, fixture.Graph);

        var validation = new MontageGraphValidatorV2().Validate(
            fixture.Project,
            new MediaUnderstandingCatalog([index]),
            resolved);

        Assert.True(validation.IsValid,
            string.Join("; ", validation.Errors.Concat(validation.Gaps.Select(item => item.Message))));
    }

    [Fact]
    public async Task PipelineRunsIndependentPassesAndStopsAtReviewableDraft()
    {
        var fixture = CreateFixture();
        var reasoner = new FakeReasoner(fixture.Graph.Decisions);
        var indexer = new FakeIndexer(fixture.Catalog);
        var pipeline = new EditorialPipeline(indexer, reasoner, reasoner, indexer);

        var result = await pipeline.RunAsync(new EditorialPipelineRequest(
            fixture.Project,
            fixture.Graph.SourceSequenceId,
            "Собери чистый монтаж",
            MontageProfileCatalog.Get(MontageProfileKind.Generic),
            TaskId: fixture.Graph.TaskId));

        Assert.Equal(DraftQualityStatus.Passed, result.QualityReport.Status);
        Assert.Equal(SequenceStatus.Draft, result.Compilation.Draft.Sequence.Status);
        Assert.Equal(
            MontageProfileCatalog.Get(MontageProfileKind.Generic).Passes.Count(pass => pass != EditorialPassKind.QualityControl),
            reasoner.RefinementCalls);
        Assert.Equal(1, reasoner.CriticCalls);
    }

    [Fact]
    public async Task UnavailableAudioCapabilityDegradesPassWithoutBlockingVisualDraft()
    {
        var fixture = CreateFixture();
        var reasoner = new FakeReasoner(fixture.Graph.Decisions, injectUnsupportedAudio: true);
        var indexer = new FakeIndexer(fixture.Catalog);
        var pipeline = new EditorialPipeline(indexer, reasoner, reasoner, indexer);

        var result = await pipeline.RunAsync(new EditorialPipelineRequest(
            fixture.Project,
            fixture.Graph.SourceSequenceId,
            "Собери визуальный монтаж без звука",
            MontageProfileCatalog.Get(MontageProfileKind.Generic),
            TaskId: fixture.Graph.TaskId));

        Assert.Equal(DraftQualityStatus.Passed, result.QualityReport.Status);
        Assert.DoesNotContain(result.Graph.Decisions, item => item.Kind == EditDecisionKind.ApplyAudioMix);
    }

    [Fact]
    public void ContextBudgetReservesSixtyFiveTwentyFifteenPercent()
    {
        var budget = ModelContextBudget.Create(32_000);

        Assert.Equal(20_800, budget.MaximumInputTokens);
        Assert.Equal(6_400, budget.MaximumOutputTokens);
        Assert.Equal(4_800, budget.ReserveTokens);
        Assert.Throws<ModelContextOverflowException>(() =>
            budget.EnsureFits("planner", "payload", new FixedTokenCounter(20_801)));
    }

    [Fact]
    public async Task ReferenceResearchIsDisabledByDefaultAndRequiresExplicitUserIntent()
    {
        var provider = new FakeReferenceProvider();
        var disabled = new ReferenceResearchGateway(provider);
        await Assert.ThrowsAsync<InvalidOperationException>(() => disabled.ResearchAsync(
            new ReferenceResearchRequest("film", "style", true)));

        var enabled = new ReferenceResearchGateway(provider, enabled: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => enabled.ResearchAsync(
            new ReferenceResearchRequest("film", "style", false)));

        var references = await enabled.ResearchAsync(
            new ReferenceResearchRequest("film", "style", true));
        var reference = Assert.Single(references);
        Assert.True(reference.UserRequested);
        Assert.Equal("https://example.test/reference", reference.Url);
    }

    private static Fixture CreateFixture(bool withCoverage = true)
    {
        var project = ProjectState.CreateNew();
        var visualTrack = project.Tracks.First(item => item.Kind == TrackKind.Visual);
        var source = new MediaSource(
            Guid.NewGuid(), "C:\\media\\source.mp4", "source", MediaKind.Video,
            TimelineTime.FromSeconds(10), false, 1920, 1080, FrameRate.Fps30,
            Fingerprint: "fixture-fingerprint");
        var clip = new MediaClip(
            Guid.NewGuid(), source.Id, visualTrack.Id, TimelineTime.Zero,
            TimelineTime.Zero, source.Duration, Video: new VideoParameters());
        project = (project with
        {
            Sources = project.Sources.Add(source.Id, source),
            MediaClips = [clip]
        }).EnsureSequenceContainer();
        var sequence = project.ActiveSequence!;
        var range = new TimeRange(TimelineTime.Zero, source.Duration);
        var fact = new TemporalFact(
            Guid.NewGuid(), source.Id, range, CoverageChannel.Frames,
            TemporalFactKind.VisualDescription, "measured scene", 0.95,
            "video-understanding", "2", ImmutableDictionary<string, string>.Empty);
        var coverage = new CoverageMap(
            source.Id, source.Fingerprint, source.Duration,
            ImmutableDictionary<CoverageChannel, ImmutableArray<CoverageInterval>>.Empty);
        if (withCoverage)
            coverage = coverage.Add(
                CoverageChannel.Frames,
                CoverageInterval.Create(range, 20, false, "video-understanding", "2"));
        var now = DateTimeOffset.UtcNow;
        var index = new MediaUnderstandingIndex(
            Guid.NewGuid(), source.Id, source.Fingerprint, "editorial-v2.0",
            source.Duration, coverage,
            [new AnalyzerManifest("video-understanding", "2", "test", "test", [CoverageChannel.Frames], now)],
            [], [], [], [], [fact], [], [], now, now);
        var profile = MontageProfileCatalog.Get(MontageProfileKind.Generic);
        var brief = new EditorialBrief(
            Guid.NewGuid(), "clean edit", "viewer", profile, source.Duration,
            "clean", [], ["source preserved"], now);
        var decision = new EditDecision(
            Guid.NewGuid(), EditDecisionKind.Keep, source.Id, range, 0,
            "Retain measured scene.", 0.95, [fact.Id],
            ImmutableDictionary<string, string>.Empty);
        var graph = new MontageGraph(
            Guid.NewGuid(), Guid.NewGuid(), sequence.Id, sequence.Revision,
            brief, [decision], 1, now, now);
        return new Fixture(project, source, range, index, new MediaUnderstandingCatalog([index]), graph);
    }

    private static Fixture CreateAnimeFixture()
    {
        var project = ProjectState.CreateNew();
        var duration = TimelineTime.FromSeconds(1427.144);
        var source = new MediaSource(
            Guid.NewGuid(), "C:\\media\\anime.mkv", "anime", MediaKind.Video,
            duration, true, 1920, 1080, FrameRate.Fps23976,
            "hevc", "aac", 10_000, 1, "anime-fixture")
        {
            Streams =
            [
                new(0, MediaStreamKind.Video, "hevc", "yuv420p10le", 1920, 1080, FrameRate: FrameRate.Fps23976),
                new(1, MediaStreamKind.Audio, "aac", SampleRate: 48_000, Channels: 2, Language: "rus", Title: "AniLibria", IsDefault: true),
                new(2, MediaStreamKind.Audio, "aac", SampleRate: 48_000, Channels: 2, Language: "jpn"),
                new(3, MediaStreamKind.Subtitle, "ass", Language: "rus", Title: "Надписи", IsForced: true),
                new(4, MediaStreamKind.Subtitle, "ass", Language: "rus", Title: "Субтитры")
            ]
        };
        var visual = project.Tracks.First(item => item.Kind == TrackKind.Visual && item.Index == 0);
        var audio0 = project.Tracks.First(item => item.Kind == TrackKind.Audio && item.Index == 0);
        var audio1 = project.Tracks.First(item => item.Kind == TrackKind.Audio && item.Index == 1);
        var subtitle0 = project.Tracks.First(item => item.Kind == TrackKind.Subtitle && item.Index == 0);
        var subtitle1 = new TimelineTrack(Guid.NewGuid(), TrackKind.Subtitle, 1, "S2");
        var link = Guid.NewGuid();
        var firstVisual = new MediaClip(
            Guid.NewGuid(), source.Id, visual.Id, TimelineTime.Zero, TimelineTime.Zero,
            TimelineTime.FromSeconds(1400), link,
            new VideoParameters(Brightness: 0.1, Contrast: 1.1), StreamIndex: 0);
        var previewVisual = new MediaClip(
            Guid.NewGuid(), source.Id, visual.Id, TimelineTime.FromSeconds(1400),
            TimelineTime.FromSeconds(1400), TimelineTime.FromSeconds(27.144),
            Video: new VideoParameters(Brightness: 0.1, Contrast: 1.1), StreamIndex: 0);
        var media = ImmutableArray.Create(
            firstVisual,
            previewVisual,
            new MediaClip(Guid.NewGuid(), source.Id, audio0.Id, TimelineTime.Zero, TimelineTime.Zero, duration,
                link, Audio: new AudioParameters(Volume: 0.9, Pan: -0.1), StreamIndex: 1),
            new MediaClip(Guid.NewGuid(), source.Id, audio1.Id, TimelineTime.Zero, TimelineTime.Zero, duration,
                link, Audio: new AudioParameters(Volume: 0.8, Pan: 0.1), StreamIndex: 2));
        var subtitles = ImmutableArray.Create(
            new SubtitleClip(Guid.NewGuid(), source.Id, 3, subtitle0.Id, TimelineTime.Zero, TimelineTime.Zero, duration, link),
            new SubtitleClip(Guid.NewGuid(), source.Id, 4, subtitle1.Id, TimelineTime.Zero, TimelineTime.Zero, duration, link));
        project = (project with
        {
            Sources = project.Sources.Add(source.Id, source),
            Tracks = project.Tracks.Add(subtitle1),
            MediaClips = media,
            SubtitleClips = subtitles,
            Transitions =
            [
                new TimelineTransition(
                    Guid.NewGuid(), TransitionKind.CrossDissolve, visual.Id,
                    firstVisual.Id, previewVisual.Id,
                    TimelineTime.FromSeconds(1399), TimelineTime.FromSeconds(2))
            ],
            Markers =
            [
                new TimelineMarker(Guid.NewGuid(), MarkerKind.PostCredits, TimelineTime.FromSeconds(1380),
                    TimelineTime.FromSeconds(20), "Post credits", "Protected"),
                new TimelineMarker(Guid.NewGuid(), MarkerKind.Preview, TimelineTime.FromSeconds(1400),
                    TimelineTime.FromSeconds(27.144), "Preview", "Protected")
            ]
        }).EnsureSequenceContainer();

        var opening = new TimeRange(TimelineTime.FromSeconds(146), TimelineTime.FromSeconds(92));
        var ending = new TimeRange(TimelineTime.FromSeconds(1290), TimelineTime.FromSeconds(90));
        var frame = FrameRate.Fps23976.FrameDuration;
        var facts = ImmutableArray.CreateBuilder<TemporalFact>();
        foreach (var (range, label) in new[] { (opening, "opening"), (ending, "ending") })
        {
            facts.Add(new TemporalFact(Guid.NewGuid(), source.Id, range, CoverageChannel.Audio,
                TemporalFactKind.AudioEvent, $"{label} theme music", 0.95, "audio-events", "2",
                ImmutableDictionary<string, string>.Empty, StreamIndex: 1));
            facts.Add(new TemporalFact(Guid.NewGuid(), source.Id, range, CoverageChannel.Ocr,
                TemporalFactKind.Ocr, $"{label} credits", 0.94, "video-understanding", "2",
                ImmutableDictionary<string, string>.Empty));
            facts.Add(new TemporalFact(Guid.NewGuid(), source.Id, new TimeRange(range.Start, frame), CoverageChannel.Frames,
                TemporalFactKind.ShotBoundary, $"{label} start", 0.99, "video-understanding", "2",
                ImmutableDictionary<string, string>.Empty));
            facts.Add(new TemporalFact(Guid.NewGuid(), source.Id, new TimeRange(range.End, frame), CoverageChannel.Frames,
                TemporalFactKind.ShotBoundary, $"{label} end", 0.99, "video-understanding", "2",
                ImmutableDictionary<string, string>.Empty));
        }
        var coverage = new CoverageMap(source.Id, source.Fingerprint, duration,
            ImmutableDictionary<CoverageChannel, ImmutableArray<CoverageInterval>>.Empty)
            .Add(CoverageChannel.Frames, CoverageInterval.Create(opening, 100, false, "video-understanding", "2"))
            .Add(CoverageChannel.Frames, CoverageInterval.Create(ending, 100, false, "video-understanding", "2"));
        var now = DateTimeOffset.UtcNow;
        SegmentRoleHypothesis Hypothesis(TimeRange range, SegmentRole role, string label)
        {
            var relevant = facts.Where(item => item.SourceRange.Overlaps(range)).Select(item => item.Id).ToImmutableArray();
            return new SegmentRoleHypothesis(
                Guid.NewGuid(), source.Id, range, role, 0.93,
                new CandidateBoundary(Guid.NewGuid(), source.Id, range.Start, 0.96, relevant),
                new CandidateBoundary(Guid.NewGuid(), source.Id, range.End, 0.96, relevant),
                relevant, [CoverageChannel.Audio, CoverageChannel.Ocr, CoverageChannel.Frames],
                "video-understanding", "2");
        }
        var hypotheses = ImmutableArray.Create(
            Hypothesis(opening, SegmentRole.Opening, "opening"),
            Hypothesis(ending, SegmentRole.Ending, "ending"),
            Hypothesis(new TimeRange(TimelineTime.FromSeconds(1380), TimelineTime.FromSeconds(20)), SegmentRole.PostCredits, "post"),
            Hypothesis(new TimeRange(TimelineTime.FromSeconds(1400), TimelineTime.FromSeconds(27.144)), SegmentRole.Preview, "preview"));
        var index = new MediaUnderstandingIndex(
            Guid.NewGuid(), source.Id, source.Fingerprint, "editorial-v2.1", duration, coverage,
            [new AnalyzerManifest("video-understanding", "2", "test", "test", [CoverageChannel.Frames], now)],
            [], [], [], [], facts.ToImmutable(), [], [], now, now, SegmentRoleHypotheses: hypotheses);
        var profile = MontageProfileCatalog.Get(MontageProfileKind.AnimeEpisode);
        var brief = new EditorialBrief(Guid.NewGuid(), "remove OP and ED only", "viewer", profile,
            duration - opening.Duration - ending.Duration, "exact", [], ["exact complement"], now,
            ScopePolicy: EditScopePolicy.AnimeOpeningEndingOnly);
        EditDecision Remove(TimeRange range, SegmentRole role, int order) => new(
            Guid.NewGuid(), EditDecisionKind.Remove, source.Id, range, order, $"Remove {role}.", 0.93,
            facts.Where(item => item.SourceRange.Overlaps(range)).Select(item => item.Id).ToImmutableArray(),
            ImmutableDictionary<string, string>.Empty, SegmentRole: role);
        var sequence = project.ActiveSequence!;
        var graph = new MontageGraph(Guid.NewGuid(), Guid.NewGuid(), sequence.Id, sequence.Revision, brief,
            [Remove(opening, SegmentRole.Opening, 0), Remove(ending, SegmentRole.Ending, 1)], 1, now, now);
        return new Fixture(project, source, opening, index, new MediaUnderstandingCatalog([index]), graph);
    }

    private static MultiEpisodeFixture CreateMultiEpisodeFixture()
    {
        var project = ProjectState.CreateNew();
        var visual = project.Tracks.First(item => item.Kind == TrackKind.Visual && item.Index == 0);
        var audio = project.Tracks.First(item => item.Kind == TrackKind.Audio && item.Index == 0);
        var sources = project.Sources.ToBuilder();
        var clips = ImmutableArray.CreateBuilder<MediaClip>();
        var indexes = ImmutableArray.CreateBuilder<MediaUnderstandingIndex>();
        var sourceIds = new List<Guid>();
        var duration = TimelineTime.FromSeconds(600);
        var opening = new TimeRange(TimelineTime.FromSeconds(30), TimelineTime.FromSeconds(30));
        var ending = new TimeRange(TimelineTime.FromSeconds(500), TimelineTime.FromSeconds(30));
        var now = DateTimeOffset.UtcNow;
        for (var episode = 0; episode < 2; episode++)
        {
            var source = new MediaSource(
                Guid.NewGuid(), $"C:\\media\\episode-{episode + 1:00}.mkv", $"episode-{episode + 1:00}.mkv",
                MediaKind.Video, duration, true, 1920, 1080, FrameRate.Fps24,
                "hevc", "aac", Fingerprint: $"episode-{episode + 1:00}-fingerprint");
            sourceIds.Add(source.Id);
            sources.Add(source.Id, source);
            var start = TimelineTime.FromSeconds(episode * 600);
            var link = Guid.NewGuid();
            clips.Add(new MediaClip(Guid.NewGuid(), source.Id, visual.Id, start, TimelineTime.Zero, duration,
                link, new VideoParameters(), StreamIndex: 0));
            clips.Add(new MediaClip(Guid.NewGuid(), source.Id, audio.Id, start, TimelineTime.Zero, duration,
                link, Audio: new AudioParameters(), StreamIndex: 1));

            var facts = ImmutableArray.CreateBuilder<TemporalFact>();
            foreach (var (range, label) in new[] { (opening, "opening"), (ending, "ending") })
            {
                facts.Add(new TemporalFact(Guid.NewGuid(), source.Id, range, CoverageChannel.Audio,
                    TemporalFactKind.AudioEvent, label + " music", 0.96, "audio-events", "2",
                    ImmutableDictionary<string, string>.Empty));
                facts.Add(new TemporalFact(Guid.NewGuid(), source.Id, range, CoverageChannel.Ocr,
                    TemporalFactKind.Ocr, label + " credits", 0.95, "video-understanding", "2",
                    ImmutableDictionary<string, string>.Empty));
                facts.Add(new TemporalFact(Guid.NewGuid(), source.Id,
                    new TimeRange(range.Start, FrameRate.Fps24.FrameDuration), CoverageChannel.Frames,
                    TemporalFactKind.ShotBoundary, label + " start", 0.99, "video-understanding", "2",
                    ImmutableDictionary<string, string>.Empty));
                facts.Add(new TemporalFact(Guid.NewGuid(), source.Id,
                    new TimeRange(range.End, FrameRate.Fps24.FrameDuration), CoverageChannel.Frames,
                    TemporalFactKind.ShotBoundary, label + " end", 0.99, "video-understanding", "2",
                    ImmutableDictionary<string, string>.Empty));
            }
            SegmentRoleHypothesis Hypothesis(TimeRange range, SegmentRole role)
            {
                var evidence = facts.Where(item => item.SourceRange.Overlaps(range)).Select(item => item.Id).ToImmutableArray();
                return new SegmentRoleHypothesis(
                    Guid.NewGuid(), source.Id, range, role, 0.94,
                    new CandidateBoundary(Guid.NewGuid(), source.Id, range.Start, 0.97, evidence),
                    new CandidateBoundary(Guid.NewGuid(), source.Id, range.End, 0.97, evidence),
                    evidence, [CoverageChannel.Audio, CoverageChannel.Ocr, CoverageChannel.Frames],
                    "video-understanding", "2");
            }
            var coverage = new CoverageMap(source.Id, source.Fingerprint, duration,
                    ImmutableDictionary<CoverageChannel, ImmutableArray<CoverageInterval>>.Empty)
                .Add(CoverageChannel.Frames, CoverageInterval.Create(opening, 100, false, "video-understanding", "2"))
                .Add(CoverageChannel.Frames, CoverageInterval.Create(ending, 100, false, "video-understanding", "2"));
            indexes.Add(new MediaUnderstandingIndex(
                Guid.NewGuid(), source.Id, source.Fingerprint, "editorial-v2.1", duration, coverage,
                [new AnalyzerManifest("video-understanding", "2", "test", "test", [CoverageChannel.Frames], now)],
                [], [], [], [], facts.ToImmutable(), [], [], now, now,
                SegmentRoleHypotheses:
                [
                    Hypothesis(opening, SegmentRole.Opening),
                    Hypothesis(ending, SegmentRole.Ending)
                ]));
        }
        project = (project with { Sources = sources.ToImmutable(), MediaClips = clips.ToImmutable() })
            .EnsureSequenceContainer();
        return new MultiEpisodeFixture(
            project, new MediaUnderstandingCatalog(indexes.ToImmutable()), sourceIds[0], sourceIds[1]);
    }

    private sealed record Fixture(
        ProjectState Project,
        MediaSource Source,
        TimeRange Range,
        MediaUnderstandingIndex Index,
        MediaUnderstandingCatalog Catalog,
        MontageGraph Graph);

    private sealed record MultiEpisodeFixture(
        ProjectState Project,
        MediaUnderstandingCatalog Catalog,
        Guid FirstSourceId,
        Guid SecondSourceId);

    private sealed class FixedTokenCounter(int count) : IModelTokenCounter
    {
        public int CountTokens(string model, string text) => count;
    }

    private sealed class FakeReferenceProvider : IExternalReferenceSearchProvider
    {
        public Task<ImmutableArray<ReferenceSearchResult>> SearchAsync(
            string query,
            CancellationToken cancellationToken)
            => Task.FromResult<ImmutableArray<ReferenceSearchResult>>([
                new("https://example.test/reference", "Reference", "Public style reference."),
                new("file:///private/source.mp4", "Rejected", "Unsafe local URI.")
            ]);
    }

    private sealed class FakeIndexer(MediaUnderstandingCatalog catalog)
        : IMediaUnderstandingIndexer, IEvidenceGapResolver
    {
        public Task<MediaUnderstandingCatalog> EnsureIndexesAsync(
            ProjectState project, Guid sourceSequenceId, MontageProfile profile,
            IProgress<EditorialPipelineProgress>? progress, CancellationToken cancellationToken)
            => Task.FromResult(catalog);

        public Task<MediaUnderstandingCatalog> ResolveAsync(
            ProjectState project, MediaUnderstandingCatalog indexes,
            ImmutableArray<EvidenceGap> gaps,
            IProgress<EditorialPipelineProgress>? progress, CancellationToken cancellationToken)
            => Task.FromResult(indexes);
    }

    private sealed class FakeReasoner(
        ImmutableArray<EditDecision> decisions,
        bool injectUnsupportedAudio = false,
        ImmutableArray<Guid> selectedTargets = default,
        double targetConfidence = 1)
        : IEditorialDirector, IEditorialCritic
    {
        public int RefinementCalls { get; private set; }
        public int CriticCalls { get; private set; }
        public int RoughCutCalls { get; private set; }
        public int BriefCalls { get; private set; }

        public Task<MontageProfileKind> SuggestProfileAsync(
            ProjectState project, Guid sourceSequenceId, string userRequest,
            CancellationToken cancellationToken)
            => Task.FromResult(MontageProfileKind.Generic);

        public Task<EditorialBrief> CreateBriefAsync(
            ProjectState project, Guid sourceSequenceId, string userRequest,
            MontageProfile profile, string revisionFeedback, CancellationToken cancellationToken)
        {
            BriefCalls++;
            return Task.FromResult(new EditorialBrief(
                Guid.NewGuid(), userRequest, "viewer", profile,
                TimelineTime.FromSeconds(10), "clean", [], [], DateTimeOffset.UtcNow,
                TargetSourceIds: selectedTargets.IsDefault ? [] : selectedTargets,
                TargetSelectionConfidence: targetConfidence,
                TargetSelectionRationale: "test semantic selection"));
        }

        public Task<MontageGraph> CreateRoughCutAsync(
            EditorialBrief brief, EditorialWorkingSet workingSet, Guid taskId,
            Guid sourceSequenceId, long sourceSequenceRevision,
            MontageGraph? previousRejectedGraph, CancellationToken cancellationToken)
        {
            RoughCutCalls++;
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new MontageGraph(
                Guid.NewGuid(), taskId, sourceSequenceId, sourceSequenceRevision,
                brief, decisions, 1, now, now));
        }

        public Task<DraftPatch> RefineAsync(
            EditorialPassKind pass, EditorialBrief brief, MontageGraph graph,
            EditorialWorkingSet workingSet, int order, CancellationToken cancellationToken)
        {
            RefinementCalls++;
            ImmutableArray<EditDecision> patchDecisions = injectUnsupportedAudio && pass == EditorialPassKind.DialogueAudio
                ? [decisions[0] with
                {
                    Id = Guid.NewGuid(),
                    Kind = EditDecisionKind.ApplyAudioMix,
                    Order = 100
                }]
                : ImmutableArray<EditDecision>.Empty;
            return Task.FromResult(new DraftPatch(
                Guid.NewGuid(), graph.Id, pass, order, patchDecisions, pass.ToString(), DateTimeOffset.UtcNow));
        }

        public Task<ImmutableArray<DraftQualityIssue>> ReviewGraphAsync(
            EditorialBrief brief, MontageGraph graph, EditorialWorkingSet workingSet,
            CancellationToken cancellationToken)
        {
            CriticCalls++;
            return Task.FromResult(ImmutableArray<DraftQualityIssue>.Empty);
        }
    }
}
