using System.Collections.Immutable;
using KadrStudio.Application.Preview;
using KadrStudio.Application.Rendering;
using KadrStudio.Core.Domain;
using KadrStudio.MediaHost;
using KadrStudio.Services;
using Xunit;

namespace KadrStudio.Integration.Tests;

public sealed class AudioWorkerSupervisorIntegrationTests
{
    [Fact(Timeout = 60_000)]
    public async Task Independent_audio_workers_mix_channels_and_survive_one_missing_source()
    {
        var root = CreateRoot();
        try
        {
            var locator = new FfmpegLocator();
            locator.EnsureAvailable();
            var tone = Path.Combine(root, "tone.wav");
            await CreateToneAsync(locator, tone);
            var project = CreateLayeredProject(tone, Path.Combine(root, "missing.wav"));
            var plan = new RenderPlanBuilder().Build(project);
            var failures = new List<Exception>();
            await using var supervisor = new AudioWorkerSupervisor(locator.FfmpegPath, failures.Add);
            AudioBlock? first = null;

            using var cancellation = new CancellationTokenSource();
            await supervisor.RunAsync(plan,
                TimelineTime.Zero, 77, block =>
                {
                    first = block;
                    cancellation.Cancel();
                    return ValueTask.CompletedTask;
                }, cancellation.Token).ContinueWith(
                    task => Assert.True(task.IsCanceled || task.IsCompletedSuccessfully),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            Assert.NotNull(first);
            Assert.Equal(77, first.Generation);
            Assert.Contains(first.InterleavedSamples.Span.ToArray(), sample => Math.Abs(sample) > 0.07f);
            Assert.NotEmpty(failures);
            Assert.All(failures, failure => Assert.IsType<AudioWorkerException>(failure));
        }
        finally { DeleteRoot(root); }
    }

    [Fact(Timeout = 60_000)]
    public async Task Constant_power_seek_midpoint_preserves_energy_and_audio_generation()
    {
        var root = CreateRoot();
        try
        {
            var locator = new FfmpegLocator();
            locator.EnsureAvailable();
            var firstPath = Path.Combine(root, "first.wav");
            var secondPath = Path.Combine(root, "second.wav");
            await CreateToneAsync(locator, firstPath, 440);
            await CreateToneAsync(locator, secondPath, 440);
            var plan = new RenderPlanBuilder().Build(CreateTransitionProject(firstPath, secondPath));
            await using var supervisor = new AudioWorkerSupervisor(locator.FfmpegPath);
            AudioBlock? captured = null;
            using var cancellation = new CancellationTokenSource();

            await supervisor.RunAsync(plan, TimelineTime.FromSeconds(2), 91, block =>
            {
                captured = block;
                cancellation.Cancel();
                return ValueTask.CompletedTask;
            }, cancellation.Token).ContinueWith(
                task => Assert.True(task.IsCanceled || task.IsCompletedSuccessfully),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            Assert.NotNull(captured);
            Assert.Equal(91, captured.Generation);
            var meter = new StereoPcmMeter().Measure(captured.InterleavedSamples.Span);
            Assert.InRange(meter.LeftRms, 0.038f, 0.05f);
            Assert.InRange(meter.RightRms, 0.038f, 0.05f);
        }
        finally { DeleteRoot(root); }
    }

    [Fact(Timeout = 60_000)]
    public async Task Clip_start_inside_mix_block_is_sample_aligned_without_early_audio()
    {
        var root = CreateRoot();
        try
        {
            var locator = new FfmpegLocator();
            locator.EnsureAvailable();
            var tone = Path.Combine(root, "aligned.wav");
            await CreateToneAsync(locator, tone);
            var project = ProjectState.CreateNew("Sample alignment") with
            {
                Sequence = new SequenceSettings(1920, 1080, FrameRate.Fps30, 48_000)
            };
            var source = CreateAudioSource(tone, "aligned");
            var track = project.Tracks.Single(item => item.Kind == TrackKind.Audio && item.Index == 0);
            project = project with
            {
                Sources = ImmutableDictionary<Guid, MediaSource>.Empty.Add(source.Id, source),
                MediaClips =
                [
                    new MediaClip(Guid.NewGuid(), source.Id, track.Id,
                        TimelineTime.FromSeconds(500d / 48_000), TimelineTime.Zero,
                        TimelineTime.FromSeconds(1), Audio: new AudioParameters())
                ]
            };
            var plan = new RenderPlanBuilder().Build(project);
            await using var supervisor = new AudioWorkerSupervisor(locator.FfmpegPath);
            AudioBlock? captured = null;
            using var cancellation = new CancellationTokenSource();

            await supervisor.RunAsync(plan, TimelineTime.Zero, 101, block =>
            {
                captured = block;
                cancellation.Cancel();
                return ValueTask.CompletedTask;
            }, cancellation.Token).ContinueWith(
                task => Assert.True(task.IsCanceled || task.IsCompletedSuccessfully),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            Assert.NotNull(captured);
            var samples = captured.InterleavedSamples.Span.ToArray();
            Assert.All(samples.Take(500 * 2), sample => Assert.Equal(0f, sample));
            Assert.Contains(samples.Skip(500 * 2), sample => Math.Abs(sample) > 0.02f);
        }
        finally { DeleteRoot(root); }
    }

    private static ProjectState CreateLayeredProject(string firstPath, string missingPath)
    {
        var project = ProjectState.CreateNew("Layered audio") with
        {
            Sequence = new SequenceSettings(1920, 1080, FrameRate.Fps30, 48_000)
        };
        var first = CreateAudioSource(firstPath, "first");
        var missing = CreateAudioSource(missingPath, "missing");
        var a1 = project.Tracks.Single(item => item.Kind == TrackKind.Audio && item.Index == 0);
        var a2 = project.Tracks.Single(item => item.Kind == TrackKind.Audio && item.Index == 1);
        return project with
        {
            Sources = ImmutableDictionary<Guid, MediaSource>.Empty.Add(first.Id, first).Add(missing.Id, missing),
            MediaClips =
            [
                new MediaClip(Guid.NewGuid(), first.Id, a1.Id, TimelineTime.Zero,
                    TimelineTime.Zero, TimelineTime.FromSeconds(2), Audio: new AudioParameters(Pan: -1)),
                new MediaClip(Guid.NewGuid(), missing.Id, a2.Id, TimelineTime.Zero,
                    TimelineTime.Zero, TimelineTime.FromSeconds(2), Audio: new AudioParameters(Pan: 1))
            ]
        };
    }

    private static ProjectState CreateTransitionProject(string firstPath, string secondPath)
    {
        var project = ProjectState.CreateNew("Audio transition") with
        {
            Sequence = new SequenceSettings(1920, 1080, FrameRate.Fps30, 48_000)
        };
        var first = CreateAudioSource(firstPath, "first");
        var second = CreateAudioSource(secondPath, "second");
        var track = project.Tracks.Single(item => item.Kind == TrackKind.Audio && item.Index == 0);
        var from = new MediaClip(Guid.NewGuid(), first.Id, track.Id, TimelineTime.Zero,
            TimelineTime.FromSeconds(0.5), TimelineTime.FromSeconds(2),
            Audio: new AudioParameters(Pan: -1));
        var to = new MediaClip(Guid.NewGuid(), second.Id, track.Id, TimelineTime.FromSeconds(2),
            TimelineTime.FromSeconds(0.5), TimelineTime.FromSeconds(2),
            Audio: new AudioParameters(Pan: 1));
        return project with
        {
            Sources = ImmutableDictionary<Guid, MediaSource>.Empty.Add(first.Id, first).Add(second.Id, second),
            MediaClips = [from, to],
            Transitions =
            [
                new TimelineTransition(Guid.NewGuid(), TransitionKind.ConstantPowerAudio, track.Id,
                    from.Id, to.Id, TimelineTime.FromSeconds(1.5), TimelineTime.FromSeconds(1))
            ]
        };
    }

    private static MediaSource CreateAudioSource(string path, string fingerprint)
        => new(Guid.NewGuid(), path, Path.GetFileName(path), MediaKind.Audio,
            TimelineTime.FromSeconds(3), true, AudioCodec: "pcm_s16le",
            FileSize: File.Exists(path) ? new FileInfo(path).Length : 0, Fingerprint: fingerprint);

    private static async Task CreateToneAsync(FfmpegLocator locator, string output, int frequency = 440)
    {
        var result = await new ProcessRunner().RunAsync(locator.FfmpegPath,
        [
            "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
            $"sine=frequency={frequency}:sample_rate=48000:duration=3", "-c:a", "pcm_s16le", output
        ]);
        Assert.Equal(0, result.ExitCode);
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "KadrStudio", "audio-workers", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}
