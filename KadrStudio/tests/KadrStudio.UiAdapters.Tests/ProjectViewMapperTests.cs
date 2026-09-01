using KadrStudio.Adapters;
using KadrStudio.Models;
using KadrStudio.Services;
using CoreTrackKind = KadrStudio.Core.Domain.TrackKind;
using CoreFrameRate = KadrStudio.Core.Domain.FrameRate;
using UiTrackKind = KadrStudio.Models.TrackKind;
using System.Collections.Immutable;

namespace KadrStudio.UiAdapters.Tests;

public sealed class ProjectViewMapperTests
{
    [Fact]
    public void Immutable_project_is_projected_for_wpf_without_losing_clip_data()
    {
        var sourceId = Guid.NewGuid();
        var link = Guid.NewGuid();
        var core = KadrStudio.Core.Domain.ProjectState.CreateNew("Mapper", CoreFrameRate.Fps23976);
        var visualTrack = core.Tracks.Single(item => item.Kind == CoreTrackKind.Visual && item.Index == 0);
        var audioTrack = core.Tracks.Single(item => item.Kind == CoreTrackKind.Audio && item.Index == 0);
        var textTrack = core.Tracks.Single(item => item.Kind == CoreTrackKind.Text);
        var source = new KadrStudio.Core.Domain.MediaSource(
            sourceId, "F:\\media\\episode.mkv", "episode.mkv", KadrStudio.Core.Domain.MediaKind.Video,
            KadrStudio.Core.Domain.TimelineTime.FromSeconds(120), true, 1920, 1080,
            CoreFrameRate.Fps23976, "hevc", "aac");
        core = core with
        {
            Revision = 7,
            Sources = core.Sources.Add(sourceId, source),
            MediaClips =
            [
                new(Guid.NewGuid(), sourceId, visualTrack.Id,
                    KadrStudio.Core.Domain.TimelineTime.FromSeconds(3),
                    KadrStudio.Core.Domain.TimelineTime.FromSeconds(5),
                    KadrStudio.Core.Domain.TimelineTime.FromSeconds(20), link,
                    new KadrStudio.Core.Domain.VideoParameters(Brightness: 0.1)),
                new(Guid.NewGuid(), sourceId, audioTrack.Id,
                    KadrStudio.Core.Domain.TimelineTime.FromSeconds(3),
                    KadrStudio.Core.Domain.TimelineTime.FromSeconds(5),
                    KadrStudio.Core.Domain.TimelineTime.FromSeconds(20), link, Audio:
                    new KadrStudio.Core.Domain.AudioParameters(Volume: 0.8, Pan: -0.2))
            ],
            TextClips =
            [
                new(Guid.NewGuid(), textTrack.Id,
                    KadrStudio.Core.Domain.TimelineTime.FromSeconds(4),
                    KadrStudio.Core.Domain.TimelineTime.FromSeconds(3),
                    "Line 1\nLine 2", new KadrStudio.Core.Domain.TextStyle())
            ]
        };

        var restored = new ProjectViewMapper().ToUi(core, "F:\\project.kadr");

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
        var project = KadrStudio.Core.Domain.ProjectState.CreateNew(
            "fractional", new CoreFrameRate(numerator, denominator));

        var restored = new ProjectViewMapper().ToUi(project);

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
        var restored = new ProjectViewMapper().ToUi(project);

        Assert.Equal(project.Tracks.Length, restored.Tracks.Count);
        Assert.Equal(id, restored.Tracks[0].Id);
        Assert.Equal("Main picture", restored.Tracks[0].Name);
        Assert.True(restored.Tracks[0].IsMuted);
        Assert.True(restored.Tracks[0].IsLocked);
        Assert.False(restored.Tracks[0].IsVisible);
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
        var restored = new ProjectViewMapper().ToUi(project);

        Assert.Equal(first.Video!.PositionX, restored.FindClip(first.Id)!.PositionX);
        var restoredSource = restored.FindAsset(source.Id)!;
        Assert.Equal("verified", restoredSource.ProbeResult!.Fingerprint.VerifiedHash);
        Assert.True(restoredSource.ProbeResult.IsVariableFrameRate);
        Assert.True(source.Streams.SequenceEqual(restoredSource.ProbeResult.Streams));
    }

