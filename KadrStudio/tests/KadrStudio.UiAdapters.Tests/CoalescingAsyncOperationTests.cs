using KadrStudio.Services;
using Xunit;

namespace KadrStudio.UiAdapters.Tests;

public sealed class CoalescingAsyncOperationTests
{
    [Fact]
    public async Task Burst_coalesces_and_never_overlaps_operations()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repeated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var work = new CoalescingAsyncOperation(async token =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
            }
            else repeated.TrySetResult();
        }, _ => { });
        Assert.True(work.Request());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var index = 0; index < 100; index++) Assert.True(work.Request());
        Assert.Equal(1, Volatile.Read(ref calls));
        release.SetResult();
        await repeated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await work.DisposeAsync();
        Assert.Equal(2, calls);
        Assert.False(work.Request());
    }

    [Fact]
    public async Task Dispose_cancels_and_awaits_cleanup()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaned = false;
        var work = new CoalescingAsyncOperation(async token =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { cleaned = true; }
        }, _ => { });
        work.Request();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await work.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cleaned);
    }

    [Fact]
    public async Task Failure_is_observed_and_next_request_can_run()
    {
        var observed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var work = new CoalescingAsyncOperation(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new IOException("fixture");
            completed.SetResult();
            return Task.CompletedTask;
        }, error => observed.TrySetResult(error));
        work.Request();
        Assert.IsType<IOException>(await observed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        work.Request();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
