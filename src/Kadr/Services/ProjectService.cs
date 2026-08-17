using System.Collections.Concurrent;
using KadrStudio.Adapters;
using KadrStudio.Infrastructure.Storage;
using KadrStudio.Models;
using KadrStudio.Application.Storage;
using KadrStudio.Core.Domain;

namespace KadrStudio.Services;

/// <summary>
/// WPF persistence adapter. Project files and crash recovery always use the
/// validated SQLite core. JSON remains private to the in-memory legacy UI undo
/// bridge until all controls emit core edit commands directly.
/// </summary>
public sealed class ProjectService : IDisposable
{
    private readonly SqliteProjectStore _projectStore;
    private readonly SqliteRecoveryStore _recoveryStore;
    private readonly EditorProjectMapper _mapper = new();
    private readonly ConcurrentDictionary<Guid, long> _revisions = [];
    private readonly SemaphoreSlim _storageGate = new(1, 1);
    private Guid? _pendingRecoveryId;
    private ProjectFileLease? _projectLease;
    private int _disposed;

    public ProjectService(string? recoveryRoot = null)
    {
        _projectStore = new SqliteProjectStore();
        _recoveryStore = new SqliteRecoveryStore(recoveryRoot);
    }

    public async Task SaveAsync(EditorProject project, string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        await _storageGate.WaitAsync(cancellationToken);
        try
        {
            EnsureLease(fullPath);
            project.UpdatedAt = DateTimeOffset.UtcNow;
            var core = _mapper.ToCore(project, NextRevision(project.Id));
            await _projectStore.SaveAsync(fullPath, core, cancellationToken);
            project.FilePath = fullPath;
            await _recoveryStore.DeleteAsync(project.Id, cancellationToken: cancellationToken);
            if (_pendingRecoveryId == project.Id) _pendingRecoveryId = null;
        }
        finally
        {
            _storageGate.Release();
        }
    }

    public async Task<EditorProject> OpenAsync(string path, CancellationToken cancellationToken = default)
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
            _revisions[core.Id] = core.Revision;
            return _mapper.ToUi(core, fullPath);
        }
        finally
        {
            _storageGate.Release();
        }
    }

    public Task SaveAutosaveAsync(EditorProject project, CancellationToken cancellationToken = default)
        => SaveAutosaveVersionAsync(project, "Automatic recovery after editing", cancellationToken);

    public async Task SaveAutosaveVersionAsync(
        EditorProject project,
        string reason,
        CancellationToken cancellationToken = default)
    {
        await _storageGate.WaitAsync(cancellationToken);
        try
        {
            project.UpdatedAt = DateTimeOffset.UtcNow;
            var core = _mapper.ToCore(project, NextRevision(project.Id));
            await _recoveryStore.SaveAsync(core, reason, cancellationToken);
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

    public Task<EditorProject> OpenAutosaveAsync(CancellationToken cancellationToken = default)
        => OpenAutosaveVersionAsync(null, null, cancellationToken);

    public async Task<EditorProject> OpenAutosaveVersionAsync(
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
            _revisions[core.Id] = core.Revision;
            _pendingRecoveryId = core.Id;
            return _mapper.ToUi(core);
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

    public string CreateSnapshot(EditorProject project) => ProjectJson.Serialize(project);

    public EditorProject RestoreSnapshot(string snapshot, string? filePath)
    {
        var project = ProjectJson.Deserialize(snapshot);
        project.FilePath = filePath;
        return project;
    }

    private long NextRevision(Guid projectId)
        => _revisions.AddOrUpdate(projectId, 1, static (_, revision) => checked(revision + 1));

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