    [Fact]
    public async Task Wpf_project_service_saves_real_sqlite_and_reopens_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "KadrStudio", "adapter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = KadrStudio.Core.Domain.ProjectState.CreateNew("SQLite UI");
            var sourceId = Guid.NewGuid();
            var source = new KadrStudio.Core.Domain.MediaSource(
                sourceId, "F:\\media\\input.mkv", "input.mkv", KadrStudio.Core.Domain.MediaKind.Video,
                KadrStudio.Core.Domain.TimelineTime.FromSeconds(10), true);
            var visualTrack = project.Tracks.Single(item => item.Kind == CoreTrackKind.Visual && item.Index == 0);
            project = project with
            {
                Sources = project.Sources.Add(sourceId, source),
                MediaClips =
                [
                    new(Guid.NewGuid(), sourceId, visualTrack.Id,
                        KadrStudio.Core.Domain.TimelineTime.Zero,
                        KadrStudio.Core.Domain.TimelineTime.Zero,
                        KadrStudio.Core.Domain.TimelineTime.FromSeconds(5),
                        Video: new KadrStudio.Core.Domain.VideoParameters())
                ]
            };
            var path = Path.Combine(root, "ui.kadr");
            using var service = new ProjectService();

            await service.SaveAsync(project, path);
            var opened = await service.OpenAsync(path);

            Assert.Equal("SQLite UI", opened.Name);
            Assert.Single(opened.MediaClips);
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
            var before = KadrStudio.Core.Domain.ProjectState.CreateNew("Before");
            using var projectService = new ProjectService();
            await projectService.SaveAsync(before, path);
            var history = new ProjectHistoryService(Path.Combine(root, "local"));

            var entry = await history.CreateCheckpointAsync(before, path, "before rename");
            var after = before with { Name = "After", Revision = before.Revision + 1 };
            await projectService.SaveAsync(after, path);
            var restored = await history.RestoreCheckpointAsync(entry);

            Assert.Equal("Before", restored.Name);
            Assert.Single(await history.GetCheckpointsAsync(after, path));
            await history.DeleteCheckpointAsync(entry);
            Assert.Empty(await history.GetCheckpointsAsync(after, path));
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
            using var service = new ProjectService(root);
            var project = KadrStudio.Core.Domain.ProjectState.CreateNew("Revision 1");
            var first = service.SaveAutosaveAsync(project);
            var second = service.SaveAutosaveAsync(project with
            {
                Name = "Revision 2", Revision = 1, UpdatedAt = project.UpdatedAt.AddSeconds(1)
            });

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

    [Fact]
    public async Task Project_service_rejects_second_writer_until_first_editor_releases_lease()
    {
        var root = Path.Combine(Path.GetTempPath(), "KadrStudio", "lease-adapter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "shared.kadr");
            using var first = new ProjectService(Path.Combine(root, "recovery-1"));
            await first.SaveAsync(KadrStudio.Core.Domain.ProjectState.CreateNew(), path);
            using var second = new ProjectService(Path.Combine(root, "recovery-2"));

            await Assert.ThrowsAsync<KadrStudio.Infrastructure.Storage.ProjectFileLockedException>(
                () => second.OpenAsync(path));

            first.Dispose();
            var reopened = await second.OpenAsync(path);
            Assert.NotEqual(Guid.Empty, reopened.Id);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Workspace_cache_settings_survive_restart_and_corruption_falls_back_safely()
    {
        var root = Path.Combine(Path.GetTempPath(), "KadrStudio", "settings-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "settings.json");
            var cacheRoot = Path.Combine(root, "cache");
            var service = new WorkspaceSettingsService(path);
            await service.SaveAsync(new WorkspaceSettings(cacheRoot, 2L * 1024 * 1024 * 1024));

            var restored = new WorkspaceSettingsService(path).Load();

            Assert.Equal(Path.GetFullPath(cacheRoot), restored.ArtifactRoot);
            Assert.Equal(2L * 1024 * 1024 * 1024, restored.ArtifactDiskBudgetBytes);
            await File.WriteAllTextAsync(path, "{not-json");
            Assert.Equal(WorkspaceSettings.Default, new WorkspaceSettingsService(path).Load());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
