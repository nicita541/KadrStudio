using KadrStudio.Infrastructure.Storage;
using KadrStudio.Application.Storage;
using KadrStudio.Core.Domain;

namespace KadrStudio.Services;

/// <summary>
/// Serializes validated immutable project snapshots and owns the cross-process
/// write lease. WPF projections never enter persistence.
/// </summary>
public sealed class ProjectService : IDisposable
{
    private readonly SqliteProjectStore _projectStore;
    private readonly SqliteRecoveryStore _recoveryStore;
    private readonly ProjectDocumentCoordinator _coordinator;
    private Guid? _pendingRecoveryId;
    private ProjectFileLease? _projectLease;
    private int _disposed;

    public ProjectService(string? recoveryRoot = null, ProjectDocumentCoordinator? coordinator = null)
    {
        _coordinator = coordinator ?? new ProjectDocumentCoordinator(KadrLocalDataPaths.HistoryRoot);
        _projectStore = new SqliteProjectStore();
        _recoveryStore = new SqliteRecoveryStore(recoveryRoot);
    }

    public async Task SaveAsync(ProjectState project, string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var storageLease = await _coordinator.EnterAsync(cancellationToken);
        try
        {
            var lease = AcquireReplacementLease(fullPath);
            try
            {
                await _projectStore.SaveWithHistoryAsync(fullPath, project,
                    _coordinator.ResolveHistoryPath(project.Id, _projectLease?.ProjectPath), cancellationToken);
            }
            catch
            {
                if (!ReferenceEquals(lease, _projectLease)) lease.Dispose();
                throw;
            }
            ReplaceLease(lease);
            _coordinator.RegisterCommittedPath(project.Id, fullPath);
            // Never erase a recovery snapshot that represents different edits.
            try
            {
                if (await _recoveryStore.IsLatestSnapshotAsync(project, CancellationToken.None))
                {
                    await _recoveryStore.DeleteAsync(project.Id, cancellationToken: CancellationToken.None);
                    if (_pendingRecoveryId == project.Id) _pendingRecoveryId = null;
                }
            }
            catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException)
            {
                System.Diagnostics.Trace.TraceWarning("Saved project; recovery cleanup deferred: {0}", exception.Message);
            }
        }
        finally
        {
            storageLease.Dispose();
        }
    }

    public async Task<ProjectState> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var storageLease = await _coordinator.EnterAsync(cancellationToken);
        try
        {
            var lease = AcquireReplacementLease(fullPath);
            ProjectState core;
            try
            {
                core = await _projectStore.LoadAsync(fullPath, cancellationToken);
            }
            catch
            {
                if (!ReferenceEquals(lease, _projectLease)) lease.Dispose();
                throw;
            }
            ReplaceLease(lease);
            _coordinator.RegisterCommittedPath(core.Id, fullPath);
            return core;
        }
        finally
        {
            storageLease.Dispose();
        }
    }

    public Task SaveAutosaveAsync(ProjectState project, CancellationToken cancellationToken = default)
        => SaveAutosaveVersionAsync(project, "Automatic recovery after editing", cancellationToken);

    public async Task SaveAutosaveVersionAsync(
        ProjectState project,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var storageLease = await _coordinator.EnterAsync(cancellationToken);
        try
        {
            await _recoveryStore.SaveAsync(project, reason, cancellationToken);
            _pendingRecoveryId = project.Id;
        }
        finally
        {
            storageLease.Dispose();
        }
    }

    public async Task<bool> HasAutosaveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var storageLease = await _coordinator.EnterAsync(cancellationToken);
            try
            {
                var recovery = (await _recoveryStore.ListAsync(cancellationToken)).FirstOrDefault();
                _pendingRecoveryId = recovery?.ProjectId;
                return recovery is not null;
            }
            finally
            {
                storageLease.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<RecoveryProjectInfo>> ListAutosavesAsync(
        CancellationToken cancellationToken = default)
    {
        var storageLease = await _coordinator.EnterAsync(cancellationToken);
        try { return await _recoveryStore.ListAsync(cancellationToken); }
        finally { storageLease.Dispose(); }
    }

    public Task<ProjectState> OpenAutosaveAsync(CancellationToken cancellationToken = default)
        => OpenAutosaveVersionAsync(null, null, cancellationToken);

    public async Task<ProjectState> OpenAutosaveVersionAsync(
        Guid? projectId,
        Guid? recoveryId,
        CancellationToken cancellationToken = default)
    {
        var storageLease = await _coordinator.EnterAsync(cancellationToken);
        try
        {
            var id = projectId ?? _pendingRecoveryId;
            if (id is null)
            {
                var latest = (await _recoveryStore.ListAsync(cancellationToken)).FirstOrDefault()
                    ?? throw new FileNotFoundException("No recovery project was found.");
                id = latest.ProjectId;
            }
            var core = await _recoveryStore.LoadAsync(id.Value, recoveryId, cancellationToken)
                ?? throw new FileNotFoundException("The recovery project no longer exists.");
            _pendingRecoveryId = core.Id;
            return core;
        }
        finally
        {
            storageLease.Dispose();
        }
    }

    public Task DeleteAutosaveAsync(CancellationToken cancellationToken = default)
        => DeleteAutosaveVersionAsync(null, null, cancellationToken);

    public async Task DeleteAutosaveVersionAsync(
        Guid? projectId,
        Guid? recoveryId,
        CancellationToken cancellationToken = default)
    {
        var storageLease = await _coordinator.EnterAsync(cancellationToken);
        try
        {
            var targetProjectId = projectId ?? _pendingRecoveryId;
            if (targetProjectId is not { } id) return;
            try
            {
                await _recoveryStore.DeleteAsync(id, recoveryId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Recovery cleanup must not make a successfully saved project unusable.
            }
            if (recoveryId is null && _pendingRecoveryId == id) _pendingRecoveryId = null;
        }
        finally
        {
            storageLease.Dispose();
        }
    }

    public async Task CloseDocumentAsync(CancellationToken cancellationToken = default)
    {
        using var operation = await _coordinator.EnterAsync(cancellationToken);
        _projectLease?.Dispose();
        _projectLease = null;
        _pendingRecoveryId = null;
    }

    private ProjectFileLease AcquireReplacementLease(string fullPath)
        => _projectLease is not null &&
           string.Equals(_projectLease.ProjectPath, fullPath, StringComparison.OrdinalIgnoreCase)
            ? _projectLease
            : ProjectFileLease.Acquire(fullPath);

    private void ReplaceLease(ProjectFileLease lease)
    {
        if (ReferenceEquals(_projectLease, lease)) return;
        var previous = _projectLease;
        _projectLease = lease;
        previous?.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _projectLease?.Dispose();

    }
}
