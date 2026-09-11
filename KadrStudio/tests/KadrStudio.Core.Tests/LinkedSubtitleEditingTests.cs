using System.Collections.Immutable;
using KadrStudio.Application.Editing;
using KadrStudio.Core.Domain;

namespace KadrStudio.Core.Tests;

public sealed class LinkedSubtitleEditingTests
{
    [Fact]
    public void Video_and_subtitle_without_audio_form_a_valid_linked_group()
    {
        var project = CreateProject(new FrameRate(24000, 1001));
        project = project with { MediaClips = [project.MediaClips[0]] };
        var session = new EditorSession(project);
        session.Execute(new EditTransaction("split", new SplitSelectedMediaClipCommand(project.MediaClips[0].Id,
            TimelineTime.FromFrames(30, project.FrameRate))));
        Assert.Equal(2, session.State.SubtitleClips.Length);
        Assert.All(session.State.SubtitleClips, subtitle =>
            Assert.Equal(session.State.MediaClips.Single(clip => clip.Start == subtitle.Start).LinkGroupId, subtitle.LinkGroupId));
    }

    [Theory]
    [InlineData(24000)]
    [InlineData(30000)]
    [InlineData(60000)]
    public void Linked_subtitles_follow_move_trim_split_delete_and_unlink_at_fractional_fps(int numerator)
    {
        var project = CreateProject(new FrameRate(numerator, 1001));
        var clip = project.MediaClips[0];
        var frame = TimelineTime.FromFrames(1, project.FrameRate);
        var split = TimelineTime.FromFrames(30, project.FrameRate);
        foreach (var operation in new[] { "move", "trim-left", "trim-right", "split", "split-all", "delete", "unlink", "ripple" })
        {
            var session = new EditorSession(project);
            IEditCommand command = operation switch
            {
                "move" => new MoveMediaClipCommand(clip.Id, clip.TrackId, split),
                "trim-left" => new TrimMediaClipCommand(clip.Id, TrimEdge.Left, frame),
                "trim-right" => new TrimMediaClipCommand(clip.Id, TrimEdge.Right, clip.End - frame),
                "split" => new SplitSelectedMediaClipCommand(clip.Id, split),
                "split-all" => new SplitMediaClipsCommand(split),
                "delete" => new DeleteMediaClipsCommand(new HashSet<Guid> { clip.Id }),
                "unlink" => new UnlinkMediaClipCommand(clip.Id),
                _ => new RippleDeleteSelectedMediaClipCommand(clip.Id)
            };
            session.Execute(new EditTransaction(operation, command));
            var subtitles = session.State.SubtitleClips.OrderBy(item => item.Start).ToArray();
            switch (operation)
            {
                case "move": Assert.Equal(split, Assert.Single(subtitles).Start); break;
                case "trim-left":
                    Assert.Equal(frame, Assert.Single(subtitles).SourceIn);
                    Assert.Equal(frame, subtitles[0].Start);
                    Assert.Equal(clip.Duration - frame, subtitles[0].Duration);
                    break;
                case "trim-right": Assert.Equal(clip.Duration - frame, Assert.Single(subtitles).Duration); break;
                case "delete": case "ripple": Assert.Empty(subtitles); break;
                case "unlink": Assert.Null(Assert.Single(subtitles).LinkGroupId); break;
                default:
                    Assert.Equal(2, subtitles.Length);
                    Assert.Equal(split, subtitles[0].Duration);
                    Assert.Equal(split, subtitles[1].Start);
                    Assert.Equal(split, subtitles[1].SourceIn);
                    Assert.Equal(session.State.MediaClips.First(item => item.Start == split).LinkGroupId, subtitles[1].LinkGroupId);
                    Assert.NotNull(subtitles[1].LinkGroupId);
                    break;
            }
            Assert.True(session.Undo());
            Assert.Equal(project.SubtitleClips, session.State.SubtitleClips);
            Assert.True(session.Redo());
            Assert.Equal(subtitles, session.State.SubtitleClips.OrderBy(item => item.Start));
        }
    }

    internal static ProjectState CreateProject(FrameRate fps)
    {
        var project = ProjectState.CreateNew("linked subtitles", fps);
        var track = project.Tracks.First(item => item.Kind == TrackKind.Visual);
        var subtitleTrack = project.Tracks.First(item => item.Kind == TrackKind.Subtitle);
        var audioTrack = project.Tracks.First(item => item.Kind == TrackKind.Audio);
        var duration = TimelineTime.FromFrames(120, fps);
        var source = new MediaSource(Guid.NewGuid(), "F:/fixtures/subtitles.mkv", "subtitles.mkv", MediaKind.Video,
            TimelineTime.FromSeconds(60), true, Streams:
            [new MediaStreamDescriptor(0, MediaStreamKind.Video, "h264"), new MediaStreamDescriptor(1, MediaStreamKind.Subtitle, "ass"),
                new MediaStreamDescriptor(2, MediaStreamKind.Audio, "aac", SampleRate: 48000, Channels: 2)]);
        var group = Guid.NewGuid();
        return project with
        {
            Sources = project.Sources.Add(source.Id, source),
            MediaClips = [new MediaClip(Guid.NewGuid(), source.Id, track.Id, TimelineTime.Zero, TimelineTime.Zero,
                duration, group, Video: new VideoParameters()),
                new MediaClip(Guid.NewGuid(), source.Id, audioTrack.Id, TimelineTime.Zero, TimelineTime.Zero,
                    duration, group, Audio: new AudioParameters(), StreamIndex: 2)],
            SubtitleClips = [new SubtitleClip(Guid.NewGuid(), source.Id, 1, subtitleTrack.Id,
                TimelineTime.Zero, TimelineTime.Zero, duration, group)]
        };
    }
}
