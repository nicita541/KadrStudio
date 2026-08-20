using System.Collections.Immutable;
using KadrStudio.Application.Editing;
using KadrStudio.Core.Domain;

namespace KadrStudio.Core.Tests;

public sealed class EditorSessionTests
{
    [Fact]
    public void Metadata_only_chat_update_is_saved_without_undo_or_sequence_revision_change()
    {
        var fixture = CreateLinkedProject();
        var project = fixture.Project.EnsureSequenceContainer();
        var session = new EditorSession(project);
        var sequenceRevision = session.State.ActiveSequence!.Revision;
        var conversation = session.State.AiConversation with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            Messages =
            [
                new AiChatMessage(Guid.NewGuid(), AiChatRole.User, AiChatMessageKind.Text,
                    "Собери монтаж", DateTimeOffset.UtcNow)
            ]
        };

        var result = session.Execute(new EditTransaction(
            "chat", [new ReplaceAiConversationCommand(conversation)],
            RecordInHistory: false, SynchronizeActiveSequence: false));

        Assert.True(result.Changed);
        Assert.False(session.CanUndo);
        Assert.Equal(sequenceRevision, session.State.ActiveSequence!.Revision);
        Assert.Single(session.State.AiConversation.Messages);
        Assert.False(result.Changes.InvalidatesVideo);
        Assert.False(result.Changes.InvalidatesAudio);
        Assert.False(result.Changes.InvalidatesOverlay);
    }

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
    public void Razor_split_of_selected_clip_splits_the_linked_pair()
    {
        var fixture = CreateLinkedProject();
        var session = new EditorSession(fixture.Project);

        session.Execute(new EditTransaction(
            "razor",
            new SplitSelectedMediaClipCommand(
                fixture.VideoClip.Id,
                TimelineTime.FromSeconds(7))));

        Assert.Equal(4, session.State.MediaClips.Length);
        Assert.All(
            session.State.MediaClips.GroupBy(clip => clip.LinkGroupId),
            group => Assert.Equal(2, group.Count()));
    }

    [Fact]
    public void Alt_razor_unlinks_original_group_and_splits_only_hovered_clip()
    {
        var fixture = CreateLinkedProject();
        var session = new EditorSession(fixture.Project);

        session.Execute(new EditTransaction(
            "alt razor",
            new SplitSelectedMediaClipCommand(
                fixture.VideoClip.Id,
                TimelineTime.FromSeconds(7),
                IncludeLinked: false)));

        Assert.Equal(3, session.State.MediaClips.Length);
        Assert.All(session.State.MediaClips, clip => Assert.Null(clip.LinkGroupId));
        Assert.Single(session.State.MediaClips.Where(clip => clip.TrackId == fixture.AudioTrack.Id));
        Assert.Equal(2, session.State.MediaClips.Count(clip => clip.TrackId == fixture.VideoTrack.Id));
    }

    [Fact]
    public void Ripple_delete_selected_link_group_closes_the_right_hand_gap()
    {
        var fixture = CreateLinkedProject();
        var secondGroup = Guid.NewGuid();
        var rightVideo = fixture.VideoClip with
        {
            Id = Guid.NewGuid(),
            Start = fixture.VideoClip.End,
            SourceIn = TimelineTime.FromSeconds(10),
            Duration = TimelineTime.FromSeconds(5),
            LinkGroupId = secondGroup
        };
        var rightAudio = fixture.AudioClip with
        {
            Id = Guid.NewGuid(),
            Start = fixture.AudioClip.End,
            SourceIn = TimelineTime.FromSeconds(10),
            Duration = TimelineTime.FromSeconds(5),
            LinkGroupId = secondGroup
        };
        var session = new EditorSession(fixture.Project with
        {
            MediaClips = fixture.Project.MediaClips.Concat([rightVideo, rightAudio]).ToImmutableArray()
        });

        session.Execute(new EditTransaction(
            "ripple selected",
            new RippleDeleteSelectedMediaClipCommand(fixture.VideoClip.Id)));

        Assert.Equal(2, session.State.MediaClips.Length);
        Assert.All(session.State.MediaClips, clip => Assert.Equal(TimelineTime.FromSeconds(2), clip.Start));
        Assert.All(session.State.MediaClips, clip => Assert.Equal(secondGroup, clip.LinkGroupId));
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
    public void Track_properties_are_command_driven_undoable_and_invalidate_only_their_pipeline()
    {
        var fixture = CreateLinkedProject();
        var session = new EditorSession(fixture.Project);
        var updated = fixture.AudioTrack with
        {
            Name = "Dialogue",
            IsMuted = true,
            IsLocked = true,
            IsVisible = false
        };

        var result = session.Execute(new EditTransaction(
            "audio track properties", new UpdateTrackCommand(updated)));

        Assert.Equal(updated, session.State.FindTrack(updated.Id));
        Assert.True(result.Changes.InvalidatesAudio);
        Assert.False(result.Changes.InvalidatesVideo);
        Assert.False(result.Changes.InvalidatesOverlay);
        Assert.True(session.Undo());
        Assert.Equal(fixture.AudioTrack, session.State.FindTrack(updated.Id));
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
    public void Text_split_batch_is_atomic_and_undoes_as_one_revision()
    {
        var fixture = CreateLinkedProject();
        var textTrack = fixture.Project.Tracks.Single(item => item.Kind == TrackKind.Text);
        var original = new TextClip(
            Guid.NewGuid(), textTrack.Id, TimelineTime.FromSeconds(2), TimelineTime.FromSeconds(6),
            "multiline\ncaption", new TextStyle());
        var initial = fixture.Project with { TextClips = [original] };
        var session = new EditorSession(initial);
        var cut = TimelineTime.FromSeconds(5);
        var right = original with
        {
            Id = Guid.NewGuid(), Start = cut, Duration = original.End - cut
        };

        session.Execute(new EditTransaction("split text", new EditBatchCommand("split text", [
            new UpsertTextClipCommand(original with { Duration = cut - original.Start }),
            new AddTextClipsCommand([right])
        ])));

        Assert.Equal(2, session.State.TextClips.Length);
        Assert.Equal(cut, session.State.TextClips.OrderBy(item => item.Start).Last().Start);
        Assert.True(session.Undo());
        Assert.Equal(original, Assert.Single(session.State.TextClips));
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

    [Fact]
    public void Five_hundred_edits_undo_and_redo_keep_a_single_valid_project_history()
    {
        var fixture = CreateLinkedProject();
        var session = new EditorSession(fixture.Project);
        for (var index = 0; index < 500; index++)
        {
            var current = session.State.FindMediaClip(fixture.AudioClip.Id)!;
            var changed = current with
            {
                Audio = current.Audio! with { Volume = (index + 1) / 501d }
            };
            var result = session.Execute(new EditTransaction(
                $"gain {index}", new UpsertMediaClipCommand(changed)));
            Assert.True(result.Changes.InvalidatesAudio);
            Assert.False(result.Changes.InvalidatesVideo);
        }

        for (var index = 0; index < 500; index++) Assert.True(session.Undo());
        Assert.Equal(fixture.Project, session.State);
        for (var index = 0; index < 500; index++) Assert.True(session.Redo());
        Assert.Equal(500, session.State.Revision - fixture.Project.Revision);
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
