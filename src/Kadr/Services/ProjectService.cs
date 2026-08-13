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
    private readonly SqliteProjectStore _projectStore = new();
    private readonly SqliteRecoveryStore _recoveryStore = new();
    private readonly EditorProjectMapper _mapper = new();
    private readonly Dictionary<Guid, long> _revisions = [];
    private Guid? _pendingRecoveryId;

    public async Task SaveAsync(EditorProject project, string path, CancellationToken cancellationToken = default)
    {
        project.UpdatedAt = DateTimeOffset.UtcNow;
        var revision = NextRevision(project.Id);
        var core = _mapper.ToCore(project, revision);
        var fullPath = Path.GetFullPath(path);
        await _projectStore.SaveAsync(fullPath, core, cancellationToken).ConfigureAwait(false);
        project.FilePath = fullPath;
        await _recoveryStore.DeleteAsync(project.Id, cancellationToken).ConfigureAwait(false);
        if (_pendingRecoveryId == project.Id) _pendingRecoveryId = null;
    }

    public async Task<EditorProject> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var core = await _projectStore.LoadAsync(fullPath, cancellationToken).ConfigureAwait(false);
        _revisions[core.Id] = core.Revision;
        return _mapper.ToUi(core, fullPath);
    }

    public async Task SaveAutosaveAsync(EditorProject project, CancellationToken cancellationToken = default)
    {
        project.UpdatedAt = DateTimeOffset.UtcNow;
        var core = _mapper.ToCore(project, NextRevision(project.Id));
        await _recoveryStore.SaveAsync(core, "Automatic recovery after editing", cancellationToken).ConfigureAwait(false);
        _pendingRecoveryId = project.Id;
    }

    public bool AutosaveExists
    {
        get
        {
            try
            {
                var recovery = _recoveryStore.ListAsync().GetAwaiter().GetResult().FirstOrDefault();
                _pendingRecoveryId = recovery?.ProjectId;
                return recovery is not null;
            }
            catch
            {
                return false;
            }
        }
    }

    public async Task<EditorProject> OpenAutosaveAsync(CancellationToken cancellationToken = default)
    {
        var id = _pendingRecoveryId;
        if (id is null)
        {
            var latest = (await _recoveryStore.ListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault()
                ?? throw new FileNotFoundException("No recovery project was found.");
            id = latest.ProjectId;
        }
        var core = await _recoveryStore.LoadAsync(id.Value, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The recovery project no longer exists.");
        _revisions[core.Id] = core.Revision;
        _pendingRecoveryId = core.Id;
        return _mapper.ToUi(core);
    }

    public void DeleteAutosave()
    {
        if (_pendingRecoveryId is not { } projectId) return;
        try { _recoveryStore.DeleteAsync(projectId).GetAwaiter().GetResult(); } catch { }
        _pendingRecoveryId = null;
    }

    public string CreateSnapshot(EditorProject project) => ProjectJson.Serialize(project);

    public EditorProject RestoreSnapshot(string snapshot, string? filePath)
    {
        var project = ProjectJson.Deserialize(snapshot);
        project.FilePath = filePath;
        return project;
    }

    private long NextRevision(Guid projectId)
    {
        var next = _revisions.TryGetValue(projectId, out var revision) ? checked(revision + 1) : 1;
        _revisions[projectId] = next;
        return next;
    }
}
