using System.Collections.Concurrent;
using KadrStudio.Adapters;
using KadrStudio.Infrastructure.Storage;
using KadrStudio.Models;

namespace KadrStudio.Services;

/// <summary>
/// WPF persistence adapter. Project files and crash recovery always use the
/// validated SQLite core. JSON remains private to the in-memory legacy UI undo
/// bridge until all controls emit core edit commands directly.
/// </summary>
public sealed class ProjectService
{
    private readonly SqliteProjectStore _projectStore;
    private readonly SqliteRecoveryStore _recoveryStore;
    private readonly EditorProjectMapper _mapper = new();
    private readonly ConcurrentDictionary<Guid, long> _revisions = [];
    private readonly SemaphoreSlim _storageGate = new(1, 1);
    private Guid? _pendingRecoveryId;

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
            project.UpdatedAt = DateTimeOffset.UtcNow;
            var core = _mapper.ToCore(project, NextRevision(project.Id));
            await _projectStore.SaveAsync(fullPath, core, cancellationToken);
            project.FilePath = fullPath;
            await _recoveryStore.DeleteAsync(project.Id, cancellationToken);
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
            var core = await _projectStore.LoadAsync(fullPath, cancellationToken);
            _revisions[core.Id] = core.Revision;
            return _mapper.ToUi(core, fullPath);
        }
        finally
        {
            _storageGate.Release();
        }
    }

    public async Task SaveAutosaveAsync(EditorProject project, CancellationToken cancellationToken = default)
    {
        await _storageGate.WaitAsync(cancellationToken);
        try
        {
            project.UpdatedAt = DateTimeOffset.UtcNow;
            var core = _mapper.ToCore(project, NextRevision(project.Id));
            await _recoveryStore.SaveAsync(core, "Automatic recovery after editing", cancellationToken);
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

    public async Task<EditorProject> OpenAutosaveAsync(CancellationToken cancellationToken = default)
    {
        await _storageGate.WaitAsync(cancellationToken);
        try
        {
            var id = _pendingRecoveryId;
            if (id is null)
            {
                var latest = (await _recoveryStore.ListAsync(cancellationToken)).FirstOrDefault()
                    ?? throw new FileNotFoundException("No recovery project was found.");
                id = latest.ProjectId;
            }
            var core = await _recoveryStore.LoadAsync(id.Value, cancellationToken)
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

    public async Task DeleteAutosaveAsync(CancellationToken cancellationToken = default)
    {
        await _storageGate.WaitAsync(cancellationToken);
        try
        {
            if (_pendingRecoveryId is not { } projectId) return;
            try
            {
                await _recoveryStore.DeleteAsync(projectId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Recovery cleanup must not make a successfully saved project unusable.
            }
            _pendingRecoveryId = null;
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
}
