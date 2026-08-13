using KadrStudio.Application.Jobs;
using KadrStudio.Infrastructure.Jobs;

namespace KadrStudio.Core.Tests;

public sealed class BackgroundJobSchedulerTests
{
    [Fact]
    public async Task Duplicate_requests_share_one_execution()
    {
        await using var scheduler = CreateSingleLaneScheduler();
        var executions = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new JobRequest<int>(
            JobKey.Create("thumbnail", 1), JobLane.MediaDecode, JobPriority.Normal,
            async _ =>
            {
                Interlocked.Increment(ref executions);
                await release.Task;
                return 42;
            });

        var first = scheduler.Schedule(request);
        var second = scheduler.Schedule(request);
        release.SetResult();

        Assert.Equal(42, await first.Completion);
        Assert.Equal(42, await second.Completion);
        Assert.Equal(1, executions);
        Assert.Equal(1, scheduler.GetSnapshot().DeduplicatedRequests);
    }

    [Fact]
    public async Task Higher_priority_job_runs_before_earlier_background_job()
    {
        await using var scheduler = CreateSingleLaneScheduler();
        var blockerRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        var blocker = scheduler.Schedule(new JobRequest<int>(
            JobKey.Create("blocker"), JobLane.MediaDecode, JobPriority.Normal,
            async _ => { blockerStarted.SetResult(); await blockerRelease.Task; return 0; }));
        await blockerStarted.Task;

        var low = scheduler.Schedule(new JobRequest<int>(
            JobKey.Create("low"), JobLane.MediaDecode, JobPriority.Background,
            _ => { lock (order) order.Add("low"); return ValueTask.FromResult(1); }));
        var high = scheduler.Schedule(new JobRequest<int>(
            JobKey.Create("high"), JobLane.MediaDecode, JobPriority.Realtime,
            _ => { lock (order) order.Add("high"); return ValueTask.FromResult(2); }));
        blockerRelease.SetResult();

        await Task.WhenAll(blocker.Completion, low.Completion, high.Completion);
        Assert.Equal(["high", "low"], order);
    }

    [Fact]
    public async Task Pausable_jobs_wait_while_export_is_active()
    {
        await using var scheduler = CreateSingleLaneScheduler();
        scheduler.SetExportActive(true);
        var started = false;
        var handle = scheduler.Schedule(new JobRequest<int>(
            JobKey.Create("waveform"), JobLane.MediaDecode, JobPriority.Background,
            _ => { started = true; return ValueTask.FromResult(1); }, PauseDuringExport: true));

        await Task.Delay(50);
        Assert.False(started);
        Assert.False(handle.Completion.IsCompleted);

        scheduler.SetExportActive(false);
        Assert.Equal(1, await handle.Completion.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Job_is_canceled_only_after_all_subscribers_cancel()
    {
        await using var scheduler = CreateSingleLaneScheduler();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new JobRequest<int>(
            JobKey.Create("analysis", 1), JobLane.Analysis, JobPriority.Normal,
            async token => { started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return 1; });
        var first = scheduler.Schedule(request);
        var second = scheduler.Schedule(request);
        await started.Task;

        first.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first.Completion);
        await Task.Delay(30);
        Assert.False(second.Completion.IsCompleted);
        second.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await second.Completion);
    }

    [Fact]
    public async Task Dispose_cancels_running_and_queued_work_without_leaving_tasks_alive()
    {
        var scheduler = CreateSingleLaneScheduler();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = scheduler.Schedule(new JobRequest<int>(
            JobKey.Create("long-running"), JobLane.Analysis, JobPriority.Normal,
            async token =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 1;
            }));
        var queued = scheduler.Schedule(new JobRequest<int>(
            JobKey.Create("queued"), JobLane.Analysis, JobPriority.Background,
            _ => ValueTask.FromResult(2)));
        await started.Task;

        await scheduler.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await running.Completion);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await queued.Completion);
        Assert.True(running.Completion.IsCompleted);
        Assert.True(queued.Completion.IsCompleted);
    }

    private static BackgroundJobScheduler CreateSingleLaneScheduler()
        => new(Enum.GetValues<JobLane>().ToDictionary(item => item, _ => 1));
}
