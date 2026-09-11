namespace KadrStudio.Application.Storage;

/// <summary>Orders document persistence and remembers committed paths across Save As.</summary>
public sealed class ProjectDocumentCoordinator(string historyRoot)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, string> _paths = [];
    public string HistoryRoot { get; } = Path.GetFullPath(historyRoot);

    public async ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(_gate);
    }

    // These methods are called only while holding the document lease.
    public string ResolveHistoryPath(Guid projectId, string? requestedPath)
        => _paths.TryGetValue(projectId, out var committed) ? committed
            : !string.IsNullOrWhiteSpace(requestedPath) ? Path.GetFullPath(requestedPath)
            : Path.Combine(HistoryRoot, $"{projectId:N}.history.kadr");

    public void RegisterCommittedPath(Guid projectId, string path) => _paths[projectId] = Path.GetFullPath(path);

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
