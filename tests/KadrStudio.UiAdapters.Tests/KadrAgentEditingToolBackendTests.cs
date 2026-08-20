using KadrStudio.Application.Automation.Agent;
using KadrStudio.Application.Automation.Agent.Runtime;
using KadrStudio.Application.Automation.Agent.Tools;
using KadrStudio.Application.Automation.Agent.Tools.Editing;
using KadrStudio.Application.Editing;
using KadrStudio.Core.Domain;
using KadrStudio.Services.Agent;

namespace KadrStudio.UiAdapters.Tests;

public sealed class KadrAgentEditingToolBackendTests
{
    [Fact]
    public async Task Request_to_plan_approval_real_edit_log_and_verification_changes_only_agent_draft()
    {
        var fixture = CreateFixture();
        var sourceBefore = fixture.Session.State.FindSequence(fixture.SourceSequenceId)!;
        var orchestrator = new AiAgentOrchestrator();
        orchestrator.StartTask(
            fixture.Session.State.Id,
            fixture.SourceSequenceId,
            "Удалить подтверждённый диапазон 5–15 секунд, остальное не трогать.",
            sourceSequenceRevision: sourceBefore.Revision);
        fixture.Backend.Reset(orchestrator.CurrentTask!.Id);

        var registry = new AgentToolRegistry();
        registry.Register(new EndToEndRangeEvidenceTool(fixture.SourceSequenceId));
        AgentEditingToolSet.RegisterDefaults(registry, fixture.Backend);
        registry.Register(new EndToEndVerificationTool("inspect_timeline_integrity"));
        registry.Register(new EndToEndVerificationTool("compare_sequences"));
        var model = new EndToEndAgentModel(fixture.SourceSequenceId);
        var executor = new AgentToolExecutor(registry);
        var planning = new AgentPlanningLoop(
            orchestrator,
            registry,
            executor,
            model);

        var waitingForApproval = await planning.RunUntilPauseAsync();

        Assert.Equal(AgentTaskPhase.WaitingForApproval, waitingForApproval.Phase);
        Assert.NotNull(waitingForApproval.Plan);
        Assert.Equal("ripple_delete_range", waitingForApproval.Plan!.Steps
            .Single(step => !string.IsNullOrWhiteSpace(step.ExpectedEditingTool))
            .ExpectedEditingTool);

        orchestrator.ApprovePlan();
        orchestrator.BeginExecution(fixture.DraftSequenceId);
        var completed = await new AgentExecutionLoop(
            orchestrator,
            registry,
            executor,
            model,
            seedObservationProvider: () => planning.Observations)
            .RunUntilPauseAsync();

        Assert.Equal(AgentTaskPhase.Completed, completed.Phase);
        var sourceAfter = fixture.Session.State.FindSequence(fixture.SourceSequenceId)!;
        var draftAfter = fixture.Session.State.FindSequence(fixture.DraftSequenceId)!;
        Assert.Equal(sourceBefore.MediaClips, sourceAfter.MediaClips);
        Assert.Equal(TimelineTime.FromSeconds(60), sourceAfter.Duration);
        Assert.Equal(TimelineTime.FromSeconds(50), draftAfter.Duration);
        var editLog = await fixture.Backend.InspectEditLogAsync(
            AgentToolContext.FromTask(completed),
            CancellationToken.None);
        var edit = Assert.Single(editLog.GetProperty("edits").EnumerateArray());
        Assert.Equal("ripple_delete_range", edit.GetProperty("toolName").GetString());
        Assert.Equal(1, model.UnderstandingCalls);
        Assert.Equal(2, model.PlanningCalls);
        Assert.Equal(1, model.CriticCalls);
    }

