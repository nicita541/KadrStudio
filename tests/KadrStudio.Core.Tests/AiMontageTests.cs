using System.Collections.Immutable;
using System.Text.Json;
using KadrStudio.Application.Automation;
using KadrStudio.Application.Editing;
using KadrStudio.Core.Domain;
using KadrStudio.Infrastructure.Storage;

namespace KadrStudio.Core.Tests;

public sealed class AiMontageTests
{
    [Fact]
    public void Anime_merge_plan_keeps_one_opening_first_and_removes_confirmed_service_blocks()
    {
        var fixture = CreateAnimeFixture(uncertainEndingStart: false);

        var plan = new AnimeEpisodeMergePlanner().CreatePlan(
            fixture.Project, fixture.Request, fixture.Manifests);
        var validation = new MontagePlanValidator().Validate(fixture.Project, plan);

        Assert.Equal(MontagePlanStatus.Ready, plan.Status);
        Assert.True(validation.IsValid, string.Join("; ", validation.Validation.Errors.Select(item => item.Message)));
        var opening = Assert.Single(plan.Items, item => item.Role == MontageRole.Opening);
        Assert.Equal(plan.Items.OrderBy(item => item.Order).First().Id, opening.Id);
        Assert.Null(opening.TransitionAfter);
        Assert.All(plan.Items, item => Assert.Null(item.TransitionAfter));
        Assert.All(fixture.Project.Sources.Keys, sourceId =>
            Assert.Contains(plan.Items, item => item.SourceId == sourceId && item.Role != MontageRole.Opening));
        foreach (var removed in plan.StructuralSegments.Where(item =>
                     item.Kind == StructuralSegmentKind.Opening ||
                     item.Disposition == StructuralSegmentDisposition.Remove))
            Assert.DoesNotContain(plan.Items, item => item.Role != MontageRole.Opening &&
                item.SourceId == removed.SourceId && item.SourceRange.Overlaps(removed.SourceRange));
        foreach (var story in plan.StructuralSegments.Where(item => item.Kind == StructuralSegmentKind.PostCreditsStory))
            Assert.Contains(plan.Items, item => item.SourceId == story.SourceId && item.SourceRange.Overlaps(story.SourceRange));
        var firstEpisodeBody = plan.Items.First(item => item.Role != MontageRole.Opening);
        Assert.Equal("episode-01.mp4", fixture.Project.Sources[firstEpisodeBody.SourceId].Name);
    }

    [Fact]
    public void Ambiguous_episode_order_and_equivalent_openings_create_explicit_questions()
    {
        var fixture = CreateAnimeFixture(uncertainEndingStart: false);
        var ids = fixture.Request.Scope.SourceIds;
        var project = fixture.Project with
        {
            Sources = fixture.Project.Sources
                .SetItem(ids[0], fixture.Project.Sources[ids[0]] with { Name = "zeta.mp4" })
                .SetItem(ids[1], fixture.Project.Sources[ids[1]] with { Name = "alpha.mp4" })
        };
        var manifests = fixture.Manifests.ToImmutableDictionary(
            pair => pair.Key,
            pair => pair.Value with
            {
                StructuralSegments = pair.Value.StructuralSegments.Select(item =>
                    item.Kind == StructuralSegmentKind.Opening ? item with { Confidence = 0.9 } : item).ToImmutableArray()
            });

        var plan = new AnimeEpisodeMergePlanner().CreatePlan(project, fixture.Request, manifests);

        Assert.Equal(MontagePlanStatus.NeedsInput, plan.Status);
        Assert.Contains(plan.Decisions, item => item.Kind == MontageDecisionKind.SourceOrder && !item.IsResolved);
        Assert.Contains(plan.Decisions, item => item.Kind == MontageDecisionKind.OpeningSelection && !item.IsResolved);
    }

