using KadrStudio.Application.Jobs;

namespace KadrStudio.Infrastructure.Jobs;

public sealed class BackgroundJobScheduler : IBackgroundJobScheduler
{
    private readonly object _gate = new();
    private readonly PriorityQueue<JobEntry, (int Priority, long Sequence)> _queue = new();
    private readonly Dictionary<JobKey, JobEntry> _entries = [];
    private readonly Dictionary<JobLane, int> _runningByLane = Enum.GetValues<JobLane>().ToDictionary(item => item, _ => 0);
    private readonly IReadOnlyDictionary<JobLane, int> _laneLimits;
    private readonly SemaphoreSlim _changed = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _dispatcher;
    private long _sequence;
    private int _deduplicatedRequests;
    private bool _manualExportActive;
    private bool _disposed;

    public BackgroundJobScheduler(IReadOnlyDictionary<JobLane, int>? laneLimits = null)
    {
        _laneLimits = laneLimits ?? new Dictionary<JobLane, int>
        {
            [JobLane.Interactive] = 2,
            [JobLane.MediaDecode] = Math.Max(2, Environment.ProcessorCount / 4),
            [JobLane.Analysis] = 1,
            [JobLane.Export] = 1,
            [JobLane.Storage] = 1
        };
        foreach (var lane in Enum.GetValues<JobLane>())
            if (!_laneLimits.TryGetValue(lane, out var limit) || limit < 1)
                throw new ArgumentOutOfRangeException(nameof(laneLimits), $"Lane {lane} must have a positive limit.");
        _dispatcher = Task.Run(DispatchAsync);
    }

    public JobHandle<TResult> Schedule<TResult>(JobRequest<TResult> request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Work);
        if (string.IsNullOrWhiteSpace(request.Key.Value)) throw new ArgumentException("A job key is required.", nameof(request));

