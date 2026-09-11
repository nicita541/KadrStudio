using KadrStudio.Application.Preview;
using KadrStudio.Application.Rendering;
using KadrStudio.Core.Domain;
using KadrStudio.MediaHost;
using Xunit;

namespace KadrStudio.Integration.Tests;

public sealed class VideoReadCancellationTests
{
    [Fact(Timeout = 15000)]
    public async Task Partial_frame_stall_does_not_block_command_caller_and_cancels()
    {
        var root = Path.Combine(Path.GetTempPath(), "kadr-stalled-decoder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var readyPath = Path.Combine(root, "partial-written");
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var range = new TimeRange(TimelineTime.Zero, TimelineTime.FromSeconds(1));
        var plan = new RenderPlan(Guid.NewGuid(), 0, 4, 4, FrameRate.Fps30, range,
            [new RenderVisualLayer(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0,
                executable, MediaKind.Video, range, TimelineTime.Zero, new VideoParameters())],
            [], [], "video", "audio", "overlay", "content");
        await using var supervisor = new VideoWorkerSupervisor(executable, commands: new StalledDecoder(readyPath));
        using var cancellation = new CancellationTokenSource();
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = Task.Run(async () =>
        {
            var pending = supervisor.RunAsync(plan,
                new PreviewRequest(TimelineTime.Zero, FrameRate.Fps30, 4, 4, false, new PreviewGeneration(1, 1, 1)),
                TimelineTime.Zero, false, frame => { frame.Owner?.Dispose(); return ValueTask.CompletedTask; }, cancellation.Token);
            returned.SetResult();
            await pending;
        });
        try
        {
            await returned.Task.WaitAsync(TimeSpan.FromSeconds(2));
            using var readyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!File.Exists(readyPath)) await Task.Delay(10, readyTimeout.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => caller.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally
        {
            cancellation.Cancel();
            await supervisor.DisposeAsync();
            try { await caller.WaitAsync(TimeSpan.FromSeconds(3)); } catch (OperationCanceledException) { }
            Directory.Delete(root, true);
        }
    }

    private sealed class StalledDecoder(string readyPath) : IRenderCommandBuilder
    {
        public ExternalRenderCommand Build(RenderPlan plan, RenderOutputOptions options)
            => new("test-decoder", ["-NoProfile", "-NonInteractive", "-Command",
                "[Console]::OpenStandardOutput().WriteByte(0); [IO.File]::WriteAllText('" +
                readyPath.Replace("'", "''", StringComparison.Ordinal) + "', 'ready'); Start-Sleep -Seconds 30"], "pipe:1", "stalled");
    }
}