    [Fact]
    public void Universal_editing_tools_are_registered_without_freeform_reason_arguments()
    {
        var fixture = CreateFixture();
        var registry = new AgentToolRegistry();

        AgentEditingToolSet.RegisterDefaults(registry, fixture.Backend);

        var names = registry.Descriptors.Select(descriptor => descriptor.Name).ToHashSet();
        Assert.Contains("set_clip_video", names);
        Assert.Contains("set_clip_audio", names);
        Assert.Contains("unlink_clips", names);
        Assert.Contains("delete_timeline_objects", names);
        Assert.Contains("add_marker", names);
        Assert.Contains("update_marker", names);
        Assert.Contains("update_text", names);
        Assert.Contains("update_transition", names);
        Assert.Contains("split_clips", names);
        Assert.Contains("insert_source_range", names);
        Assert.DoesNotContain("split_timeline_at", names);
        Assert.All(
            registry.Descriptors.Where(descriptor => descriptor.Access == AgentToolAccess.Editing),
            descriptor => Assert.False(
                descriptor.InputSchema.GetProperty("properties").TryGetProperty("reason", out _),
                $"Editing tool '{descriptor.Name}' exposes a free-form reason argument."));
    }

    [Fact]
    public async Task Exact_split_changes_only_requested_link_group_and_preserves_source()
    {
        var fixture = CreateFixture();
        var sourceBefore = fixture.Session.State.FindSequence(fixture.SourceSequenceId)!;
        var draftBefore = fixture.Session.State.FindSequence(fixture.DraftSequenceId)!;
        var video = draftBefore.MediaClips.First(clip =>
            draftBefore.Tracks.First(track => track.Id == clip.TrackId).Kind == TrackKind.Visual);

        await fixture.Backend.SplitClipsAsync(
            fixture.Context,
            [video.Id],
            10,
            includeLinked: true,
            "Approved agent plan",
            CancellationToken.None);

        var state = fixture.Session.State;
        Assert.Equal(sourceBefore.MediaClips, state.FindSequence(fixture.SourceSequenceId)!.MediaClips);
        Assert.Equal(4, state.FindSequence(fixture.DraftSequenceId)!.MediaClips.Length);
        var edit = Assert.Single((await fixture.Backend.InspectEditLogAsync(
            fixture.Context,
            CancellationToken.None)).GetProperty("edits").EnumerateArray());
        Assert.Equal("split_clips", edit.GetProperty("toolName").GetString());
    }

    [Fact]
    public async Task Partial_audio_update_preserves_omitted_parameters()
    {
        var fixture = CreateFixture();
        var draft = fixture.Session.State.FindSequence(fixture.DraftSequenceId)!;
        var audio = draft.MediaClips.First(clip =>
            draft.Tracks.First(track => track.Id == clip.TrackId).Kind == TrackKind.Audio);

        await fixture.Backend.UpdateClipAudioAsync(
            fixture.Context,
            audio.Id,
            new AgentAudioParametersPatch(Volume: 0.5),
            "Approved agent plan",
            CancellationToken.None);

        var updated = fixture.Session.State.FindSequence(fixture.DraftSequenceId)!
            .MediaClips.Single(clip => clip.Id == audio.Id).Audio!;
        Assert.Equal(0.5, updated.Volume);
        Assert.Equal(audio.Audio!.Pan, updated.Pan);
        Assert.Equal(audio.Audio.FadeIn, updated.FadeIn);
        Assert.Equal(audio.Audio.Treble, updated.Treble);
    }

