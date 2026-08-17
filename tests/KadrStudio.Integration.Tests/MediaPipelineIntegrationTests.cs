using KadrStudio.Application.Rendering;
using KadrStudio.Playback;
using KadrStudio.Services;
using KadrStudio.Application.Media;
using KadrStudio.Core.Domain;
using KadrStudio.Infrastructure.Jobs;
using KadrStudio.Infrastructure.Rendering;
using System.Collections.Immutable;
using Xunit;

namespace KadrStudio.Integration.Tests;

public sealed class MediaPipelineIntegrationTests
{
    [Theory(Timeout = 120_000)]
    [InlineData(TransitionKind.CrossDissolve)]
    [InlineData(TransitionKind.DipToBlack)]
    [InlineData(TransitionKind.DipToWhite)]
    [InlineData(TransitionKind.Wipe)]
    [InlineData(TransitionKind.Slide)]
    public async Task Every_typed_transition_renders_real_video_and_constant_power_audio(TransitionKind kind)
    {
        var root = CreateRoot();
        try
        {
            var locator = new FfmpegLocator();
            locator.EnsureAvailable();
            var red = Path.Combine(root, "red.mp4");
            var blue = Path.Combine(root, "blue.mp4");
            await CreateColorSourceAsync(locator, red, "red", 440);
            await CreateColorSourceAsync(locator, blue, "blue", 880);
            var output = Path.Combine(root, $"transition-{kind}.mp4");
            var plan = new RenderPlanBuilder().Build(CreateTransitionProject(red, blue, kind));

            await using var scheduler = new BackgroundJobScheduler();
            var engine = new FfmpegRenderEngine(locator.FfmpegPath, new FfmpegRenderCommandBuilder(), scheduler);
            await engine.RenderAsync(plan, new RenderOutputOptions(
                RenderPurpose.Export, output, 320, 240, VideoQuality: 30));

            var probe = await new ProcessRunner().RunAsync(locator.FfprobePath,
                ["-v", "error", "-show_entries", "format=duration:stream=codec_type", "-of", "default=nw=1", output]);
            Assert.Equal(0, probe.ExitCode);
            Assert.Contains("codec_type=video", probe.StandardOutput, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("codec_type=audio", probe.StandardOutput, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("duration=4.", probe.StandardOutput, StringComparison.OrdinalIgnoreCase);
            Assert.True(new FileInfo(output).Length > 5_000);
        }
        finally { DeleteRoot(root); }
    }

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
            await coordinator.RenderAsync(coordinator.CreatePlan(
                    CreateAvProject(source)),
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
    public async Task Nvenc_startup_failure_is_retried_with_cpu_and_publishes_valid_output()
    {
        var root = CreateRoot();
        try
        {
            var locator = new FfmpegLocator();
            locator.EnsureAvailable();
            var source = Path.Combine(root, "fallback-source.mp4");
            var output = Path.Combine(root, "fallback-result.mp4");
            var create = await new ProcessRunner().RunAsync(locator.FfmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "testsrc2=s=160x90:r=24:d=1",
                "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo:d=1",
                "-shortest", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                "-c:a", "aac", source
            ]);
            Assert.Equal(0, create.ExitCode);
            var project = CreateAvProject(source);
            await using var scheduler = new BackgroundJobScheduler();
            var engine = new FfmpegRenderEngine(
                locator.FfmpegPath, new FailingNvencCommandBuilder(), scheduler);

            await engine.RenderAsync(new RenderPlanBuilder().Build(project),
                new RenderOutputOptions(
                    RenderPurpose.Export, output, 160, 90, VideoQuality: 30, UseHardwareEncoding: true));

            var probe = await new ProcessRunner().RunAsync(locator.FfprobePath,
                ["-v", "error", "-show_entries", "stream=codec_type", "-of", "csv=p=0", output]);
            Assert.Equal(0, probe.ExitCode);
            Assert.Contains("video", probe.StandardOutput, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("audio", probe.StandardOutput, StringComparison.OrdinalIgnoreCase);
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
            var coreProject = CreateAvProject(source);
            await using var coordinator = new TimelineRenderCoordinator(locator);
            var originalPlan = coordinator.CreatePlan(coreProject);
            string proxyPath;
            await using (var proxies = new PreviewProxyStore(locator))
            {
                await proxies.PrepareAsync(coreProject);
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
                await reopened.PrepareAsync(coreProject);
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
            await using var timelineCache = new TimelineMediaCacheService(
                locator, new ProcessRunner(), Path.Combine(root, "timeline-cache"));
            var sourceInfo = new MediaSource(
                asset.Id, asset.Path, asset.Name, KadrStudio.Core.Domain.MediaKind.Audio,
                TimelineTime.FromSeconds(asset.Duration), true,
                AudioCodec: asset.AudioCodec,
                FileSize: asset.FileSizeBytes,
                Fingerprint: asset.ProbeResult!.Fingerprint.FastHash,
                FastFingerprint: asset.ProbeResult.Fingerprint.FastHash,
                VerifiedFingerprint: asset.ProbeResult.Fingerprint.VerifiedHash ?? string.Empty,
                Streams: asset.ProbeResult.Streams);
            var preparations = await Task.WhenAll(
                timelineCache.PrepareAsync(sourceInfo),
                timelineCache.PrepareAsync(sourceInfo));
            var derived = preparations[0];

            Assert.Same(derived.Waveform, preparations[1].Waveform);
            Assert.False(derived.Waveform.IsEmpty);
            var basePeaks = derived.Waveform.Levels[0].Peaks;
            Assert.Contains(basePeaks, peak => peak == default);
            Assert.Contains(basePeaks, peak => peak.MaximumLeft > 0.7f && peak.MaximumRight == 0);
            Assert.Contains(basePeaks, peak => peak.MaximumRight > 0.3f);
            Assert.Equal(800, derived.Waveform.ReadColumns(0, 1, 800).Length);

            const string longFingerprint = "long-recording-waveform-policy";
            var longRecording = sourceInfo with
            {
                Id = Guid.NewGuid(),
                Duration = TimelineTime.FromSeconds(10 * 60 * 60),
                Fingerprint = longFingerprint,
                FastFingerprint = longFingerprint,
                VerifiedFingerprint = longFingerprint
            };
            var bounded = await timelineCache.PrepareAsync(longRecording);
            Assert.True(bounded.Waveform.Levels[0].FramesPerPeak > 256);
            Assert.True(bounded.Waveform.Levels[0].Count < basePeaks.Length);
        }
        finally { DeleteRoot(root); }
    }

    [Fact(Timeout = 60_000)]
    public async Task Timeline_thumbnails_are_materialized_on_demand_at_distinct_frame_times()
    {
        var root = CreateRoot();
        try
        {
            var locator = new FfmpegLocator();
            locator.EnsureAvailable();
            var path = Path.Combine(root, "moving.mp4");
            var create = await new ProcessRunner().RunAsync(locator.FfmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
                "testsrc2=s=320x180:r=30:d=2", "-c:v", "libx264", "-preset", "ultrafast",
                "-pix_fmt", "yuv420p", path
            ]);
            Assert.Equal(0, create.ExitCode);
            var probed = await new MediaProbeService(locator, new ProcessRunner()).ProbeAsync(path);
            var source = new MediaSource(
                probed.Id, probed.Path, probed.Name, KadrStudio.Core.Domain.MediaKind.Video,
                TimelineTime.FromSeconds(probed.Duration), false,
                probed.Width, probed.Height, probed.ProbeResult!.FrameRate,
                probed.VideoCodec, FileSize: probed.FileSizeBytes,
                Fingerprint: probed.ProbeResult.Fingerprint.FastHash,
                FastFingerprint: probed.ProbeResult.Fingerprint.FastHash,
                VerifiedFingerprint: probed.ProbeResult.Fingerprint.VerifiedHash ?? string.Empty,
                Streams: probed.ProbeResult.Streams);
            await using var cache = new TimelineMediaCacheService(
                locator, new ProcessRunner(), Path.Combine(root, "artifacts"));

            var early = await cache.GetThumbnailAsync(source, TimelineTime.FromSeconds(0.2));
            var late = await cache.GetThumbnailAsync(source, TimelineTime.FromSeconds(1.6));
            var earlyCached = await cache.GetThumbnailAsync(source, TimelineTime.FromSeconds(0.2));

            Assert.NotNull(early);
            Assert.NotNull(late);
            Assert.True(File.Exists(early));
            Assert.True(File.Exists(late));
            Assert.NotEqual(early, late);
            Assert.Equal(early, earlyCached);
            Assert.True(new FileInfo(early!).Length > 100);
            Assert.True(new FileInfo(late!).Length > 100);
        }
        finally { DeleteRoot(root); }
    }

    [Fact(Timeout = 60_000)]
    public async Task Ffprobe_ingest_preserves_fractional_rate_and_stream_layout()
    {
        var root = CreateRoot();
        try
        {
            var locator = new FfmpegLocator();
            locator.EnsureAvailable();
            var source = Path.Combine(root, "fractional.mp4");
            var create = await new ProcessRunner().RunAsync(locator.FfmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "testsrc2=s=320x180:r=24000/1001:d=1",
                "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo",
                "-shortest", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                "-c:a", "aac", source
            ]);
            Assert.Equal(0, create.ExitCode);
            IMediaProbe probe = new MediaProbeService(locator, new ProcessRunner());

            var result = await probe.ProbeAsync(source, verifyContent: true);

            Assert.Equal(FrameRate.Fps23976, result.FrameRate);
            var video = result.Streams.Single(item => item.Kind == MediaStreamKind.Video);
            var audio = result.Streams.Single(item => item.Kind == MediaStreamKind.Audio);
            Assert.Equal(FrameRate.Fps23976, video.FrameRate);
            Assert.Equal(48_000, audio.SampleRate);
            Assert.Equal(2, audio.Channels);
            Assert.NotNull(result.Fingerprint.VerifiedHash);
        }
        finally { DeleteRoot(root); }
    }

    private static ProjectState CreateAvProject(string source)
    {
        var id = Guid.NewGuid();
        var project = ProjectState.CreateNew("AV integration", new FrameRate(24)) with
        {
            Sequence = new SequenceSettings(320, 240, new FrameRate(24), 48_000)
        };
        var mediaSource = new MediaSource(
            id, source, "source-av.mp4", KadrStudio.Core.Domain.MediaKind.Video,
            TimelineTime.FromSeconds(2), true, 320, 180, new FrameRate(24), "h264", "aac",
            new FileInfo(source).Length, Fingerprint: $"av-{new FileInfo(source).Length:x}");
        var visual = project.Tracks.Single(item => item.Kind == TrackKind.Visual && item.Index == 0);
        var audio = project.Tracks.Single(item => item.Kind == TrackKind.Audio && item.Index == 0);
        var link = Guid.NewGuid();
        return project with
        {
            Sources = project.Sources.Add(id, mediaSource),
            MediaClips =
            [
                new(Guid.NewGuid(), id, visual.Id, TimelineTime.Zero, TimelineTime.Zero,
                    TimelineTime.FromSeconds(2), link, Video: new VideoParameters()),
                new(Guid.NewGuid(), id, audio.Id, TimelineTime.Zero, TimelineTime.Zero,
                    TimelineTime.FromSeconds(2), link, Audio: new AudioParameters())
            ]
        };
    }

    private static async Task CreateColorSourceAsync(
        FfmpegLocator locator,
        string output,
        string color,
        int frequency)
    {
        var result = await new ProcessRunner().RunAsync(locator.FfmpegPath,
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", $"color=c={color}:s=320x240:r=24:d=3",
            "-f", "lavfi", "-i", $"sine=frequency={frequency}:sample_rate=48000:duration=3",
            "-shortest", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            "-c:a", "aac", output
        ]);
        Assert.Equal(0, result.ExitCode);
    }

    private static ProjectState CreateTransitionProject(string firstPath, string secondPath, TransitionKind kind)
    {
        var project = ProjectState.CreateNew($"Transition {kind}", new FrameRate(24)) with
        {
            Sequence = new SequenceSettings(320, 240, new FrameRate(24), 48_000)
        };
        var firstSource = new MediaSource(Guid.NewGuid(), firstPath, "red.mp4", KadrStudio.Core.Domain.MediaKind.Video,
            TimelineTime.FromSeconds(3), true, 320, 240, new FrameRate(24), "h264", "aac",
            new FileInfo(firstPath).Length, Fingerprint: "red-source");
        var secondSource = new MediaSource(Guid.NewGuid(), secondPath, "blue.mp4", KadrStudio.Core.Domain.MediaKind.Video,
            TimelineTime.FromSeconds(3), true, 320, 240, new FrameRate(24), "h264", "aac",
            new FileInfo(secondPath).Length, Fingerprint: "blue-source");
        var visual = project.Tracks.Single(item => item.Kind == KadrStudio.Core.Domain.TrackKind.Visual && item.Index == 0);
        var audio = project.Tracks.Single(item => item.Kind == KadrStudio.Core.Domain.TrackKind.Audio && item.Index == 0);
        var v1 = new MediaClip(Guid.NewGuid(), firstSource.Id, visual.Id, TimelineTime.Zero,
            TimelineTime.FromSeconds(0.5), TimelineTime.FromSeconds(2), Video: new VideoParameters());
        var v2 = new MediaClip(Guid.NewGuid(), secondSource.Id, visual.Id, TimelineTime.FromSeconds(2),
            TimelineTime.FromSeconds(0.5), TimelineTime.FromSeconds(2), Video: new VideoParameters());
        var a1 = new MediaClip(Guid.NewGuid(), firstSource.Id, audio.Id, TimelineTime.Zero,
            TimelineTime.FromSeconds(0.5), TimelineTime.FromSeconds(2), Audio: new AudioParameters());
        var a2 = new MediaClip(Guid.NewGuid(), secondSource.Id, audio.Id, TimelineTime.FromSeconds(2),
            TimelineTime.FromSeconds(0.5), TimelineTime.FromSeconds(2), Audio: new AudioParameters());
        return project with
        {
            Sources = ImmutableDictionary<Guid, MediaSource>.Empty
                .Add(firstSource.Id, firstSource).Add(secondSource.Id, secondSource),
            MediaClips = [v1, v2, a1, a2],
            Transitions =
            [
                new TimelineTransition(Guid.NewGuid(), kind, visual.Id, v1.Id, v2.Id,
                    TimelineTime.FromSeconds(1.5), TimelineTime.FromSeconds(1)),
                new TimelineTransition(Guid.NewGuid(), TransitionKind.ConstantPowerAudio, audio.Id, a1.Id, a2.Id,
                    TimelineTime.FromSeconds(1.5), TimelineTime.FromSeconds(1))
            ]
        };
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "KadrStudio", "media-integration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root) { try { Directory.Delete(root, recursive: true); } catch { } }

    private sealed class FailingNvencCommandBuilder : IRenderCommandBuilder
    {
        private readonly FfmpegRenderCommandBuilder _inner = new();

        public ExternalRenderCommand Build(RenderPlan plan, RenderOutputOptions options)
        {
            var command = _inner.Build(plan, options);
            return options.UseHardwareEncoding
                ? command with
                {
                    Arguments = command.Arguments
                        .Select(argument => argument == "h264_nvenc" ? "h264_nvenc_missing" : argument)
                        .ToImmutableArray()
                }
                : command;
        }
    }
}
