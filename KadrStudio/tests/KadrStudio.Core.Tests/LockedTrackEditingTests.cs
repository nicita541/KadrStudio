using KadrStudio.Application.Editing;
using KadrStudio.Core.Domain;

namespace KadrStudio.Core.Tests;

public sealed class LockedTrackEditingTests
{
    [Fact]
    public void Initializing_sequence_container_preserves_locked_timeline()
    {
        var project = LinkedSubtitleEditingTests.CreateProject(new FrameRate(24, 1));
        project = new UpdateTrackCommand(project.Tracks[0] with { IsLocked = true }).Apply(project);
        var session = new EditorSession(project);
        session.Execute(new EditTransaction("initialize", new InitializeSequenceWorkspaceCommand()));
        Assert.Equal(project.MediaClips, session.State.MediaClips);
        Assert.NotNull(session.State.ActiveSequence);
    }

    [Theory]
    [InlineData("move")]
    [InlineData("trim")]
    [InlineData("split")]
    [InlineData("delete")]
    [InlineData("unlink")]
    [InlineData("ripple")]
    public void Locked_linked_subtitle_rejects_entire_edit(string operation)
    {
        var project = LinkedSubtitleEditingTests.CreateProject(new FrameRate(24000, 1001));
        var track = project.FindTrack(project.SubtitleClips[0].TrackId)!;
        project = new UpdateTrackCommand(track with { IsLocked = true }).Apply(project);
        var clip = project.MediaClips[0];
        var position = TimelineTime.FromFrames(30, project.FrameRate);
        IEditCommand command = operation switch
        {
            "move" => new MoveMediaClipCommand(clip.Id, clip.TrackId, position),
            "trim" => new TrimMediaClipCommand(clip.Id, TrimEdge.Left, position),
            "split" => new SplitSelectedMediaClipCommand(clip.Id, position),
            "delete" => new DeleteMediaClipsCommand(new HashSet<Guid> { clip.Id }),
            "unlink" => new UnlinkMediaClipCommand(clip.Id),
            _ => new RippleDeleteSelectedMediaClipCommand(clip.Id)
        };
        var session = new EditorSession(project);
        Assert.Throws<EditRejectedException>(() => session.Execute(new EditTransaction(operation, command)));
        Assert.Same(project, session.State);
        Assert.False(session.CanUndo);
        session.Execute(new EditTransaction("unlock", new UpdateTrackCommand(track)));
        Assert.True(session.Execute(new EditTransaction(operation, command)).Changed);
    }
}
