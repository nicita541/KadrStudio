using KadrStudio.Adapters;
using KadrStudio.Models;
using KadrStudio.Services;
using CoreTrackKind = KadrStudio.Core.Domain.TrackKind;
using UiTrackKind = KadrStudio.Models.TrackKind;

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
