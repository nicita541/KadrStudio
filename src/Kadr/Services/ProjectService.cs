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
    private readonly SemaphoreSlim _storageGate = new(1, 1);
    private Guid? _pendingRecoveryId;
    private ProjectFileLease? _projectLease;
    private int _disposed;

    public ProjectService(string? recoveryRoot = null)
    {
        _projectStore = new SqliteProjectStore();
        _recoveryStore = new SqliteRecoveryStore(recoveryRoot);
    }

    public async Task SaveAsync(ProjectState project, string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        await _storageGate.WaitAsync(cancellationToken);
        try
        {
            EnsureLease(fullPath);
            await _projectStore.SaveAsync(fullPath, project, cancellationToken);
            await _recoveryStore.DeleteAsync(project.Id, cancellationToken: cancellationToken);
            if (_pendingRecoveryId == project.Id) _pendingRecoveryId = null;
        }
        finally
        {
            _storageGate.Release();
        }
    }

    public async Task<ProjectState> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        await _storageGate.WaitAsync(cancellationToken);
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
            return core;
        }
        finally
        {
            _storageGate.Release();
        }
    }

    public Task SaveAutosaveAsync(ProjectState project, CancellationToken cancellationToken = default)
        => SaveAutosaveVersionAsync(project, "Automatic recovery after editing", cancellationToken);

    public async Task SaveAutosaveVersionAsync(
        ProjectState project,
        string reason,
        CancellationToken cancellationToken = default)
    {
        await _storageGate.WaitAsync(cancellationToken);
        try
        {
            await _recoveryStore.SaveAsync(project, reason, cancellationToken);
            _pendingRecoveryId = project.Id;
        }
        finally
        {
            _storageGate.Release();
        }
    }

    public async Task<bool> HasAutosaveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _storageGate.WaitAsync(cancellationToken);
            try
            {
                var recovery = (await _recoveryStore.ListAsync(cancellationToken)).FirstOrDefault();
                _pendingRecoveryId = recovery?.ProjectId;
                return recovery is not null;
            }
            finally
            {
                _storageGate.Release();
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
        await _storageGate.WaitAsync(cancellationToken);
        try { return await _recoveryStore.ListAsync(cancellationToken); }
        finally { _storageGate.Release(); }
    }

    public Task<ProjectState> OpenAutosaveAsync(CancellationToken cancellationToken = default)
        => OpenAutosaveVersionAsync(null, null, cancellationToken);

    public async Task<ProjectState> OpenAutosaveVersionAsync(
        Guid? projectId,
        Guid? recoveryId,
        CancellationToken cancellationToken = default)
    {
        await _storageGate.WaitAsync(cancellationToken);
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
            _storageGate.Release();
        }
    }

    public Task DeleteAutosaveAsync(CancellationToken cancellationToken = default)
        => DeleteAutosaveVersionAsync(null, null, cancellationToken);

    public async Task DeleteAutosaveVersionAsync(
        Guid? projectId,
        Guid? recoveryId,
        CancellationToken cancellationToken = default)
    {
        await _storageGate.WaitAsync(cancellationToken);
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
            _storageGate.Release();
        }
    }

    private void EnsureLease(string fullPath)
    {
        if (_projectLease is not null &&
            string.Equals(_projectLease.ProjectPath, fullPath, StringComparison.OrdinalIgnoreCase)) return;
        ReplaceLease(ProjectFileLease.Acquire(fullPath));
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
        _storageGate.Dispose();
    }
}
