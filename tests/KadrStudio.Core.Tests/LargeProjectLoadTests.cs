using System.Collections.Immutable;
using System.Diagnostics;
using KadrStudio.Application.Rendering;
using KadrStudio.Core.Domain;
using KadrStudio.Core.Validation;
using KadrStudio.Infrastructure.Storage;

namespace KadrStudio.Core.Tests;

public sealed class LargeProjectLoadTests
{
    private static readonly TimelineTime FourHours = TimelineTime.FromSeconds(4 * 60 * 60);

    [Fact]
    public async Task Four_hour_eighteen_track_project_with_ten_thousand_clips_survives_full_pipeline()
    {
        var project = CreateLargeProject();
        var stopwatch = Stopwatch.StartNew();

        var validation = new ProjectValidator().Validate(project);
        var validationElapsed = stopwatch.Elapsed;
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
        Assert.True(validationElapsed < TimeSpan.FromSeconds(5), $"Validation took {validationElapsed}.");

        stopwatch.Restart();
        var plan = new RenderPlanBuilder().Build(project);
        var renderPlanElapsed = stopwatch.Elapsed;
        Assert.Equal(FourHours, project.Duration);
        Assert.Equal(5_000, plan.VisualLayers.Length);
        Assert.Equal(5_000, plan.AudioLayers.Length);
        Assert.Equal(240, plan.TextLayers.Length);
        Assert.True(renderPlanElapsed < TimeSpan.FromSeconds(5), $"Render plan took {renderPlanElapsed}.");

        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "four-hours.kadr");
        var store = new SqliteProjectStore();
        stopwatch.Restart();
        await store.SaveAsync(path, project);
        var restored = await store.LoadAsync(path);
        var storageElapsed = stopwatch.Elapsed;

        Assert.Equal(project.Id, restored.Id);
        Assert.Equal(project.Revision, restored.Revision);
        Assert.True(project.Tracks.SequenceEqual(restored.Tracks), "Track order changed during SQLite roundtrip.");
        Assert.Equal(project.MediaClips.Length, restored.MediaClips.Length);
        Assert.Equal(project.TextClips.Length, restored.TextClips.Length);
        Assert.Equal(project.Duration, restored.Duration);
        Assert.Equal(plan.ContentSignature, new RenderPlanBuilder().Build(restored).ContentSignature);
        Assert.True(storageElapsed < TimeSpan.FromSeconds(30), $"SQLite roundtrip took {storageElapsed}.");
        Assert.True((await store.CheckIntegrityAsync(path)).IsValid);
    }

    private static ProjectState CreateLargeProject()
    {
        var tracks = ImmutableArray.CreateBuilder<TimelineTrack>(18);
        for (var index = 0; index < 8; index++)
            tracks.Add(new TimelineTrack(Guid.NewGuid(), TrackKind.Visual, index, $"V{index + 1}"));
        for (var index = 0; index < 8; index++)
            tracks.Add(new TimelineTrack(Guid.NewGuid(), TrackKind.Audio, index, $"A{index + 1}"));
        for (var index = 0; index < 2; index++)
            tracks.Add(new TimelineTrack(Guid.NewGuid(), TrackKind.Text, index, $"T{index + 1}"));

        var source = new MediaSource(
            Guid.NewGuid(), "F:\\media\\four-hours.mkv", "four-hours.mkv", MediaKind.Video,
            FourHours, true, 3840, 2160, FrameRate.Fps23976, "hevc", "aac", 32_000_000_000, 123, "load-test");
        const int clipsPerTrack = 625;
        var mediaClips = ImmutableArray.CreateBuilder<MediaClip>(10_000);
        var clipDuration = new TimelineTime(FourHours.Ticks / clipsPerTrack);
        foreach (var track in tracks.Where(item => item.Kind is TrackKind.Visual or TrackKind.Audio))
        {
            for (var clipIndex = 0; clipIndex < clipsPerTrack; clipIndex++)
            {
                var position = new TimelineTime(clipDuration.Ticks * clipIndex);
                mediaClips.Add(new MediaClip(
                    Guid.NewGuid(), source.Id, track.Id, position, position, clipDuration,
                    Video: track.Kind == TrackKind.Visual ? new VideoParameters() : null,
                    Audio: track.Kind == TrackKind.Audio ? new AudioParameters() : null));
            }
        }

        var textClips = ImmutableArray.CreateBuilder<TextClip>(240);
        foreach (var track in tracks.Where(item => item.Kind == TrackKind.Text))
        {
            for (var index = 0; index < 120; index++)
            {
                textClips.Add(new TextClip(
                    Guid.NewGuid(), track.Id, TimelineTime.FromSeconds(index * 120 + track.Index * 10),
                    TimelineTime.FromSeconds(5), $"Subtitle {track.Index + 1}:{index + 1}", new TextStyle(IsSubtitle: true)));
            }
        }

        return new ProjectState
        {
            Id = Guid.NewGuid(),
            Name = "Four-hour load test",
            FrameRate = FrameRate.Fps23976,
            Revision = 7_500,
            Tracks = tracks.MoveToImmutable(),
            Sources = ImmutableDictionary<Guid, MediaSource>.Empty.Add(source.Id, source),
            MediaClips = mediaClips.MoveToImmutable(),
            TextClips = textClips.MoveToImmutable()
        };
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KadrStudio", "load-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
