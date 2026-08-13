using KadrStudio.Application.Preview;
using KadrStudio.Core.Domain;
using KadrStudio.Models;
using KadrStudio.Playback;
using KadrStudio.Services;
using UiMediaKind = KadrStudio.Models.MediaKind;
using UiTrackKind = KadrStudio.Models.TrackKind;

namespace KadrStudio.UiAdapters.Tests;

public sealed class PreviewFrameServerIntegrationTests
{
    [Fact(Timeout = 30_000)]
    public async Task Real_ffmpeg_source_produces_non_black_frames_and_exact_seek()
    {
        var root = Path.Combine(Path.GetTempPath(), "KadrStudio", "frame-server-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var locator = new FfmpegLocator();
            locator.EnsureAvailable();
            var source = Path.Combine(root, "moving-source.mp4");
            var result = await new ProcessRunner().RunAsync(locator.FfmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "testsrc2=s=160x90:r=10:d=4",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", source
            ]);
            Assert.Equal(0, result.ExitCode);

            var assetId = Guid.NewGuid();
            var project = EditorProject.CreateNew();
            project.CanvasWidth = 160;
            project.CanvasHeight = 90;
            project.FrameRate = 10;
            project.Media.Add(new MediaAsset
            {
                Id = assetId, Path = source, Name = "moving-source.mp4", Kind = UiMediaKind.Video,
                Duration = 4, Width = 160, Height = 90, FrameRate = 10, VideoCodec = "h264"
            });
            project.Clips.Add(new TimelineClip
            {
                AssetId = assetId, Track = UiTrackKind.Visual, TrackIndex = 0,
                Start = 0, SourceStart = 0, Duration = 4
            });

            await using var coordinator = new TimelineRenderCoordinator(locator);
            await using var engine = new PreviewFrameServer(locator.FfmpegPath, coordinator);
            var first = NextFrame(engine);
            await engine.PrepareAsync(coordinator.CreatePlan(project), new PreviewRequest(
                TimelineTime.Zero, new FrameRate(10), 160, 90, false, new PreviewGeneration(7, 3, 2)));
            var firstFrame = await first.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(7, firstFrame.Generation);
            Assert.Equal(160 * 90 * 4, firstFrame.Bgra.Length);
            Assert.Contains(firstFrame.Bgra.Span.ToArray(), value => value != 0);

            var sought = NextFrame(engine);
            await engine.SeekAsync(TimelineTime.FromSeconds(2));
            var soughtFrame = await sought.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.InRange(soughtFrame.Position.TotalSeconds, 2, 2.11);
            Assert.False(firstFrame.Bgra.Span.SequenceEqual(soughtFrame.Bgra.Span));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static Task<VideoFrame> NextFrame(PreviewFrameServer engine)
    {
        var result = new TaskCompletionSource<VideoFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<VideoFrame>? handler = null;
        handler = (_, frame) =>
        {
            engine.FramePresented -= handler;
            result.TrySetResult(frame);
        };
        engine.FramePresented += handler;
        return result.Task;
    }
}
