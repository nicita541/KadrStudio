using KadrStudio.Adapters;
using KadrStudio.Models;
using KadrStudio.Services;
using CoreTrackKind = KadrStudio.Core.Domain.TrackKind;
using CoreFrameRate = KadrStudio.Core.Domain.FrameRate;
using UiTrackKind = KadrStudio.Models.TrackKind;
using System.Collections.Immutable;

namespace KadrStudio.UiAdapters.Tests;

public sealed class EditorProjectMapperTests
{
    [Fact]
    public void Mutable_ui_project_roundtrips_through_immutable_core()
    {
        var sourceId = Guid.NewGuid();
        var link = Guid.NewGuid();
        var project = EditorProject.CreateNew();
        project.Name = "Mapper";
        project.Media.Add(new MediaAsset
        {
            Id = sourceId, Path = "F:\\media\\episode.mkv", Name = "episode.mkv",
            Kind = MediaKind.Video, Duration = 120, HasAudio = true, Width = 1920,
            Height = 1080, FrameRate = 23.976, VideoCodec = "hevc", AudioCodec = "aac"
        });
        project.Clips.Add(new TimelineClip
        {
            AssetId = sourceId, Track = UiTrackKind.Visual, TrackIndex = 0,
            Start = 3, SourceStart = 5, Duration = 20, LinkGroupId = link, Brightness = 0.1
        });
        project.Clips.Add(new TimelineClip
        {
            AssetId = sourceId, Track = UiTrackKind.Audio, TrackIndex = 0,
            Start = 3, SourceStart = 5, Duration = 20, LinkGroupId = link, Volume = 0.8, Pan = -0.2
        });
        project.TextOverlays.Add(new TextOverlay { Text = "Line 1\nLine 2", Start = 4, Duration = 3 });

        var mapper = new EditorProjectMapper();
        var core = mapper.ToCore(project, revision: 7);
        var restored = mapper.ToUi(core, "F:\\project.kadr");

        Assert.Equal(7, core.Revision);
        Assert.Equal(2, core.Tracks.Count(item => item.Kind == CoreTrackKind.Visual));
        Assert.Equal(2, core.Tracks.Count(item => item.Kind == CoreTrackKind.Audio));
        Assert.Single(core.Tracks.Where(item => item.Kind == CoreTrackKind.Text));
        Assert.Equal(2, restored.Clips.Count);
        Assert.Single(restored.TextOverlays);
        Assert.Equal("Line 1\nLine 2", restored.TextOverlays[0].Text);
        Assert.Equal("F:\\project.kadr", restored.FilePath);
        Assert.Equal(0.8, restored.Clips.Single(item => item.Track == UiTrackKind.Audio).Volume);
    }

    [Theory]
    [InlineData(24000, 1001)]
    [InlineData(30000, 1001)]
    [InlineData(60000, 1001)]
    public void Adapter_preserves_exact_fractional_sequence_timebase(int numerator, int denominator)
    {
        var project = EditorProject.CreateNew();
        project.FrameRateValue = new CoreFrameRate(numerator, denominator);
        var mapper = new EditorProjectMapper();

        var restored = mapper.ToUi(mapper.ToCore(project));

        Assert.Equal(new CoreFrameRate(numerator, denominator), restored.FrameRateValue);
    }

    [Fact]
    public void Adapter_preserves_track_identity_order_names_and_flags()
    {
        var id = Guid.NewGuid();
        var project = KadrStudio.Core.Domain.ProjectState.CreateNew("tracks") with
        {
            Tracks =
            [
                new(id, CoreTrackKind.Visual, 0, "Main picture", IsMuted: true, IsLocked: true, IsVisible: false),
                new(Guid.NewGuid(), CoreTrackKind.Visual, 1, "Overlay"),
                new(Guid.NewGuid(), CoreTrackKind.Audio, 0, "Dialogue", IsMuted: true),
                new(Guid.NewGuid(), CoreTrackKind.Audio, 1, "Music", IsLocked: true),
                new(Guid.NewGuid(), CoreTrackKind.Text, 0, "Subtitles", IsVisible: false)
            ]
        };
        var mapper = new EditorProjectMapper();

        var restored = mapper.ToCore(mapper.ToUi(project));

        Assert.Equal(project.Tracks.ToArray(), restored.Tracks.ToArray());
        Assert.Equal(id, restored.Tracks[0].Id);
    }

