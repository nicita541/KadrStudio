using System.Collections.Immutable;
using KadrStudio.Application.Preview;
using KadrStudio.Application.Rendering;
using KadrStudio.Core.Domain;
using KadrStudio.Playback;
using KadrStudio.Services;
using Xunit;

namespace KadrStudio.Integration.Tests;

public sealed class MediaHostIntegrationTests
{
    [Fact(Timeout = 90_000)]
    public async Task Out_of_process_host_seeks_and_recovers_after_forced_termination()
    {
        var root = CreateRoot();
        try
        {
            var locator = new FfmpegLocator();
            locator.EnsureAvailable();
            var host = ResolveMediaHost();
            var source = Path.Combine(root, "host-source.mp4");
            var create = await new ProcessRunner().RunAsync(locator.FfmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "testsrc2=s=320x240:r=24:d=4",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", source
            ]);
            Assert.Equal(0, create.ExitCode);
            var plan = new RenderPlanBuilder().Build(CreateProject(source));
            await using var client = new MediaHostClient(host, locator.FfmpegPath);
            var first = NextFrame(client);
            var request = new PreviewRequest(TimelineTime.Zero, new FrameRate(24), 320, 240, false,
                new PreviewGeneration(71, 19, 3));

            await client.PrepareAsync(plan, request);
            var firstFrame = await first.WaitAsync(TimeSpan.FromSeconds(15));
            var firstPid = client.HostProcessId;
            Assert.True(firstPid > 0);
            Assert.Equal(71, firstFrame.Generation);
            Assert.Contains(firstFrame.Bgra.Span.ToArray(), value => value != 0);

            var sought = NextFrame(client);
            await client.SeekAsync(TimelineTime.FromSeconds(2));
            var soughtFrame = await sought.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.InRange(soughtFrame.Position.TotalSeconds, 2, 2.05);
            Assert.False(firstFrame.Bgra.Span.SequenceEqual(soughtFrame.Bgra.Span));

            var recovered = NextFrame(client);
            client.TerminateHostForTest();
            var recoveredFrame = await recovered.WaitAsync(TimeSpan.FromSeconds(30));
            await WaitUntilAsync(() => client.HostProcessId > 0 && client.HostProcessId != firstPid,
                TimeSpan.FromSeconds(15));

            Assert.Equal(71, recoveredFrame.Generation);
            Assert.InRange(recoveredFrame.Position.TotalSeconds, 2, 2.05);
            Assert.NotEqual(firstPid, client.HostProcessId);
            await client.PingAsync();
        }
        finally { DeleteRoot(root); }
    }

    [Fact(Timeout = 90_000)]
    public async Task Rapid_generation_update_never_presents_an_old_frame_after_acknowledgement()
    {
        var root = CreateRoot();
        try
        {
            var locator = new FfmpegLocator();
            locator.EnsureAvailable();
            var source = Path.Combine(root, "generation-source.mp4");
            var create = await new ProcessRunner().RunAsync(locator.FfmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "testsrc2=s=320x240:r=24:d=4",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", source
            ]);
            Assert.Equal(0, create.ExitCode);
            var plan = new RenderPlanBuilder().Build(CreateProject(source));
            await using var client = new MediaHostClient(ResolveMediaHost(), locator.FfmpegPath);
            var received = new System.Collections.Concurrent.ConcurrentQueue<long>();
            client.FramePresented += (_, frame) => received.Enqueue(frame.Generation);
            await client.PrepareAsync(plan, new PreviewRequest(
                TimelineTime.Zero, new FrameRate(24), 320, 240, false, new PreviewGeneration(1, 1, 1)));

            await client.UpdatePlanAsync(plan, new PreviewRequest(
                TimelineTime.FromSeconds(1), new FrameRate(24), 320, 240, false,
                new PreviewGeneration(2, 1, 1)), restartVideo: true, restartAudio: false);
            received.Clear();
            var current = NextFrame(client);
            await client.SeekAsync(TimelineTime.FromSeconds(1));
            var frame = await current.WaitAsync(TimeSpan.FromSeconds(15));
            await Task.Delay(100);

            Assert.Equal(2, frame.Generation);
            Assert.DoesNotContain(received, generation => generation != 2);
        }
        finally { DeleteRoot(root); }
    }

    [Fact(Timeout = 60_000)]
    public async Task Empty_video_gap_emits_real_black_frame_instead_of_stale_content()
    {
        var root = CreateRoot();
        try
        {
            var locator = new FfmpegLocator();
            locator.EnsureAvailable();
            var source = Path.Combine(root, "gap-source.mp4");
            var create = await new ProcessRunner().RunAsync(locator.FfmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "testsrc2=s=320x240:r=24:d=2",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", source
            ]);
            Assert.Equal(0, create.ExitCode);
            var project = CreateProject(source);
            var clip = project.MediaClips.Single();
            project = project with
            {
                MediaClips = [clip with { Start = TimelineTime.FromSeconds(1), Duration = TimelineTime.FromSeconds(2) }]
            };
            var plan = new RenderPlanBuilder().Build(project);
            await using var client = new MediaHostClient(ResolveMediaHost(), locator.FfmpegPath);
            var pending = NextFrame(client);

            await client.PrepareAsync(plan, new PreviewRequest(
                TimelineTime.Zero, new FrameRate(24), 320, 240, false, new PreviewGeneration(17, 0, 0)));
            var frame = await pending.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal(17, frame.Generation);
            for (var index = 0; index < frame.Bgra.Length; index += 4)
            {
                Assert.Equal(0, frame.Bgra.Span[index]);
                Assert.Equal(0, frame.Bgra.Span[index + 1]);
                Assert.Equal(0, frame.Bgra.Span[index + 2]);
            }
        }
        finally { DeleteRoot(root); }
    }

    [Fact(Timeout = 90_000)]
    public async Task Higher_video_track_wins_and_failed_worker_does_not_destroy_lower_track()
    {
        var root = CreateRoot();
        try
        {
            var locator = new FfmpegLocator();
            locator.EnsureAvailable();
            var red = Path.Combine(root, "lower-red.mp4");
            var blue = Path.Combine(root, "upper-blue.mp4");
            await CreateColorAsync(locator, red, "red");
            await CreateColorAsync(locator, blue, "blue");
            await using var client = new MediaHostClient(ResolveMediaHost(), locator.FfmpegPath);
            var stacked = new RenderPlanBuilder().Build(CreateStackedProject(red, blue));
            var upper = NextFrame(client);

            await client.PrepareAsync(stacked, new PreviewRequest(
                TimelineTime.Zero, new FrameRate(24), 320, 240, false, new PreviewGeneration(31, 1, 1)));
            var upperFrame = await upper.WaitAsync(TimeSpan.FromSeconds(15));
            var upperPixel = CenterPixel(upperFrame);
            Assert.True(upperPixel.B > 180 && upperPixel.R < 80,
                $"Expected upper blue track, got B={upperPixel.B}, G={upperPixel.G}, R={upperPixel.R}.");

            var failedProject = CreateStackedProject(red, Path.Combine(root, "missing-upper.mp4"));
            var failedPlan = new RenderPlanBuilder().Build(failedProject);
            var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Failed += (_, exception) => failure.TrySetResult(exception);
            var lower = NextFrame(client);
            await client.PrepareAsync(failedPlan, new PreviewRequest(
                TimelineTime.Zero, new FrameRate(24), 320, 240, false, new PreviewGeneration(32, 1, 1)));
            var lowerFrame = await lower.WaitAsync(TimeSpan.FromSeconds(15));
            var lowerPixel = CenterPixel(lowerFrame);

            Assert.True(lowerPixel.R > 180 && lowerPixel.B < 80,
                $"Expected surviving lower red track, got B={lowerPixel.B}, G={lowerPixel.G}, R={lowerPixel.R}.");
            Assert.IsType<MediaHostException>(await failure.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(PreviewState.Paused, client.State);
            await client.PingAsync();
        }
        finally { DeleteRoot(root); }
    }

    [Theory(Timeout = 90_000)]
    [InlineData(TransitionKind.CrossDissolve)]
    [InlineData(TransitionKind.DipToBlack)]
    [InlineData(TransitionKind.DipToWhite)]
    [InlineData(TransitionKind.Wipe)]
    [InlineData(TransitionKind.Slide)]
    public async Task Seeking_into_video_transition_starts_at_the_correct_progress(TransitionKind kind)
    {
        var root = CreateRoot();
        try
        {
            var locator = new FfmpegLocator();
            locator.EnsureAvailable();
            var red = Path.Combine(root, "from-red.mp4");
            var blue = Path.Combine(root, "to-blue.mp4");
            await CreateColorAsync(locator, red, "red");
            await CreateColorAsync(locator, blue, "blue");
            var plan = new RenderPlanBuilder().Build(CreateTransitionProject(red, blue, kind));
            await using var client = new MediaHostClient(ResolveMediaHost(), locator.FfmpegPath);
            var pending = NextFrame(client);

            await client.PrepareAsync(plan, new PreviewRequest(
                TimelineTime.FromSeconds(2), new FrameRate(24), 320, 240, false,
                new PreviewGeneration(41, 1, 1)));
            var frame = await pending.WaitAsync(TimeSpan.FromSeconds(15));
            var pixel = CenterPixel(frame);

            if (kind == TransitionKind.DipToBlack)
            {
                Assert.True(pixel.R < 40 && pixel.G < 40 && pixel.B < 40,
                    $"Expected black midpoint, got B={pixel.B}, G={pixel.G}, R={pixel.R}.");
            }
            else if (kind == TransitionKind.DipToWhite)
            {
                Assert.True(pixel.R > 210 && pixel.G > 210 && pixel.B > 210,
                    $"Expected white midpoint, got B={pixel.B}, G={pixel.G}, R={pixel.R}.");
            }
            else if (kind == TransitionKind.Wipe)
            {
                var left = PixelAt(frame, frame.Width / 4, frame.Height / 2);
                var right = PixelAt(frame, frame.Width * 3 / 4, frame.Height / 2);
                Assert.True(left.B > 180 && left.R < 80,
                    $"Expected incoming blue left half, got B={left.B}, G={left.G}, R={left.R}.");
                Assert.True(right.R > 180 && right.B < 80,
                    $"Expected outgoing red right half, got B={right.B}, G={right.G}, R={right.R}.");
            }
            else if (kind == TransitionKind.Slide)
            {
                var left = PixelAt(frame, frame.Width / 4, frame.Height / 2);
                var right = PixelAt(frame, frame.Width * 3 / 4, frame.Height / 2);
                Assert.True(left.R > 180 && left.B < 80,
                    $"Expected red left half, got B={left.B}, G={left.G}, R={left.R}.");
                Assert.True(right.B > 180 && right.R < 80,
                    $"Expected blue right half, got B={right.B}, G={right.G}, R={right.R}.");
            }
            else
            {
                Assert.True(pixel.R > 70 && pixel.B > 70,
                    $"Expected mixed transition frame, got B={pixel.B}, G={pixel.G}, R={pixel.R}.");
                Assert.InRange(Math.Abs(pixel.R - pixel.B), 0, 80);
            }
        }
        finally { DeleteRoot(root); }
    }

    [Fact(Timeout = 600_000)]
    public async Task One_thousand_seeks_keep_one_host_bounded_workers_and_no_orphan_ffmpeg()
    {
        var root = CreateRoot();
        var ffmpegBefore = System.Diagnostics.Process.GetProcessesByName("ffmpeg")
            .Select(process => process.Id).ToHashSet();
        var hostPid = 0;
        try
        {
            var locator = new FfmpegLocator();
            locator.EnsureAvailable();
            var source = Path.Combine(root, "seek-stress.mp4");
            var create = await new ProcessRunner().RunAsync(locator.FfmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "testsrc2=s=320x240:r=24:d=4",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", source
            ]);
            Assert.Equal(0, create.ExitCode);
            var plan = new RenderPlanBuilder().Build(CreateProject(source));
            await using (var client = new MediaHostClient(ResolveMediaHost(), locator.FfmpegPath))
            {
                var request = new PreviewRequest(TimelineTime.Zero, new FrameRate(24), 320, 240, false,
                    new PreviewGeneration(501, 601, 701));
                await client.PrepareAsync(plan, request);
                hostPid = client.HostProcessId;
                Assert.True(hostPid > 0);

                for (var index = 0; index < 1_000; index++)
                {
                    var seconds = (index * 37 % 95) / 24d;
                    var pending = NextFrame(client);
                    await client.SeekAsync(TimelineTime.FromSeconds(seconds));
                    var frame = await pending.WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.Equal(501, frame.Generation);
                    Assert.InRange(Math.Abs(frame.Position.TotalSeconds - seconds), 0, 1d / 24);
                    Assert.Equal(hostPid, client.HostProcessId);
                    if (index % 25 == 0)
                    {
                        var diagnostics = await client.GetDiagnosticsAsync();
                        Assert.Equal(hostPid, diagnostics.ProcessId);
                        Assert.InRange(diagnostics.ActiveVideoWorkers, 0, 1);
                        Assert.Equal(0, diagnostics.ActiveAudioWorkers);
                        Assert.InRange(diagnostics.PeakVideoWorkers, 0, 1);
                    }
                }
            }

            await WaitUntilAsync(() =>
                System.Diagnostics.Process.GetProcessesByName("ffmpeg")
                    .All(process => ffmpegBefore.Contains(process.Id)), TimeSpan.FromSeconds(10));
            Assert.False(IsProcessAlive(hostPid));
        }
        finally { DeleteRoot(root); }
    }

    private static ProjectState CreateProject(string sourcePath)
    {
        var project = ProjectState.CreateNew("MediaHost", new FrameRate(24)) with
        {
            Sequence = new SequenceSettings(320, 240, new FrameRate(24), 48_000)
        };
        var source = new MediaSource(Guid.NewGuid(), sourcePath, Path.GetFileName(sourcePath), MediaKind.Video,
            TimelineTime.FromSeconds(4), false, 320, 240, new FrameRate(24), "h264",
            FileSize: new FileInfo(sourcePath).Length, Fingerprint: "host-source");
        var track = project.Tracks.Single(item => item.Kind == TrackKind.Visual && item.Index == 0);
        var clip = new MediaClip(Guid.NewGuid(), source.Id, track.Id, TimelineTime.Zero,
            TimelineTime.Zero, TimelineTime.FromSeconds(4), Video: new VideoParameters());
        return project with
        {
            Sources = ImmutableDictionary<Guid, MediaSource>.Empty.Add(source.Id, source),
            MediaClips = [clip]
        };
    }

    private static ProjectState CreateStackedProject(string lowerPath, string upperPath)
    {
        var project = ProjectState.CreateNew("Stacked video", new FrameRate(24)) with
        {
            Sequence = new SequenceSettings(320, 240, new FrameRate(24), 48_000)
        };
        var lower = CreateSource(lowerPath, "lower");
        var upper = CreateSource(upperPath, "upper");
        var v1 = project.Tracks.Single(item => item.Kind == TrackKind.Visual && item.Index == 0);
        var v2 = project.Tracks.Single(item => item.Kind == TrackKind.Visual && item.Index == 1);
        return project with
        {
            Sources = ImmutableDictionary<Guid, MediaSource>.Empty.Add(lower.Id, lower).Add(upper.Id, upper),
            MediaClips =
            [
                new MediaClip(Guid.NewGuid(), lower.Id, v1.Id, TimelineTime.Zero,
                    TimelineTime.Zero, TimelineTime.FromSeconds(3), Video: new VideoParameters()),
                new MediaClip(Guid.NewGuid(), upper.Id, v2.Id, TimelineTime.Zero,
                    TimelineTime.Zero, TimelineTime.FromSeconds(3), Video: new VideoParameters())
            ]
        };
    }

    private static ProjectState CreateTransitionProject(
        string firstPath,
        string secondPath,
        TransitionKind kind)
    {
        var project = ProjectState.CreateNew("Cross dissolve", new FrameRate(24)) with
        {
            Sequence = new SequenceSettings(320, 240, new FrameRate(24), 48_000)
        };
        var first = CreateSource(firstPath, "from");
        var second = CreateSource(secondPath, "to");
        var v1 = project.Tracks.Single(item => item.Kind == TrackKind.Visual && item.Index == 0);
        var from = new MediaClip(Guid.NewGuid(), first.Id, v1.Id, TimelineTime.Zero,
            TimelineTime.FromSeconds(0.5), TimelineTime.FromSeconds(2), Video: new VideoParameters());
        var to = new MediaClip(Guid.NewGuid(), second.Id, v1.Id, TimelineTime.FromSeconds(2),
            TimelineTime.FromSeconds(0.5), TimelineTime.FromSeconds(2), Video: new VideoParameters());
        return project with
        {
            Sources = ImmutableDictionary<Guid, MediaSource>.Empty.Add(first.Id, first).Add(second.Id, second),
            MediaClips = [from, to],
            Transitions =
            [
                new TimelineTransition(Guid.NewGuid(), kind, v1.Id, from.Id, to.Id,
                    TimelineTime.FromSeconds(1.5), TimelineTime.FromSeconds(1))
            ]
        };
    }

    private static MediaSource CreateSource(string path, string fingerprint)
        => new(Guid.NewGuid(), path, Path.GetFileName(path), MediaKind.Video,
            TimelineTime.FromSeconds(3), false, 320, 240, new FrameRate(24), "h264",
            FileSize: File.Exists(path) ? new FileInfo(path).Length : 0, Fingerprint: fingerprint);

    private static async Task CreateColorAsync(FfmpegLocator locator, string path, string color)
    {
        var create = await new ProcessRunner().RunAsync(locator.FfmpegPath,
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", $"color=c={color}:s=320x240:r=24:d=3",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", path
        ]);
        Assert.Equal(0, create.ExitCode);
    }

    private static (byte B, byte G, byte R) CenterPixel(VideoFrame frame)
        => PixelAt(frame, frame.Width / 2, frame.Height / 2);

    private static (byte B, byte G, byte R) PixelAt(VideoFrame frame, int x, int y)
    {
        var offset = y * frame.Stride + x * 4;
        return (frame.Bgra.Span[offset], frame.Bgra.Span[offset + 1], frame.Bgra.Span[offset + 2]);
    }

    private static Task<VideoFrame> NextFrame(MediaHostClient client)
    {
        var completion = new TaskCompletionSource<VideoFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<VideoFrame>? handler = null;
        handler = (_, frame) =>
        {
            client.FramePresented -= handler;
            completion.TrySetResult(frame);
        };
        client.FramePresented += handler;
        return completion.Task;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (started.Elapsed > timeout) throw new TimeoutException("Condition was not reached.");
            await Task.Delay(25);
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        if (processId <= 0) return false;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
    }

    private static string ResolveMediaHost()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "KadrStudio.sln"))) root = root.Parent;
        if (root is null) throw new DirectoryNotFoundException("KadrStudio solution root was not found.");
        var path = Path.Combine(root.FullName, "src", "Kadr.MediaHost", "bin", "Release",
            "net10.0-windows", "win-x64", "Kadr.MediaHost.exe");
        if (!File.Exists(path)) throw new FileNotFoundException("Kadr.MediaHost test binary was not found.", path);
        return path;
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "KadrStudio", "media-host", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}