    [Fact]
    public void Uncertain_anime_boundary_blocks_compilation_until_user_confirms_exact_frame()
    {
        var fixture = CreateAnimeFixture(uncertainEndingStart: true);
        var planner = new AnimeEpisodeMergePlanner();
        var plan = planner.CreatePlan(fixture.Project, fixture.Request, fixture.Manifests);
        var decision = Assert.Single(plan.Decisions.Where(item =>
            !item.IsResolved && item.Kind == MontageDecisionKind.SegmentStart));

        Assert.Equal(MontagePlanStatus.NeedsInput, plan.Status);
        Assert.Contains(new MontagePlanValidator().Validate(fixture.Project, plan).Validation.Errors,
            item => item.Code == "anime.boundary");
        Assert.Throws<InvalidOperationException>(() => new MontagePlanCompiler().Compile(fixture.Project, plan));

        var resolved = planner.ResolveDecision(
            fixture.Project, plan, decision.Id, "90.000", TimelineTime.FromSeconds(90));

        Assert.Equal(MontagePlanStatus.Ready, resolved.Status);
        Assert.True(new MontagePlanValidator().Validate(fixture.Project, resolved).IsValid);
        Assert.DoesNotContain(resolved.Decisions, item => !item.IsResolved);
        Assert.Equal(BoundaryResolutionStatus.UserConfirmed,
            resolved.StructuralSegments.Single(item => item.Id == decision.SegmentId).StartBoundary.Status);
    }

