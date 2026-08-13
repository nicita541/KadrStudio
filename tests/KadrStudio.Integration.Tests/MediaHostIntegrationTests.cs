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
