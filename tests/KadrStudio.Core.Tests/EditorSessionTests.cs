using System.Collections.Immutable;
using KadrStudio.Application.Editing;
using KadrStudio.Core.Domain;

namespace KadrStudio.Core.Tests;

public sealed class EditorSessionTests
{
    [Fact]
    public void Invalid_transaction_is_atomic()
    {
        var fixture = CreateLinkedProject();
        var session = new EditorSession(fixture.Project);
        var before = session.State;
        var invalid = fixture.VideoClip with { Start = TimelineTime.FromSeconds(-1) };

        Assert.Throws<EditRejectedException>(() => session.Execute(
            new EditTransaction("invalid", new AddMediaClipsCommand([invalid]))));
        Assert.Same(before, session.State);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void Linked_move_is_one_undoable_transaction()
    {
        var fixture = CreateLinkedProject();
        var session = new EditorSession(fixture.Project);

        session.Execute(new EditTransaction("move",
            new MoveMediaClipCommand(fixture.VideoClip.Id, fixture.VideoTrack.Id, TimelineTime.FromSeconds(8))));

        Assert.All(session.State.MediaClips, clip => Assert.Equal(TimelineTime.FromSeconds(8), clip.Start));
        Assert.True(session.Undo());
        Assert.All(session.State.MediaClips, clip => Assert.Equal(TimelineTime.FromSeconds(2), clip.Start));
        Assert.True(session.Redo());
        Assert.All(session.State.MediaClips, clip => Assert.Equal(TimelineTime.FromSeconds(8), clip.Start));
    }

    [Fact]
    public void Split_preserves_linked_pairs_on_both_sides()
    {
        var fixture = CreateLinkedProject();
        var session = new EditorSession(fixture.Project);

        session.Execute(new EditTransaction("split",
            new SplitMediaClipsCommand(TimelineTime.FromSeconds(7))));

        Assert.Equal(4, session.State.MediaClips.Length);
        var groups = session.State.MediaClips.GroupBy(item => item.LinkGroupId).ToArray();
        Assert.Equal(2, groups.Length);
        Assert.All(groups, group => Assert.Equal(2, group.Count()));
        Assert.All(session.State.MediaClips.Where(item => item.Start == TimelineTime.FromSeconds(2)),
            item => Assert.Equal(TimelineTime.FromSeconds(5), item.Duration));
        Assert.All(session.State.MediaClips.Where(item => item.Start == TimelineTime.FromSeconds(7)), item =>
        {
            Assert.Equal(TimelineTime.FromSeconds(5), item.SourceIn);
            Assert.Equal(TimelineTime.FromSeconds(5), item.Duration);
        });
    }

    [Fact]
    public void Ripple_delete_updates_media_text_markers_and_in_out_together()
    {
        var fixture = CreateLinkedProject(startSeconds: 0, durationSeconds: 20);
        var textTrack = fixture.Project.Tracks.Single(item => item.Kind == TrackKind.Text);
        var project = fixture.Project with
        {
            TextClips = [new TextClip(Guid.NewGuid(), textTrack.Id, TimelineTime.FromSeconds(3), TimelineTime.FromSeconds(10), "hello", new TextStyle())],
            Markers = [new TimelineMarker(Guid.NewGuid(), MarkerKind.Note, TimelineTime.FromSeconds(12), TimelineTime.FromSeconds(2), "note")],
            InPoint = TimelineTime.FromSeconds(2),
            OutPoint = TimelineTime.FromSeconds(18)
        };
        var session = new EditorSession(project);

        session.Execute(new EditTransaction("ripple",
            new RippleDeleteRangeCommand(new TimeRange(TimelineTime.FromSeconds(5), TimelineTime.FromSeconds(4)))));

        Assert.Equal(4, session.State.MediaClips.Length);
        Assert.All(session.State.MediaClips.GroupBy(item => item.TrackId), trackClips =>
            Assert.Equal(TimelineTime.FromSeconds(16), trackClips.Max(item => item.End)));
        Assert.Equal(2, session.State.MediaClips.GroupBy(item => item.LinkGroupId).Count());
        Assert.Equal(TimelineTime.FromSeconds(6), session.State.TextClips.Single().Duration);
        Assert.Equal(TimelineTime.FromSeconds(8), session.State.Markers.Single().Start);
        Assert.Equal(TimelineTime.FromSeconds(2), session.State.InPoint);
        Assert.Equal(TimelineTime.FromSeconds(14), session.State.OutPoint);
    }

    [Fact]
    public void Overlapping_clips_are_rejected_by_shared_validator()
    {
        var fixture = CreateLinkedProject();
        var second = fixture.VideoClip with
        {
            Id = Guid.NewGuid(),
            LinkGroupId = null,
            Start = TimelineTime.FromSeconds(4),
            Duration = TimelineTime.FromSeconds(4)
        };
        var session = new EditorSession(fixture.Project);

        var exception = Assert.Throws<EditRejectedException>(() => session.Execute(
            new EditTransaction("overlap", new AddMediaClipsCommand([second]))));

        Assert.Contains(exception.Errors, item => item.Code == "clip.overlap");
    }

    [Fact]
    public void Audio_only_edit_invalidates_audio_but_not_video_or_overlay()
    {
        var fixture = CreateLinkedProject();
        var session = new EditorSession(fixture.Project);
        var changedAudio = fixture.AudioClip with
        {
            Audio = fixture.AudioClip.Audio! with { Volume = 0.25 }
        };

        var result = session.Execute(new EditTransaction(
            "audio gain",
            new DeleteMediaClipsCommand(new HashSet<Guid> { fixture.AudioClip.Id }, IncludeLinked: false),
            new AddMediaClipsCommand([changedAudio])));

        Assert.True(result.Changes.InvalidatesAudio);
        Assert.False(result.Changes.InvalidatesVideo);
        Assert.False(result.Changes.InvalidatesOverlay);
        Assert.Equal(fixture.AudioClip.Range, Assert.Single(result.Changes.AudioRanges));
    }

    [Fact]
    public void Text_edit_invalidates_overlay_without_restarting_media_pipelines()
    {
        var fixture = CreateLinkedProject();
        var textTrack = fixture.Project.Tracks.Single(item => item.Kind == TrackKind.Text);
        var text = new TextClip(
            Guid.NewGuid(), textTrack.Id, TimelineTime.FromSeconds(3), TimelineTime.FromSeconds(2),
            "caption", new TextStyle());
        var session = new EditorSession(fixture.Project);

        var result = session.Execute(new EditTransaction("add text", new UpsertTextClipCommand(text)));

        Assert.True(result.Changes.InvalidatesOverlay);
        Assert.False(result.Changes.InvalidatesVideo);
        Assert.False(result.Changes.InvalidatesAudio);
        Assert.Equal(text.Range, Assert.Single(result.Changes.OverlayRanges));
    }

    [Fact]
    public void Undo_reports_the_same_pipeline_range_as_the_original_edit()
    {
        var fixture = CreateLinkedProject();
        var session = new EditorSession(fixture.Project);
        ProjectChangeSet? undoChanges = null;
        session.StateChanged += (_, args) =>
        {
            if (args.IsUndoOrRedo) undoChanges = args.Changes;
        };
        session.Execute(new EditTransaction(
            "video color",
            new DeleteMediaClipsCommand(new HashSet<Guid> { fixture.VideoClip.Id }, IncludeLinked: false),
            new AddMediaClipsCommand([fixture.VideoClip with
            {
                Video = fixture.VideoClip.Video! with { Saturation = 0.5 }
            }])));

        Assert.True(session.Undo());

        Assert.NotNull(undoChanges);
        Assert.True(undoChanges.InvalidatesVideo);
        Assert.False(undoChanges.InvalidatesAudio);
        Assert.Equal(fixture.VideoClip.Range, Assert.Single(undoChanges.VideoRanges));
    }

    [Fact]
    public void Video_transition_invalidates_only_its_video_range()
    {
        var project = ProjectState.CreateNew();
        var source = new MediaSource(
            Guid.NewGuid(), "F:\\media\\episode.mkv", "episode.mkv", MediaKind.Video,
            TimelineTime.FromSeconds(60), false);
        project = project with { Sources = project.Sources.Add(source.Id, source) };
        var track = project.Tracks.Single(item => item.Kind == TrackKind.Visual && item.Index == 0);
        var first = new MediaClip(Guid.NewGuid(), source.Id, track.Id, TimelineTime.Zero, TimelineTime.Zero,
            TimelineTime.FromSeconds(5), Video: new VideoParameters());
        var second = new MediaClip(Guid.NewGuid(), source.Id, track.Id, TimelineTime.FromSeconds(5), TimelineTime.FromSeconds(5),
            TimelineTime.FromSeconds(5), Video: new VideoParameters());
        project = project with { MediaClips = [first, second] };
        var transition = new TimelineTransition(
            Guid.NewGuid(), TransitionKind.CrossDissolve, track.Id, first.Id, second.Id,
            TimelineTime.FromSeconds(4.5), TimelineTime.FromSeconds(1));
        var session = new EditorSession(project);

        var result = session.Execute(new EditTransaction("transition", new UpsertTransitionCommand(transition)));

        Assert.True(result.Changes.InvalidatesVideo);
        Assert.False(result.Changes.InvalidatesAudio);
        Assert.Equal(transition.Range, Assert.Single(result.Changes.VideoRanges));
    }

    [Fact]
    public void Editing_a_transition_endpoint_removes_transition_instead_of_corrupting_project()
    {
        var (project, transition, first) = CreateProjectWithTransition();
        var session = new EditorSession(project);

        var result = session.Execute(new EditTransaction(
            "trim endpoint",
            new TrimMediaClipCommand(first.Id, TrimEdge.Right, TimelineTime.FromSeconds(4))));

        Assert.Empty(result.State.Transitions);
        Assert.Contains(result.Changes.VideoRanges, range =>
            range.Start <= transition.Start && range.End >= transition.End);
    }

    [Fact]
    public void Rollback_latest_transaction_restores_state_without_creating_redo_branch()
    {
        var fixture = CreateLinkedProject();
        var session = new EditorSession(fixture.Project);
        session.Execute(new EditTransaction("draft", new SplitMediaClipsCommand(TimelineTime.FromSeconds(7))));

        Assert.True(session.RollbackLatestTransaction());

        Assert.Equal(fixture.Project, session.State);
        Assert.False(session.CanUndo);
        Assert.False(session.CanRedo);
    }

    private static (ProjectState Project, TimelineTransition Transition, MediaClip First) CreateProjectWithTransition()
    {
        var project = ProjectState.CreateNew();
        var source = new MediaSource(
            Guid.NewGuid(), "F:\\media\\episode.mkv", "episode.mkv", MediaKind.Video,
            TimelineTime.FromSeconds(60), false);
        project = project with { Sources = project.Sources.Add(source.Id, source) };
        var track = project.Tracks.Single(item => item.Kind == TrackKind.Visual && item.Index == 0);
        var first = new MediaClip(Guid.NewGuid(), source.Id, track.Id, TimelineTime.Zero, TimelineTime.Zero,
            TimelineTime.FromSeconds(5), Video: new VideoParameters());
        var second = new MediaClip(Guid.NewGuid(), source.Id, track.Id, TimelineTime.FromSeconds(5), TimelineTime.FromSeconds(5),
            TimelineTime.FromSeconds(5), Video: new VideoParameters());
        var transition = new TimelineTransition(
            Guid.NewGuid(), TransitionKind.CrossDissolve, track.Id, first.Id, second.Id,
            TimelineTime.FromSeconds(4.5), TimelineTime.FromSeconds(1));
        return (project with { MediaClips = [first, second], Transitions = [transition] }, transition, first);
    }

    private static Fixture CreateLinkedProject(double startSeconds = 2, double durationSeconds = 10)
    {
        var project = ProjectState.CreateNew();
        var source = new MediaSource(
            Guid.NewGuid(), "F:\\media\\episode.mkv", "episode.mkv", MediaKind.Video,
            TimelineTime.FromSeconds(60), true, 1920, 1080, FrameRate.Fps23976);
        project = project with { Sources = ImmutableDictionary<Guid, MediaSource>.Empty.Add(source.Id, source) };
        var videoTrack = project.Tracks.Single(item => item.Kind == TrackKind.Visual && item.Index == 0);
        var audioTrack = project.Tracks.Single(item => item.Kind == TrackKind.Audio && item.Index == 0);
        var group = Guid.NewGuid();
        var video = new MediaClip(
            Guid.NewGuid(), source.Id, videoTrack.Id, TimelineTime.FromSeconds(startSeconds), TimelineTime.Zero,
            TimelineTime.FromSeconds(durationSeconds), group, new VideoParameters(), null);
        var audio = new MediaClip(
            Guid.NewGuid(), source.Id, audioTrack.Id, TimelineTime.FromSeconds(startSeconds), TimelineTime.Zero,
            TimelineTime.FromSeconds(durationSeconds), group, null, new AudioParameters());
        project = project with { MediaClips = [video, audio] };
        return new Fixture(project, source, videoTrack, audioTrack, video, audio);
    }

    private sealed record Fixture(
        ProjectState Project,
        MediaSource Source,
        TimelineTrack VideoTrack,
        TimelineTrack AudioTrack,
        MediaClip VideoClip,
        MediaClip AudioClip);
}