    [Fact]
    public void Adapter_preserves_v3_media_transition_and_transform_fields()
    {
        var project = KadrStudio.Core.Domain.ProjectState.CreateNew("v3", CoreFrameRate.Fps2997);
        var source = new KadrStudio.Core.Domain.MediaSource(
            Guid.NewGuid(), "F:\\media\\vfr.mkv", "vfr.mkv", KadrStudio.Core.Domain.MediaKind.Video,
            KadrStudio.Core.Domain.TimelineTime.FromSeconds(20), false,
            FastFingerprint: "fast", VerifiedFingerprint: "verified",
            Streams: ImmutableArray.Create(new KadrStudio.Core.Domain.MediaStreamDescriptor(
                0, KadrStudio.Core.Domain.MediaStreamKind.Video, "hevc", IsVariableFrameRate: true)),
            IsVariableFrameRate: true);
        var track = project.Tracks.Single(item => item.Kind == CoreTrackKind.Visual && item.Index == 0);
        var first = new KadrStudio.Core.Domain.MediaClip(
            Guid.NewGuid(), source.Id, track.Id, KadrStudio.Core.Domain.TimelineTime.Zero,
            KadrStudio.Core.Domain.TimelineTime.Zero, KadrStudio.Core.Domain.TimelineTime.FromSeconds(5),
            Video: new KadrStudio.Core.Domain.VideoParameters(
                PositionX: 0.4, PositionY: 0.6, ScaleX: 1.2, ScaleY: 0.8, Rotation: 12,
                CropLeft: 0.1, CropRight: 0.05, Opacity: 0.75));
        var second = new KadrStudio.Core.Domain.MediaClip(
            Guid.NewGuid(), source.Id, track.Id, KadrStudio.Core.Domain.TimelineTime.FromSeconds(5),
            KadrStudio.Core.Domain.TimelineTime.FromSeconds(5), KadrStudio.Core.Domain.TimelineTime.FromSeconds(5),
            Video: new KadrStudio.Core.Domain.VideoParameters());
        var transition = new KadrStudio.Core.Domain.TimelineTransition(
            Guid.NewGuid(), KadrStudio.Core.Domain.TransitionKind.CrossDissolve, track.Id, first.Id, second.Id,
            KadrStudio.Core.Domain.TimelineTime.FromSeconds(4.5), KadrStudio.Core.Domain.TimelineTime.FromSeconds(1));
        project = project with
        {
            Sources = project.Sources.Add(source.Id, source),
            MediaClips = [first, second],
            Transitions = [transition]
        };
        var mapper = new EditorProjectMapper();

        var restored = mapper.ToCore(mapper.ToUi(project));

        Assert.Equal(transition, Assert.Single(restored.Transitions));
        Assert.Equal(first.Video, restored.FindMediaClip(first.Id)!.Video);
        var restoredSource = restored.Sources[source.Id];
        Assert.Equal("verified", restoredSource.VerifiedFingerprint);
        Assert.True(restoredSource.IsVariableFrameRate);
        Assert.True(source.Streams.SequenceEqual(restoredSource.Streams));
    }

    [Fact]
    public async Task Wpf_project_service_saves_real_sqlite_and_reopens_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "KadrStudio", "adapter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = EditorProject.CreateNew();
            project.Name = "SQLite UI";
            var sourceId = Guid.NewGuid();
            project.Media.Add(new MediaAsset
            {
                Id = sourceId, Path = "F:\\media\\input.mkv", Name = "input.mkv",
                Kind = MediaKind.Video, Duration = 10, HasAudio = true
            });
            project.Clips.Add(new TimelineClip
            {
                AssetId = sourceId, Track = UiTrackKind.Visual, Start = 0, Duration = 5
            });
            var path = Path.Combine(root, "ui.kadr");
            var service = new ProjectService();

            await service.SaveAsync(project, path);
            var opened = await service.OpenAsync(path);

            Assert.Equal("SQLite UI", opened.Name);
            Assert.Single(opened.Clips);
            Assert.Equal(path, opened.FilePath);
            var header = new byte[16];
            await using var stream = File.OpenRead(path);
            Assert.Equal(header.Length, await stream.ReadAsync(header));
            Assert.Equal("SQLite format 3\0", System.Text.Encoding.ASCII.GetString(header));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Project_history_is_embedded_in_sqlite_and_restores_snapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "KadrStudio", "history-adapter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "history.kadr");
            var project = EditorProject.CreateNew();
            project.Name = "Before";
            var projectService = new ProjectService();
            await projectService.SaveAsync(project, path);
            var history = new ProjectHistoryService(Path.Combine(root, "local"));

            var entry = await history.CreateCheckpointAsync(project, "before rename");
            project.Name = "After";
            await projectService.SaveAsync(project, path);
            var restored = await history.RestoreCheckpointAsync(entry, path);

            Assert.Equal("Before", restored.Name);
            Assert.Single(await history.GetCheckpointsAsync(project));
            await history.DeleteCheckpointAsync(entry);
            Assert.Empty(await history.GetCheckpointsAsync(project));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Concurrent_autosaves_are_serialized_and_latest_snapshot_wins()
    {
        var root = Path.Combine(Path.GetTempPath(), "KadrStudio", "autosave-adapter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var service = new ProjectService(root);
            var project = EditorProject.CreateNew();
            project.Name = "Revision 1";
            var first = service.SaveAutosaveAsync(project);
            project.Name = "Revision 2";
            var second = service.SaveAutosaveAsync(project);

            await Task.WhenAll(first, second);
            var restored = await service.OpenAutosaveAsync();

            Assert.Equal("Revision 2", restored.Name);
            await service.DeleteAutosaveAsync();
            Assert.False(await service.HasAutosaveAsync());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