    [Fact]
    public async Task Partial_text_update_preserves_omitted_timing_and_style()
    {
        var fixture = CreateFixture();
        var added = await fixture.Backend.AddTextAsync(
            fixture.Context,
            2,
            4,
            "Original",
            subtitle: true,
            fontSize: 42,
            x: 0.4,
            y: 0.8,
            "Approved agent plan",
            CancellationToken.None);
        var textClipId = added.GetProperty("text_clip_id").GetGuid();

        await fixture.Backend.UpdateTextAsync(
            fixture.Context,
            textClipId,
            startSeconds: null,
            durationSeconds: null,
            text: "Updated",
            subtitle: null,
            fontSize: 56,
            x: null,
            y: null,
            "Approved agent plan",
            CancellationToken.None);

        var updated = fixture.Session.State.FindSequence(fixture.DraftSequenceId)!
            .TextClips.Single(item => item.Id == textClipId);
        Assert.Equal(TimelineTime.FromSeconds(2), updated.Start);
        Assert.Equal(TimelineTime.FromSeconds(4), updated.Duration);
        Assert.Equal("Updated", updated.Text);
        Assert.True(updated.Style.IsSubtitle);
        Assert.Equal(56, updated.Style.FontSize);
        Assert.Equal(0.4, updated.Style.X);
        Assert.Equal(0.8, updated.Style.Y);
    }

    [Fact]
    public async Task Marker_update_accepts_only_neutral_note_markers()
    {
        var fixture = CreateFixture();
        var added = await fixture.Backend.AddMarkerAsync(
            fixture.Context,
            5,
            1,
            "Check",
            "Original",
            "Approved agent plan",
            CancellationToken.None);
        var markerId = added.GetProperty("marker_id").GetGuid();

        await fixture.Backend.UpdateMarkerAsync(
            fixture.Context,
            markerId,
            startSeconds: null,
            durationSeconds: 2,
            title: null,
            description: "Updated",
            "Approved agent plan",
            CancellationToken.None);

        var updated = fixture.Session.State.FindSequence(fixture.DraftSequenceId)!
            .Markers.Single(item => item.Id == markerId);
        Assert.Equal(MarkerKind.Note, updated.Kind);
        Assert.Equal(TimelineTime.FromSeconds(5), updated.Start);
        Assert.Equal(TimelineTime.FromSeconds(2), updated.Duration);
        Assert.Equal("Check", updated.Title);
        Assert.Equal("Updated", updated.Description);
    }

    [Fact]
    public async Task Ripple_delete_changes_only_agent_draft_and_preserves_source_sequence()
    {
        var fixture = CreateFixture();
        var sourceBefore = fixture.Session.State
            .FindSequence(fixture.SourceSequenceId)!;

        var result = await fixture.Backend.RippleDeleteRangeAsync(
            fixture.Context,
            5,
            15,
            "Удалить подтверждённый диапазон.",
            CancellationToken.None);

        var state = fixture.Session.State;
        var sourceAfter = state.FindSequence(fixture.SourceSequenceId)!;
        var draftAfter = state.FindSequence(fixture.DraftSequenceId)!;

        Assert.Equal(
            sourceBefore.MediaClips,
            sourceAfter.MediaClips);
        Assert.Equal(
            TimelineTime.FromSeconds(60),
            sourceAfter.Duration);
        Assert.Equal(
            TimelineTime.FromSeconds(50),
            draftAfter.Duration);
        Assert.Equal(
            fixture.DraftSequenceId,
            result.GetProperty("sequence_id").GetGuid());

        var editLog = await fixture.Backend.InspectEditLogAsync(
            fixture.Context,
            CancellationToken.None);

        Assert.Equal(
            1,
            editLog.GetProperty("edit_count").GetInt32());
        var edit = Assert.Single(
            editLog.GetProperty("edits").EnumerateArray());
        Assert.Equal(
            "ripple_delete_range",
            edit.GetProperty("toolName").GetString());
    }

    [Fact]
    public async Task Multi_range_ripple_delete_uses_one_coordinate_state_and_preserves_source()
    {
        var fixture = CreateFixture();

        var result = await fixture.Backend.RippleDeleteRangesAsync(
            fixture.Context,
            new[]
            {
                new AgentTimelineRange(5, 10),
                new AgentTimelineRange(50, 55)
            },
            "Удалить два подтверждённых диапазона.",
            CancellationToken.None);

        var state = fixture.Session.State;
        var source = state.FindSequence(fixture.SourceSequenceId)!;
        var draft = state.FindSequence(fixture.DraftSequenceId)!;

        Assert.Equal(TimelineTime.FromSeconds(60), source.Duration);
        Assert.Equal(TimelineTime.FromSeconds(50), draft.Duration);
        Assert.Equal(
            10,
            result.GetProperty("removed_duration_seconds").GetDouble());

        var editLog = await fixture.Backend.InspectEditLogAsync(
            fixture.Context,
            CancellationToken.None);
        var edit = Assert.Single(
            editLog.GetProperty("edits").EnumerateArray());
        Assert.Equal(
            "ripple_delete_ranges",
            edit.GetProperty("toolName").GetString());
    }

