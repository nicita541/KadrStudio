using System.Reflection;
using KadrStudio.Application.Rendering;
using KadrStudio.Core.Domain;
using KadrStudio.Infrastructure.Jobs;
using KadrStudio.Infrastructure.Rendering;
using KadrStudio.Services;
using Xunit;

namespace KadrStudio.Integration.Tests;

public sealed class RenderCancellationLifetimeTests
{
    [Fact(Timeout = 15000)]
    public async Task Reader_failure_terminates_long_running_renderer_and_preserves_failure()
    {
        var locator = new FfmpegLocator();
        await using var scheduler = new BackgroundJobScheduler();
        var engine = new FfmpegRenderEngine(locator.FfmpegPath, new FfmpegRenderCommandBuilder(), scheduler);
        using var cancellation = new CancellationTokenSource();
        var command = new ExternalRenderCommand("ffmpeg",
            ["-hide_banner", "-nostdin", "-re", "-f", "lavfi", "-i", "color=s=16x16:r=10", "-t", "60", "-f", "null", "-"], "-", "failure");
        var pending = Task.Run(() => (Task)typeof(FfmpegRenderEngine).GetMethod("ExecuteAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(engine,
            [command, TimelineTime.FromSeconds(60), new FailingProgress(), cancellation.Token])!);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("progress-reader-failed", error.Message);
        }
        finally
        {
            cancellation.Cancel();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(6)); } catch (InvalidOperationException) { }
            catch (OperationCanceledException) { }
        }
    }

    [Fact(Timeout = 20000)]
    public async Task Cancellation_waits_for_active_stderr_callback_before_returning()
    {
        var locator = new FfmpegLocator();
        await using var scheduler = new BackgroundJobScheduler();
        var engine = new FfmpegRenderEngine(locator.FfmpegPath, new FfmpegRenderCommandBuilder(), scheduler);
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new BlockingProgress(entered, release);
        var command = new ExternalRenderCommand("ffmpeg",
            ["-hide_banner", "-nostdin", "-re", "-f", "lavfi", "-i", "color=s=16x16:r=10", "-t", "60", "-f", "null", "-"], "-", "lifetime");
        var pending = Task.Run(() => (Task)typeof(FfmpegRenderEngine).GetMethod("ExecuteAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(engine,
            [command, TimelineTime.FromSeconds(60), progress, cancellation.Token])!);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
            cancellation.Cancel();
            await Task.Delay(150);
            Assert.False(pending.IsCompleted, "Render returned while its stderr callback still owned resources.");
            release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            release.Set();
            cancellation.Cancel();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(6)); } catch (OperationCanceledException) { }
        }
    }

    private sealed class BlockingProgress(TaskCompletionSource entered, ManualResetEventSlim release) : IProgress<RenderProgress>
    {
        public void Report(RenderProgress value)
        {
            entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class FailingProgress : IProgress<RenderProgress>
    {
        public void Report(RenderProgress value) => throw new InvalidOperationException("progress-reader-failed");
    }
}
