using KadrStudio.Application.Automation.Agent;
using KadrStudio.Application.Automation.Agent.Tools;
using KadrStudio.Application.Automation.Agent.Tools.Editing;
using KadrStudio.Application.Editing;
using KadrStudio.Core.Domain;
using KadrStudio.Services.Agent;

namespace KadrStudio.UiAdapters.Tests;

public sealed class KadrAgentEditingToolBackendTests
{
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

    private sealed record Fixture(
        EditorSession Session,
        KadrAgentEditingToolBackend Backend,
        AgentToolContext Context,
        Guid SourceSequenceId,
        Guid DraftSequenceId);
}