    [Fact]
    public async Task Multi_range_delete_rejects_overlap_before_any_edit_is_applied()
    {
        var fixture = CreateFixture();

        var error = await Assert.ThrowsAsync<AgentToolRejectedException>(
            async () => await fixture.Backend.RippleDeleteRangesAsync(
                fixture.Context,
                new[]
                {
                    new AgentTimelineRange(5, 15),
                    new AgentTimelineRange(10, 20)
                },
                "Некорректные пересекающиеся диапазоны.",
                CancellationToken.None));

        Assert.Equal("overlapping_ranges", error.ErrorCode);
        Assert.Equal(
            TimelineTime.FromSeconds(60),
            fixture.Session.State.FindSequence(fixture.DraftSequenceId)!.Duration);

        var editLog = await fixture.Backend.InspectEditLogAsync(
            fixture.Context,
            CancellationToken.None);
        Assert.Equal(0, editLog.GetProperty("edit_count").GetInt32());
    }

    [Fact]
    public async Task Editing_backend_rejects_source_sequence_as_target()
    {
        var fixture = CreateFixture();
        var sourceContext = fixture.Context with
        {
            DraftSequenceId = fixture.SourceSequenceId
        };

        var error = await Assert.ThrowsAsync<AgentToolRejectedException>(
            async () => await fixture.Backend.RippleDeleteRangeAsync(
                sourceContext,
                5,
                10,
                "Нельзя менять исходник.",
                CancellationToken.None));

        Assert.Equal("draft_required", error.ErrorCode);
    }

    [Fact]
    public async Task Editing_backend_rejects_edit_when_agent_draft_is_not_active()
    {
        var fixture = CreateFixture();

        fixture.Session.Execute(
            new EditTransaction(
                "Open source",
                new ActivateSequenceCommand(
                    fixture.SourceSequenceId)));

        var error = await Assert.ThrowsAsync<AgentToolRejectedException>(
            async () => await fixture.Backend.SplitTimelineAsync(
                fixture.Context,
                10,
                "Проверка защиты.",
                CancellationToken.None));

        Assert.Equal("draft_not_active", error.ErrorCode);
    }

    [Fact]
    public async Task Editing_backend_is_bound_to_one_agent_task()
    {
        var fixture = CreateFixture();
        var wrongContext = fixture.Context with
        {
            TaskId = Guid.NewGuid()
        };

        var error = await Assert.ThrowsAsync<AgentToolRejectedException>(
            async () => await fixture.Backend.SplitTimelineAsync(
                wrongContext,
                10,
                "Проверка владельца.",
                CancellationToken.None));

        Assert.Equal(
            "editing_backend_task_mismatch",
            error.ErrorCode);
    }

