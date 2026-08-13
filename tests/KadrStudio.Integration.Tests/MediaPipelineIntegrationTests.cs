using KadrStudio.Application.Rendering;
using KadrStudio.Models;
using KadrStudio.Playback;
using KadrStudio.Services;
using UiMediaKind = KadrStudio.Models.MediaKind;
using UiTrackKind = KadrStudio.Models.TrackKind;
using Xunit;

namespace KadrStudio.Integration.Tests;

public sealed class MediaPipelineIntegrationTests
{
    [Fact(Timeout = 60_000)]
    public async Task Export_composes_real_video_and_audio_into_one_valid_file()
    {
        var root = CreateRoot();
        try
        {
            var locator = new FfmpegLocator();
            locator.EnsureAvailable();
            var source = Path.Combine(root, "export-source.mp4");
            var output = Path.Combine(root, "export-result.mp4");
            var create = await new ProcessRunner().RunAsync(locator.FfmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "testsrc2=s=320x180:r=24:d=2",
                "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=2",
                "-shortest", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                "-c:a", "aac", source
            ]);
            Assert.Equal(0, create.ExitCode);

            await using var coordinator = new TimelineRenderCoordinator(locator);
            await coordinator.RenderAsync(coordinator.CreatePlan(CreateAvProject(source, root)),
                new RenderOutputOptions(RenderPurpose.Export, output, 320, 180, VideoQuality: 24));

            var probe = await new ProcessRunner().RunAsync(locator.FfprobePath,
                ["-v", "error", "-show_entries", "stream=codec_type", "-of", "csv=p=0", output]);
            Assert.Equal(0, probe.ExitCode);
            Assert.Contains("video", probe.StandardOutput, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("audio", probe.StandardOutput, StringComparison.OrdinalIgnoreCase);
            Assert.True(new FileInfo(output).Length > 10_000);
        }
        finally { DeleteRoot(root); }
    }

    [Fact(Timeout = 60_000)]
    public async Task Proxy_is_video_only_keeps_audio_on_original_and_rebuilds_after_corruption()
    {
        var root = CreateRoot();
        try
        {
            var locator = new FfmpegLocator();
            locator.EnsureAvailable();
            var source = Path.Combine(root, "source-av.mp4");
            var create = await new ProcessRunner().RunAsync(locator.FfmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "testsrc2=s=320x180:r=24:d=2",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2",
                "-shortest", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                "-c:a", "aac", source
            ]);
            Assert.Equal(0, create.ExitCode);
            var project = CreateAvProject(source, root);
            await using var coordinator = new TimelineRenderCoordinator(locator);
            var originalPlan = coordinator.CreatePlan(project);
            string proxyPath;
            await using (var proxies = new PreviewProxyStore(locator))
            {
                await proxies.PrepareAsync(project);
                var proxied = proxies.UseAvailable(originalPlan);
                proxyPath = proxied.VisualLayers.Single().SourcePath;
                Assert.NotEqual(source, proxyPath);
                Assert.Equal(source, proxied.AudioLayers.Single().SourcePath);
            }

            var probe = await new ProcessRunner().RunAsync(locator.FfprobePath,
                ["-v", "error", "-show_entries", "stream=codec_type,width,height", "-of", "csv=p=0", proxyPath]);
            Assert.Equal(0, probe.ExitCode);
            Assert.Contains("video", probe.StandardOutput, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("audio", probe.StandardOutput, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("960,540", probe.StandardOutput);

            await File.WriteAllBytesAsync(proxyPath, [1, 2, 3, 4]);
            await using (var reopened = new PreviewProxyStore(locator))
            {
                await reopened.PrepareAsync(project);
                Assert.True(new FileInfo(reopened.UseAvailable(originalPlan).VisualLayers.Single().SourcePath).Length > 1024);
            }
        }
        finally { DeleteRoot(root); }
    }

    [Fact(Timeout = 60_000)]
    public async Task Real_stereo_audio_builds_zero_silence_and_distinct_left_right_peaks()
    {
        var root = CreateRoot();
        try
        {
            var locator = new FfmpegLocator();
            locator.EnsureAvailable();
            var source = Path.Combine(root, "stereo.wav");
            var create = await new ProcessRunner().RunAsync(locator.FfmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
                "aevalsrc=if(lt(t\\,0.5)\\,0\\,0.8*sin(2*PI*440*t))|if(lt(t\\,1.0)\\,0\\,0.35*sin(2*PI*880*t)):s=48000:d=2",
                "-c:a", "pcm_f32le", source
            ]);
            Assert.Equal(0, create.ExitCode);
            var asset = await new MediaProbeService(locator, new ProcessRunner()).ProbeAsync(source);
            await new TimelineMediaCacheService(locator, new ProcessRunner(), Path.Combine(root, "timeline-cache"))
                .PrepareAsync(asset);

            Assert.False(asset.Waveform.IsEmpty);
            var basePeaks = asset.Waveform.Levels[0].Peaks;
            Assert.Contains(basePeaks, peak => peak == default);
            Assert.Contains(basePeaks, peak => peak.MaximumLeft > 0.7f && peak.MaximumRight == 0);
            Assert.Contains(basePeaks, peak => peak.MaximumRight > 0.3f);
            Assert.Equal(800, asset.Waveform.ReadColumns(0, 1, 800).Length);
        }
        finally { DeleteRoot(root); }
    }

    private static EditorProject CreateAvProject(string source, string root)
    {
        var id = Guid.NewGuid();
        var project = EditorProject.CreateNew();
        project.FilePath = Path.Combine(root, "proxy-test.kadr");
        project.FrameRate = 24;
        project.Media.Add(new MediaAsset
        {
            Id = id, Path = source, Name = "source-av.mp4", Kind = UiMediaKind.Video,
            Duration = 2, Width = 320, Height = 180, FrameRate = 24, HasAudio = true,
            FileSizeBytes = new FileInfo(source).Length
        });
        var link = Guid.NewGuid();
        project.Clips.Add(new TimelineClip { AssetId = id, Track = UiTrackKind.Visual, Duration = 2, LinkGroupId = link });
        project.Clips.Add(new TimelineClip { AssetId = id, Track = UiTrackKind.Audio, Duration = 2, LinkGroupId = link });
        return project;
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "KadrStudio", "media-integration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root) { try { Directory.Delete(root, recursive: true); } catch { } }
}
