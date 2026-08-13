namespace KadrStudio.Application.Jobs;

public enum JobPriority
{
    Realtime = 0,
    UserInitiated = 1,
    Normal = 2,
    Background = 3,
    Maintenance = 4
}

public enum JobLane
{
    Interactive,
    MediaDecode,
    Analysis,
    Export,
    Storage
}

public readonly record struct JobKey(string Value)
{
    public override string ToString() => Value;

    public static JobKey Create(string subsystem, params object?[] parts)
    {
        if (string.IsNullOrWhiteSpace(subsystem)) throw new ArgumentException("A subsystem is required.", nameof(subsystem));
        return new JobKey(string.Join(':', new[] { subsystem.Trim() }.Concat(parts.Select(FormatPart))));
    }

    private static string FormatPart(object? value) => value switch
    {
        null => "-",
        Guid guid => guid.ToString("N"),
        IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture) ?? "-",
        _ => value.ToString() ?? "-"
    };
}

public sealed record JobRequest<TResult>(
    JobKey Key,
    JobLane Lane,
    JobPriority Priority,
    Func<CancellationToken, ValueTask<TResult>> Work,
    bool PauseDuringExport = false);

public sealed class JobHandle<TResult>
{
    private readonly Action _cancel;
    private int _isCanceled;

    public JobHandle(JobKey key, Task<TResult> completion, Action cancel)
    {
        Key = key;
        Completion = completion;
        _cancel = cancel;
    }

    public JobKey Key { get; }
    public Task<TResult> Completion { get; }
    public void Cancel()
    {
        if (Interlocked.Exchange(ref _isCanceled, 1) == 0) _cancel();
    }
}

public sealed record SchedulerSnapshot(
    int Queued,
    int Running,
    int DeduplicatedRequests,
    bool IsExportActive,
    IReadOnlyDictionary<JobLane, int> QueuedByLane,
    IReadOnlyDictionary<JobLane, int> RunningByLane);

public interface IBackgroundJobScheduler : IAsyncDisposable
{
    JobHandle<TResult> Schedule<TResult>(JobRequest<TResult> request);
    void SetExportActive(bool active);
    SchedulerSnapshot GetSnapshot();
}