    private static Fixture CreateFixture()
    {
        var project = ProjectState
            .CreateNew(
                "Agent edit backend",
                FrameRate.Fps30);
        var visualTrack = project.Tracks
            .First(item => item.Kind == TrackKind.Visual);
        var audioTrack = project.Tracks
            .First(item => item.Kind == TrackKind.Audio);
        var source = new MediaSource(
            Guid.NewGuid(),
            @"C:\media\episode.mp4",
            "episode.mp4",
            MediaKind.Video,
            TimelineTime.FromSeconds(60),
            true,
            1920,
            1080,
            FrameRate.Fps30);
        var linkGroupId = Guid.NewGuid();
        var video = new MediaClip(
            Guid.NewGuid(),
            source.Id,
            visualTrack.Id,
            TimelineTime.Zero,
            TimelineTime.Zero,
            TimelineTime.FromSeconds(60),
            linkGroupId,
            new VideoParameters());
        var audio = new MediaClip(
            Guid.NewGuid(),
            source.Id,
            audioTrack.Id,
            TimelineTime.Zero,
            TimelineTime.Zero,
            TimelineTime.FromSeconds(60),
            linkGroupId,
            null,
            new AudioParameters());

        project = project with
        {
            Sources = project.Sources.Add(
                source.Id,
                source),
            MediaClips = [video, audio]
        };
        project = project
            .EnsureSequenceContainer()
            .SynchronizeActiveSequence();

        var sourceSequence = project.ActiveSequence!;
        var draft = sourceSequence with
        {
            Id = Guid.NewGuid(),
            Name = "Agent Draft",
            Revision = 0,
            Status = SequenceStatus.Draft,
            ParentSequenceId = sourceSequence.Id,
            MontagePlanId = null
        };

        var session = new EditorSession(project);
        session.Execute(
            new EditTransaction(
                "Create Agent Draft",
                new CreateSequenceCommand(
                    draft,
                    Activate: true)));

        var taskId = Guid.NewGuid();
        var backend = new KadrAgentEditingToolBackend(
            () => session.State,
            (description, command) =>
            {
                var result = session.Execute(
                    new EditTransaction(
                        description,
                        command));
                return result.Changed;
            });
        backend.Reset(taskId);

        var context = new AgentToolContext(
            taskId,
            project.Id,
            sourceSequence.Id,
            draft.Id,
            AgentTaskPhase.Executing);

        return new Fixture(
            session,
            backend,
            context,
            sourceSequence.Id,
            draft.Id);
    }

    private sealed class EndToEndAgentModel :
        IAgentModel,
        IAgentTaskInterpreter,
        IAgentPlanCritic
    {
        private readonly Guid _sourceSequenceId;

        public EndToEndAgentModel(Guid sourceSequenceId)
        {
            _sourceSequenceId = sourceSequenceId;
        }

        public int UnderstandingCalls { get; private set; }
        public int PlanningCalls { get; private set; }
        public int CriticCalls { get; private set; }

        public ValueTask<AgentTaskUnderstanding> UnderstandAsync(
            AgentModelTurnRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UnderstandingCalls++;
            Assert.Contains("5–15", request.Task.UserRequest, StringComparison.Ordinal);
            return ValueTask.FromResult(new AgentTaskUnderstanding(
                AgentTaskBrief.Create(
                    AgentTaskKind.Edit,
                    "Удалить подтверждённый диапазон 5–15 секунд",
                    "Активная последовательность",
                    protectedElements: ["Всё вне диапазона 5–15 секунд"],
                    acceptanceCriteria: ["Source не изменён", "Agent Draft короче на 10 секунд"]),
                []));
        }

        public ValueTask<AgentModelDecision> DecideAsync(
            AgentModelTurnRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlanningCalls++;
            if (PlanningCalls == 1)
            {
                return ValueTask.FromResult(AgentModelDecision.UseTool(
                    "inspect_range",
                    AgentToolJson.ToElement(new
                    {
                        sequence_id = _sourceSequenceId,
                        start_seconds = 5,
                        end_seconds = 15,
                        detail = "frames"
                    }),
                    "Проверяю утверждённый диапазон."));
            }

            if (PlanningCalls == 2)
            {
                return ValueTask.FromResult(AgentModelDecision.PublishPlan(
                    AgentPlanDraft.Create(
                        "Удалить диапазон только в Agent Draft.",
                        "Одно точное утверждаемое действие и автоматическая проверка.",
                        ["Source не изменяется", "Всё вне 5–15 секунд сохраняется"],
                        [new AgentPlanStepDraft(
                            "Удалить диапазон",
                            "Применить ripple delete к доказанному диапазону.",
                            "ripple_delete_range",
                            [1],
                            AgentToolJson.ToElement(new
                            {
                                start_seconds = 5,
                                end_seconds = 15
                            }),
                            AgentEvidenceRequirement.Frames,
                            "Agent Draft становится короче на 10 секунд.",
                            ["Source не изменяется"],
                            ["Сверить edit log", "Проверить integrity"])]),
                    "План готов к утверждению."));
            }

            throw new InvalidOperationException("Deterministic execution must not ask the model for editing actions.");
        }

        public ValueTask<AgentPlanReview> ReviewPlanAsync(
            AgentPlanReviewRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CriticCalls++;
            Assert.Equal("ripple_delete_range", request.Plan.Steps[0].ExpectedEditingTool);
            return ValueTask.FromResult(new AgentPlanReview(
                true,
                "План точный и подтверждён evidence.",
                []));
        }
    }

