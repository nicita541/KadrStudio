namespace KadrStudio.Services;

public sealed class CoalescingAsyncOperation(Func<CancellationToken, Task> operation, Action<Exception> onError) : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task _worker = Task.CompletedTask;
    private Task? _disposal;
    private bool _running;
    private bool _pending;
    private bool _disposed;

    public bool Request()
    {
        lock (_sync)
        {
            if (_disposed) return false;
            _pending = true;
            if (!_running)
            {
                _running = true;
                _worker = Task.Run(DrainAsync);
            }
            return true;
        }
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            lock (_sync)
            {
                if (_disposed || !_pending) { _running = false; return; }
                _pending = false;
            }
            try { await operation(_lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception error)
            {
                try { onError(error); }
                catch (Exception reportingError) { System.Diagnostics.Trace.TraceError("Async error reporting failed: {0}", reportingError); }
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposal is not null) return new ValueTask(_disposal);
            _disposed = true;
            var worker = _worker;
            _disposal = Task.Run(async () =>
            {
                try
                {
                    await _lifetime.CancelAsync().ConfigureAwait(false);
                    await worker.ConfigureAwait(false);
                }
                finally { _lifetime.Dispose(); }
            });
            return new ValueTask(_disposal);
        }
    }
}