    [Fact]
    public async Task Needs_input_plan_is_valid_in_sqlite_schema_v5()
    {
        var fixture = CreateAnimeFixture(uncertainEndingStart: true);
        var plan = new AnimeEpisodeMergePlanner().CreatePlan(
            fixture.Project, fixture.Request, fixture.Manifests);
        var root = Path.Combine(Path.GetTempPath(), "KadrStudio", "needs-input-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "needs-input.kadr");
            var store = new SqliteProjectStore();
            await store.SaveAsync(path, fixture.Project with { MontagePlans = [plan] });

            var restored = await store.LoadAsync(path);

            Assert.Equal(MontagePlanStatus.NeedsInput, Assert.Single(restored.MontagePlans).Status);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Legacy_game_profile_json_defaults_to_game_material_kind()
    {
        const string json = """
            {"Id":"legacy","Version":1,"DisplayName":"Legacy","GameFamily":"Sandbox","EventTags":[],"EventWeights":{},"MinimumSegmentSeconds":1,"MaximumSegmentSeconds":10,"ContextBeforeSeconds":0,"ContextAfterSeconds":0,"PlanningGuidance":"legacy"}
            """;

        var restored = JsonSerializer.Deserialize<GameEditingProfile>(json);

        Assert.NotNull(restored);
        Assert.Equal(MaterialProfileKind.Game, restored.Kind);
    }

    [Fact]
    public void Compiler_builds_linked_portrait_draft_without_changing_original_sequence()
    {
        var fixture = CreateFixture();
        var before = fixture.Project;

        var result = new MontagePlanCompiler().Compile(before, fixture.Plan, fixture.Manifests);

        Assert.Equal(1080, result.Sequence.Settings.CanvasWidth);
        Assert.Equal(1920, result.Sequence.Settings.CanvasHeight);
        Assert.Equal(SequenceStatus.Draft, result.Sequence.Status);
        Assert.Equal(before.ActiveSequenceId, result.Sequence.ParentSequenceId);
        Assert.Equal(fixture.Plan.Id, result.Sequence.MontagePlanId);
        var clips = result.Sequence.MediaClips.OrderBy(item => item.TrackId).ToArray();
        Assert.Equal(2, clips.Length);
        Assert.NotNull(clips[0].LinkGroupId);
        Assert.Equal(clips[0].LinkGroupId, clips[1].LinkGroupId);
        Assert.All(clips, item => Assert.Equal(TimelineTime.FromSeconds(10), item.SourceIn));
        var video = Assert.Single(clips, item => item.Video is not null);
        Assert.True(video.Video!.CropLeft > 0);
        Assert.True(video.Video.CropRight > 0);
        Assert.Single(result.Sequence.TextClips);
        Assert.Empty(before.MediaClips);
        Assert.Single(before.Sequences);
    }

    [Fact]
    public void Draft_creation_switching_and_undo_redo_keep_variants_independent()
    {
        var fixture = CreateFixture();
        var compiled = new MontagePlanCompiler().Compile(fixture.Project, fixture.Plan, fixture.Manifests).Sequence;
        var originalId = fixture.Project.ActiveSequenceId!.Value;
        var session = new EditorSession(fixture.Project);

        session.Execute(new EditTransaction("create AI draft", [
            new UpsertMontagePlanCommand(fixture.Plan),
            new CreateSequenceCommand(compiled, Activate: true)
        ]));

        Assert.Equal(compiled.Id, session.State.ActiveSequenceId);
        Assert.Equal(2, session.State.Sequences.Length);
        Assert.NotEmpty(session.State.MediaClips);
        Assert.Empty(session.State.FindSequence(originalId)!.MediaClips);
        Assert.True(session.Undo());
        Assert.Equal(originalId, session.State.ActiveSequenceId);
        Assert.Single(session.State.Sequences);
        Assert.Empty(session.State.MediaClips);
        Assert.True(session.Redo());
        Assert.Equal(compiled.Id, session.State.ActiveSequenceId);

        session.Execute(new EditTransaction("open original", new ActivateSequenceCommand(originalId)));
        Assert.Empty(session.State.MediaClips);
        Assert.NotEmpty(session.State.FindSequence(compiled.Id)!.MediaClips);
    }

    [Fact]
    public void Validator_rejects_missing_required_and_intersecting_excluded_ranges()
    {
        var fixture = CreateFixture();
        var required = new MontageConstraint(
            Guid.NewGuid(), fixture.Source.Id, SourceAnnotationKind.Required,
            Range(30, 3), "must include", true);
        var excluded = new MontageConstraint(
            Guid.NewGuid(), fixture.Source.Id, SourceAnnotationKind.Excluded,
            Range(11, 1), "do not use", true);
        var invalid = fixture.Plan with { Constraints = [required, excluded] };

        var result = new MontagePlanValidator().Validate(fixture.Project, invalid);

        Assert.False(result.IsValid);
        Assert.Contains(result.Validation.Errors, item => item.Code == "montage.required");
        Assert.Contains(result.Validation.Errors, item => item.Code == "montage.excluded");
    }

    [Fact]
    public async Task Sqlite_v5_roundtrip_preserves_sequences_plans_annotations_and_analysis_references()
    {
        var fixture = CreateFixture();
        var compiled = new MontagePlanCompiler().Compile(fixture.Project, fixture.Plan, fixture.Manifests).Sequence;
        var active = (fixture.Project with
        {
            MontagePlans = [fixture.Plan],
            SourceAnnotations =
            [
                new SourceAnnotation(Guid.NewGuid(), fixture.Source.Id, SourceAnnotationKind.Required,
                    fixture.Plan.Items[0].SourceRange, "clutch", DateTimeOffset.UtcNow)
            ],
            AnalysisReferences =
            [
                new MediaAnalysisReference(fixture.Source.Id, "verified", "analysis-v1", "test-model",
                    fixture.Profile.Id, fixture.Profile.Version, DateTimeOffset.UtcNow)
            ],
            Sequences = fixture.Project.Sequences.Add(compiled)
        }).ActivateSequence(compiled.Id);
        var root = Path.Combine(Path.GetTempPath(), "KadrStudio", "ai-montage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "workspace.kadr");
            var store = new SqliteProjectStore();

            await store.SaveAsync(path, active);
            var restored = await store.LoadAsync(path);

            Assert.Equal(compiled.Id, restored.ActiveSequenceId);
            Assert.Equal(2, restored.Sequences.Length);
            var restoredPlan = Assert.Single(restored.MontagePlans);
            Assert.Equal(fixture.Plan.Id, restoredPlan.Id);
            Assert.Equal(fixture.Plan.ProfileSnapshot.Id, restoredPlan.ProfileSnapshot.Id);
            Assert.Equal(fixture.Plan.ProfileSnapshot.EventTags.ToArray(), restoredPlan.ProfileSnapshot.EventTags.ToArray());
            Assert.Equal(
                fixture.Plan.Dependencies.SourceFingerprints.OrderBy(item => item.Key).ToArray(),
                restoredPlan.Dependencies.SourceFingerprints.OrderBy(item => item.Key).ToArray());
            var restoredItem = Assert.Single(restoredPlan.Items);
            Assert.Equal(fixture.Plan.Items[0].Id, restoredItem.Id);
            Assert.Equal(fixture.Plan.Items[0].SourceRange, restoredItem.SourceRange);
            Assert.Equal(fixture.Plan.Items[0].Evidence.ToArray(), restoredItem.Evidence.ToArray());
            Assert.Equal(active.SourceAnnotations.ToArray(), restored.SourceAnnotations.ToArray());
            Assert.Equal(active.AnalysisReferences.ToArray(), restored.AnalysisReferences.ToArray());
            Assert.Equal(compiled.MediaClips.ToArray(), restored.MediaClips.ToArray());
            Assert.Empty(restored.FindSequence(fixture.Project.ActiveSequenceId!.Value)!.MediaClips);

            var recovery = new SqliteRecoveryStore(Path.Combine(root, "recovery"));
            await recovery.SaveAsync(active, "AI montage autosave");
            var recovered = Assert.IsType<ProjectState>(await recovery.LoadAsync(active.Id));
            Assert.Equal(active.ActiveSequenceId, recovered.ActiveSequenceId);
            Assert.Equal(2, recovered.Sequences.Length);
            Assert.Single(recovered.MontagePlans);
            Assert.Single(recovered.SourceAnnotations);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Revision_provider_cannot_change_locked_items()
    {
        var fixture = CreateFixture();
        var lockedPlan = fixture.Plan with
        {
            Items = [fixture.Plan.Items[0] with { IsLocked = true }]
        };
        var provider = new MutatingRevisionProvider();
        var coordinator = new AiMontageCoordinator(new StubAnalysisPipeline(), provider);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RevisePlanAsync(
            fixture.Project, lockedPlan, "change everything", fixture.Manifests));
    }

    [Fact]
    public async Task Planner_limits_selected_clip_scope_to_the_clips_source_range()
    {
        var fixture = CreateFixture();
        var visual = fixture.Project.Tracks.Single(item => item.Kind == TrackKind.Visual && item.Index == 0);
        var clip = new MediaClip(
            Guid.NewGuid(), fixture.Source.Id, visual.Id, TimelineTime.Zero,
            TimelineTime.FromSeconds(20), TimelineTime.FromSeconds(5), Video: new VideoParameters());
        var project = (fixture.Project with { MediaClips = [clip] }).SynchronizeActiveSequence(incrementRevision: false);
        var outside = fixture.Manifests[fixture.Source.Id].Segments[0] with
        {
            Id = Guid.NewGuid(),
            SourceRange = Range(2, 3)
        };
        var inside = outside with { Id = Guid.NewGuid(), SourceRange = Range(21, 3), Confidence = 0.8 };
        var manifests = fixture.Manifests.SetItem(
            fixture.Source.Id,
            fixture.Manifests[fixture.Source.Id] with { Segments = [outside, inside] });
        var request = new MontageRequest(
            Guid.NewGuid(),
            new MontageScope(MontageScopeKind.SelectedClips, [fixture.Source.Id],
                project.ActiveSequenceId, [clip.Id]),
            MontageTargetFormat.YouTube,
            TimelineTime.FromSeconds(1), TimelineTime.FromSeconds(2), TimelineTime.FromSeconds(10),
            "selected clip only", fixture.Profile, []);

        var plan = await new EvidenceMontagePlanningProvider().CreatePlanAsync(
            new MontagePlanningContext(project, request, manifests));

        var item = Assert.Single(plan.Items);
        Assert.Equal(inside.Id, item.Id);
        Assert.True(item.SourceRange.Start >= clip.SourceIn);
        Assert.True(item.SourceRange.End <= clip.SourceIn + clip.Duration);
    }

    [Fact]
    public void Changed_source_fingerprint_blocks_draft_compilation()
    {
        var fixture = CreateFixture();
        var changed = fixture.Project with
        {
            Sources = fixture.Project.Sources.SetItem(
                fixture.Source.Id,
                fixture.Source with { VerifiedFingerprint = "changed" })
        };

        var validation = new MontagePlanValidator().Validate(changed, fixture.Plan);

        Assert.Contains(validation.Validation.Errors, item => item.Code == "montage.source-stale");
        Assert.Throws<InvalidOperationException>(() => new MontagePlanCompiler().Compile(changed, fixture.Plan));
    }

    [Fact]
    public void Annotation_added_after_planning_invalidates_the_plan()
    {
        var fixture = CreateFixture();
        var annotation = new SourceAnnotation(
            Guid.NewGuid(), fixture.Source.Id, SourceAnnotationKind.Excluded,
            fixture.Plan.Items[0].SourceRange, "added later", DateTimeOffset.UtcNow);
        var changed = fixture.Project with { SourceAnnotations = [annotation] };

        var validation = new MontagePlanValidator().Validate(changed, fixture.Plan);

        Assert.Contains(validation.Validation.Errors, item => item.Code == "montage.annotations-stale");
    }

    private static Fixture CreateFixture()
    {
        var project = ProjectState.CreateNew("AI montage");
        var source = new MediaSource(
            Guid.NewGuid(), "F:\\media\\gameplay.mp4", "gameplay.mp4", MediaKind.Video,
            TimelineTime.FromSeconds(120), true, 1920, 1080, FrameRate.Fps30,
            "h264", "aac", 1000, 2000, "legacy", FastFingerprint: "fast", VerifiedFingerprint: "verified");
        project = (project with { Sources = project.Sources.Add(source.Id, source) }).EnsureSequenceContainer();
        var profile = GameEditingProfiles.Get("rust");
        var evidence = new AnalysisEvidence(MontageEvidenceKind.Vision, "PvP victory", "frame-10");
        var segment = new AnalysisSegment(
            Guid.NewGuid(), source.Id, Range(10, 4), 0.9, 0.8, 0.7, "We won",
            ImmutableDictionary<string, double>.Empty.Add("pvp", 0.95), 0.93, [evidence]);
        var manifest = new MediaAnalysisManifest(
            source.Id, "verified", "analysis-v1", "test-model", profile.Id, profile.Version,
            DateTimeOffset.UtcNow, [segment]);
        var item = new MontagePlanItem(
            segment.Id, source.Id, segment.SourceRange, MontageRole.Hook, 0,
            "Opening clutch", 0.93, [evidence], TransitionAfter: TransitionKind.CrossDissolve);
        var sequence = project.ActiveSequence!;
        var dependencies = new AutomationDependencyStamp(
            project.Id, sequence.Id, sequence.Revision,
            ImmutableDictionary<Guid, string>.Empty.Add(source.Id, "verified"),
            "analysis-v1", "test-model", profile.Id, profile.Version);
        var now = DateTimeOffset.UtcNow;
        var plan = new MontagePlan(
            Guid.NewGuid(), Guid.NewGuid(), "Shorts test", "Evidence-only plan", MontagePlanStatus.Ready,
            MontageTargetFormat.Shorts, TimelineTime.FromSeconds(1), TimelineTime.FromSeconds(4),
            TimelineTime.FromSeconds(30), profile, dependencies, [], [item], [], now, now);
        return new Fixture(
            project, source, profile, plan,
            ImmutableDictionary<Guid, MediaAnalysisManifest>.Empty.Add(source.Id, manifest));
    }

    private static AnimeFixture CreateAnimeFixture(bool uncertainEndingStart)
    {
        var project = ProjectState.CreateNew("Anime merge");
        var episode2 = new MediaSource(
            Guid.NewGuid(), "F:\\media\\episode-02.mp4", "episode-02.mp4", MediaKind.Video,
            TimelineTime.FromSeconds(120), true, 1920, 1080, FrameRate.Fps24,
            "h264", "aac", 2000, 3000, FastFingerprint: "ep2", VerifiedFingerprint: "episode-2");
        var episode1 = episode2 with
        {
            Id = Guid.NewGuid(), Path = "F:\\media\\episode-01.mp4", Name = "episode-01.mp4",
            FastFingerprint = "ep1", VerifiedFingerprint = "episode-1"
        };
        project = (project with
        {
            Sources = ImmutableDictionary<Guid, MediaSource>.Empty
                .Add(episode2.Id, episode2)
                .Add(episode1.Id, episode1)
        }).EnsureSequenceContainer();
        var profile = GameEditingProfiles.Get("anime");
        var evidence = ImmutableArray.Create(new AnalysisEvidence(
            MontageEvidenceKind.Vision, "Подтверждено кадрами", "anime-test"));

        ImmutableArray<StructuralSegment> Structure(MediaSource source, double openingConfidence, bool uncertain)
            =>
            [
                Segment(source, StructuralSegmentKind.Recap, 0, 8, 0.95, StructuralSegmentDisposition.Remove, evidence),
                Segment(source, StructuralSegmentKind.Opening, 8, 12, openingConfidence, StructuralSegmentDisposition.Remove, evidence),
                Segment(source, StructuralSegmentKind.Ending, 90, 10, 0.96, StructuralSegmentDisposition.Remove, evidence,
                    uncertainStart: uncertain),
                Segment(source, StructuralSegmentKind.PostCreditsStory, 100, 5, 0.95, StructuralSegmentDisposition.Retain, evidence),
                Segment(source, StructuralSegmentKind.Preview, 105, 5, 0.95, StructuralSegmentDisposition.Remove, evidence)
            ];

        var firstManifest = new MediaAnalysisManifest(
            episode1.Id, "episode-1", "content-analysis-v2", "vision-test",
            profile.Id, profile.Version, DateTimeOffset.UtcNow, [], Structure(episode1, 0.99, uncertainEndingStart));
        var secondManifest = new MediaAnalysisManifest(
            episode2.Id, "episode-2", "content-analysis-v2", "vision-test",
            profile.Id, profile.Version, DateTimeOffset.UtcNow, [], Structure(episode2, 0.88, false));
        var manifests = ImmutableDictionary<Guid, MediaAnalysisManifest>.Empty
            .Add(episode1.Id, firstManifest)
            .Add(episode2.Id, secondManifest);
        var request = new MontageRequest(
            Guid.NewGuid(),
            new MontageScope(MontageScopeKind.SelectedSources, [episode2.Id, episode1.Id]),
            MontageTargetFormat.Source,
            TimelineTime.FromSeconds(1), TimelineTime.FromSeconds(240), TimelineTime.FromSeconds(240),
            "Объедини серии", profile, [], AutomationPresets.AnimeMergeEpisodes);
        return new AnimeFixture(project, request, manifests);
    }

    private static StructuralSegment Segment(
        MediaSource source,
        StructuralSegmentKind kind,
        double start,
        double duration,
        double confidence,
        StructuralSegmentDisposition disposition,
        ImmutableArray<AnalysisEvidence> evidence,
        bool uncertainStart = false)
    {
        var sourceRange = Range(start, duration);
        var startBoundary = new ResolvedBoundary(
            sourceRange.Start, sourceRange.Start,
            uncertainStart ? BoundaryResolutionStatus.Suggested : BoundaryResolutionStatus.Verified,
            uncertainStart ? BoundaryPrecision.Coarse : BoundaryPrecision.Frame,
            uncertainStart ? 0.6 : confidence, evidence);
        var endBoundary = new ResolvedBoundary(
            sourceRange.End, sourceRange.End, BoundaryResolutionStatus.Verified,
            BoundaryPrecision.Frame, confidence, evidence);
        return new StructuralSegment(
            Guid.NewGuid(), source.Id, kind, sourceRange, disposition,
            confidence, startBoundary, endBoundary, evidence);
    }

    private static TimeRange Range(double start, double duration)
        => new(TimelineTime.FromSeconds(start), TimelineTime.FromSeconds(duration));

    private sealed record Fixture(
        ProjectState Project,
        MediaSource Source,
        GameEditingProfile Profile,
        MontagePlan Plan,
        ImmutableDictionary<Guid, MediaAnalysisManifest> Manifests);

    private sealed record AnimeFixture(
        ProjectState Project,
        MontageRequest Request,
        ImmutableDictionary<Guid, MediaAnalysisManifest> Manifests);

    private sealed class StubAnalysisPipeline : IMediaAnalysisPipeline
    {
        public Task<ImmutableDictionary<Guid, MediaAnalysisManifest>> AnalyzeSourcesAsync(
            ProjectState project,
            MediaAnalysisRequest request,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ImmutableDictionary<Guid, MediaAnalysisManifest>.Empty);
    }

    private sealed class MutatingRevisionProvider : IMontagePlanningProvider
    {
        public Task<MontagePlan> CreatePlanAsync(
            MontagePlanningContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(context.PreviousPlan!);

        public Task<MontagePlan> RevisePlanAsync(
            MontagePlanningContext context,
            CancellationToken cancellationToken = default)
        {
            var plan = context.PreviousPlan!;
            return Task.FromResult(plan with
            {
                Items = [plan.Items[0] with { SourceRange = Range(12, 2) }],
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
    }
}