    private sealed class EndToEndRangeEvidenceTool : IAgentTool
    {
        private readonly Guid _sequenceId;

        public EndToEndRangeEvidenceTool(Guid sequenceId)
        {
            _sequenceId = sequenceId;
        }

        public AgentToolDescriptor Descriptor { get; } = new(
            "inspect_range",
            "Return factual evidence for a requested timeline range.",
            AgentToolAccess.ReadOnly,
            AgentToolJson.ParseObject(
                """
                {"type":"object","properties":{"sequence_id":{"type":"string","format":"uuid"},"start_seconds":{"type":"number"},"end_seconds":{"type":"number"},"detail":{"type":"string"}},"required":["sequence_id","start_seconds","end_seconds","detail"],"additionalProperties":false}
                """));

        public ValueTask<AgentToolExecutionOutput> ExecuteAsync(
            AgentToolContext context,
            System.Text.Json.JsonElement arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(AgentToolExecutionOutput.From(
                "Range has exact visual evidence.",
                new
                {
                    channel = "frames",
                    sequence_id = _sequenceId,
                    sequence_revision = 0,
                    detail = "frames",
                    start_seconds = 5,
                    end_seconds = 15,
                    truncated = false,
                    vision = new
                    {
                        observations = new[]
                        {
                            new { timestamp_seconds = 5, fact = "left boundary" },
                            new { timestamp_seconds = 15, fact = "right boundary" }
                        }
                    }
                }));
        }
    }

    private sealed class EndToEndVerificationTool : IAgentTool
    {
        public EndToEndVerificationTool(string name)
        {
            Descriptor = new AgentToolDescriptor(
                name,
                "Return deterministic verification facts.",
                AgentToolAccess.ReadOnly,
                AgentToolJson.ParseObject(
                    """
                    {"type":"object","additionalProperties":true}
                    """));
        }

        public AgentToolDescriptor Descriptor { get; }

        public ValueTask<AgentToolExecutionOutput> ExecuteAsync(
            AgentToolContext context,
            System.Text.Json.JsonElement arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = Descriptor.Name == "inspect_timeline_integrity"
                ? AgentToolJson.ToElement(new
                {
                    channel = "integrity",
                    sequence_id = context.DraftSequenceId,
                    sequence_revision = 1,
                    gap_count = 0,
                    overlap_count = 0,
                    link_issue_count = 0
                })
                : AgentToolJson.ToElement(new
                {
                    channel = "sequence_diff",
                    source_sequence_id = context.SourceSequenceId,
                    draft_sequence_id = context.DraftSequenceId,
                    source_revision = 0,
                    unapproved_change_count = 0
                });
            return ValueTask.FromResult(AgentToolExecutionOutput.From(
                "Verification succeeded.",
                data));
        }
    }

    private sealed record Fixture(
        EditorSession Session,
        KadrAgentEditingToolBackend Backend,
        AgentToolContext Context,
        Guid SourceSequenceId,
        Guid DraftSequenceId);
}