        JobEntry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(request.Key, out entry!))
            {
                if (entry.ResultType != typeof(TResult))
                    throw new InvalidOperationException($"Job key '{request.Key}' is already used for {entry.ResultType.Name}.");
                entry.SubscriberCount++;
                _deduplicatedRequests++;
            }
            else
            {
                entry = new JobEntry(
                    request.Key,
                    request.Lane,
                    request.Priority,
                    request.PauseDuringExport,
                    typeof(TResult),
                    async token => await request.Work(token).ConfigureAwait(false));
                _entries.Add(entry.Key, entry);
                _queue.Enqueue(entry, ((int)entry.Priority, _sequence++));
                SignalChanged();
            }
        }

        var subscription = new CancellationTokenSource();
        return new JobHandle<TResult>(
            entry.Key,
            AwaitSubscriberAsync<TResult>(entry.Completion.Task, subscription),
            () =>
            {
                subscription.Cancel();
                CancelSubscriber(entry);
            });
    }

    public void SetExportActive(bool active)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _manualExportActive = active;
            SignalChanged();
        }
    }

    public SchedulerSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var active = _entries.Values.Where(item => item.State != JobState.Completed).ToArray();
            return new SchedulerSnapshot(
                active.Count(item => item.State == JobState.Queued),
                active.Count(item => item.State == JobState.Running),
                _deduplicatedRequests,
                IsExportActiveUnsafe(),
                Enum.GetValues<JobLane>().ToDictionary(
                    lane => lane,
                    lane => active.Count(item => item.State == JobState.Queued && item.Lane == lane)),
                new Dictionary<JobLane, int>(_runningByLane));
        }
    }

    public async ValueTask DisposeAsync()
    {
        JobEntry[] entries;
        JobEntry[] running;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            entries = _entries.Values.ToArray();
            _shutdown.Cancel();
            foreach (var entry in entries)
            {
                entry.Cancellation.Cancel();
                if (entry.State == JobState.Queued) CompleteCanceledUnsafe(entry);
            }
            running = entries.Where(item => item.State == JobState.Running).ToArray();
            SignalChanged();
        }
        try { await _dispatcher.ConfigureAwait(false); } catch (OperationCanceledException) { }
        foreach (var entry in running)
        {
            try { await entry.Completion.Task.ConfigureAwait(false); } catch (OperationCanceledException) { } catch { }
        }
        foreach (var entry in entries) entry.Cancellation.Dispose();
        _shutdown.Dispose();
        _changed.Dispose();
    }

    private async Task DispatchAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            await _changed.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            while (true)
            {
                JobEntry? entry;
                lock (_gate)
                {
                    entry = TakeRunnableUnsafe();
                    if (entry is null) break;
                    entry.State = JobState.Running;
                    _runningByLane[entry.Lane]++;
                }
                _ = Task.Run(() => ExecuteAsync(entry), CancellationToken.None);
            }
        }
    }

    private JobEntry? TakeRunnableUnsafe()
    {
        var skipped = new List<(JobEntry Entry, (int Priority, long Sequence) Priority)>();
        JobEntry? selected = null;
        while (_queue.TryDequeue(out var candidate, out var priority))
        {
            if (candidate.State == JobState.Completed) continue;
            if (candidate.Cancellation.IsCancellationRequested)
            {
                CompleteCanceledUnsafe(candidate);
                continue;
            }
            if (_runningByLane[candidate.Lane] >= _laneLimits[candidate.Lane] ||
                candidate.PauseDuringExport && IsExportActiveUnsafe())
            {
                skipped.Add((candidate, priority));
                continue;
            }
            selected = candidate;
            break;
        }
        foreach (var item in skipped) _queue.Enqueue(item.Entry, item.Priority);
        return selected;
    }

    private async Task ExecuteAsync(JobEntry entry)
    {
        try
        {
            var result = await entry.Work(entry.Cancellation.Token).ConfigureAwait(false);
            entry.Completion.TrySetResult(result);
        }
        catch (OperationCanceledException) when (entry.Cancellation.IsCancellationRequested)
        {
            entry.Completion.TrySetCanceled(entry.Cancellation.Token);
        }
        catch (Exception exception)
        {
            entry.Completion.TrySetException(exception);
        }
        finally
        {
            lock (_gate)
            {
                if (entry.State == JobState.Running) _runningByLane[entry.Lane]--;
                entry.State = JobState.Completed;
                _entries.Remove(entry.Key);
                SignalChanged();
            }
        }
    }

    private void CancelSubscriber(JobEntry entry)
    {
        lock (_gate)
        {
            if (entry.State == JobState.Completed || entry.SubscriberCount <= 0) return;
            entry.SubscriberCount--;
            if (entry.SubscriberCount == 0) entry.Cancellation.Cancel();
            SignalChanged();
        }
    }

    private void CompleteCanceledUnsafe(JobEntry entry)
    {
        entry.State = JobState.Completed;
        _entries.Remove(entry.Key);
        entry.Completion.TrySetCanceled(entry.Cancellation.Token);
    }

    private bool IsExportActiveUnsafe()
        => _manualExportActive || _runningByLane[JobLane.Export] > 0;

    private void SignalChanged()
    {
        try { _changed.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { }
    }

    private static async Task<TResult> AwaitSubscriberAsync<TResult>(
        Task<object?> completion,
        CancellationTokenSource subscription)
    {
        try
        {
            return (TResult)(await completion.WaitAsync(subscription.Token).ConfigureAwait(false))!;
        }
        finally
        {
            subscription.Dispose();
        }
    }

    private enum JobState
    {
        Queued,
        Running,
        Completed
    }

    private sealed class JobEntry(
        JobKey key,
        JobLane lane,
        JobPriority priority,
        bool pauseDuringExport,
        Type resultType,
        Func<CancellationToken, ValueTask<object?>> work)
    {
        public JobKey Key { get; } = key;
        public JobLane Lane { get; } = lane;
        public JobPriority Priority { get; } = priority;
        public bool PauseDuringExport { get; } = pauseDuringExport;
        public Type ResultType { get; } = resultType;
        public Func<CancellationToken, ValueTask<object?>> Work { get; } = work;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<object?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SubscriberCount { get; set; } = 1;
        public JobState State { get; set; }
    }
}
