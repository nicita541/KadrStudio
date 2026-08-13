using KadrStudio.Application.Preview;
using KadrStudio.Application.Rendering;
using KadrStudio.Core.Domain;
using KadrStudio.Models;
using KadrStudio.Playback;
using KadrStudio.Services;
using UiMediaKind = KadrStudio.Models.MediaKind;
using UiTrackKind = KadrStudio.Models.TrackKind;
using Xunit;

namespace KadrStudio.Integration.Tests;

public sealed class PreviewFrameServerIntegrationTests
{
    [Fact(Timeout = 30_000)]
    public async Task Real_ffmpeg_source_produces_non_black_frames_and_exact_seek()
    {
        var root = CreateRoot();
        try
        {
            var locator = new FfmpegLocator();
            locator.EnsureAvailable();
            var source = Path.Combine(root, "moving-source.mp4");
            await CreateVideoAsync(locator, source);
            var project = CreateProject(source, includeVideo: true, includeAudio: false);

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
        finally { DeleteRoot(root); }
    }

    [Fact(Timeout = 30_000)]
    public async Task Video_audio_and_combined_plans_emit_only_the_requested_streams()
    {
        var root = CreateRoot();
        try
        {
            var locator = new FfmpegLocator();
            locator.EnsureAvailable();
            var source = Path.Combine(root, "av-source.mp4");
            await CreateVideoAsync(locator, source, withAudio: true);
            await using var coordinator = new TimelineRenderCoordinator(locator);

            var videoPlan = coordinator.CreatePlan(CreateProject(source, includeVideo: true, includeAudio: false));
            var video = coordinator.CreateCommand(videoPlan, new RenderOutputOptions(
                RenderPurpose.FrameServer, "pipe:1", 160, 90,
                IncludeVideo: true, IncludeAudio: false, IncludeOverlays: false));
            Assert.Contains("[vout]", video.Arguments);
            Assert.DoesNotContain("[aout]", video.Arguments);

            var audioPlan = coordinator.CreatePlan(CreateProject(source, includeVideo: false, includeAudio: true));
            var audio = coordinator.CreateCommand(audioPlan, new RenderOutputOptions(
                RenderPurpose.AudioServer, "pipe:1", 16, 16,
                IncludeVideo: false, IncludeAudio: true, IncludeOverlays: false));
            Assert.Contains("[aout]", audio.Arguments);
            Assert.DoesNotContain("[vout]", audio.Arguments);
            Assert.Contains("pcm_f32le", audio.Arguments);

            var combined = coordinator.CreatePlan(CreateProject(source, includeVideo: true, includeAudio: true));
            Assert.Single(combined.VisualLayers);
            Assert.Single(combined.AudioLayers);
            Assert.NotEqual(combined.VideoContentSignature, combined.AudioContentSignature);
        }
        finally { DeleteRoot(root); }
    }

    private static EditorProject CreateProject(string source, bool includeVideo, bool includeAudio)
    {
        var id = Guid.NewGuid();
        var project = EditorProject.CreateNew();
        project.CanvasWidth = 160;
        project.CanvasHeight = 90;
        project.FrameRate = 10;
        project.Media.Add(new MediaAsset
        {
            Id = id, Path = source, Name = Path.GetFileName(source), Kind = UiMediaKind.Video,
            Duration = 4, Width = 160, Height = 90, FrameRate = 10, HasAudio = includeAudio,
            VideoCodec = "h264", AudioCodec = includeAudio ? "aac" : string.Empty,
            FileSizeBytes = new FileInfo(source).Length
        });
        if (includeVideo)
            project.Clips.Add(new TimelineClip { AssetId = id, Track = UiTrackKind.Visual, Start = 0, Duration = 4 });
        if (includeAudio)
            project.Clips.Add(new TimelineClip { AssetId = id, Track = UiTrackKind.Audio, Start = 0, Duration = 4 });
        return project;
    }

    private static async Task CreateVideoAsync(FfmpegLocator locator, string output, bool withAudio = false)
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=160x90:r=10:d=4"
        };
        if (withAudio) arguments.AddRange(["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=4", "-shortest"]);
        arguments.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p"]);
        if (withAudio) arguments.AddRange(["-c:a", "aac"]);
        arguments.Add(output);
        var result = await new ProcessRunner().RunAsync(locator.FfmpegPath, arguments);
        Assert.Equal(0, result.ExitCode);
    }

    private static Task<VideoFrame> NextFrame(PreviewFrameServer engine)
    {
        var result = new TaskCompletionSource<VideoFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<VideoFrame>? handler = null;
        handler = (_, frame) => { engine.FramePresented -= handler; result.TrySetResult(frame); };
        engine.FramePresented += handler;
        return result.Task;
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "KadrStudio", "integration-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root) { try { Directory.Delete(root, recursive: true); } catch { } }
}
