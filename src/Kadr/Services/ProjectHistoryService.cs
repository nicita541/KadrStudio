using KadrStudio.Adapters;
using KadrStudio.Infrastructure.Storage;
using KadrStudio.Models;

namespace KadrStudio.Services;

/// <summary>
/// Stores checkpoints inside the SQLite project document. Unsaved projects use
/// an isolated local SQLite history document and never spill JSON snapshots.
/// </summary>
public sealed class ProjectHistoryService
{
    private readonly SqliteProjectStore _store = new();
    private readonly EditorProjectMapper _mapper = new();
    private readonly string _historyRoot;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public ProjectHistoryService(string? historyRoot = null)
    {
        _historyRoot = Path.GetFullPath(historyRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kadr Studio", "History"));
        Directory.CreateDirectory(_historyRoot);
    }

    public async Task<ProjectHistoryEntry> CreateCheckpointAsync(
        EditorProject project,
        string message,
        string? existingSnapshot = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var checkpointProject = existingSnapshot is null ? project : ProjectJson.Deserialize(existingSnapshot);
        var core = _mapper.ToCore(checkpointProject);
        var path = GetHistoryPath(project);
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(path))
                await _store.SaveAsync(path, core, cancellationToken);
            var info = await _store.CreateCheckpointAsync(path, core, NormalizeMessage(message), cancellationToken);
            return ToEntry(info.Id, info.ProjectId, info.CreatedAt, info.Name, path);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<IReadOnlyList<ProjectHistoryEntry>> GetCheckpointsAsync(
        EditorProject project,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var path = GetHistoryPath(project);
        if (!File.Exists(path)) return [];
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            return (await _store.GetCheckpointsAsync(path, cancellationToken))
                .Select(item => ToEntry(item.Id, item.ProjectId, item.CreatedAt, item.Name, path))
                .ToArray();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<EditorProject> RestoreCheckpointAsync(
        ProjectHistoryEntry entry,
        string? projectFilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var path = entry.StoragePath ?? throw new InvalidOperationException("The checkpoint storage path is missing.");
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var core = await _store.RestoreCheckpointAsync(path, entry.Id, cancellationToken);
            return _mapper.ToUi(core, projectFilePath);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task DeleteCheckpointAsync(
        ProjectHistoryEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var path = entry.StoragePath ?? throw new InvalidOperationException("The checkpoint storage path is missing.");
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            await _store.DeleteCheckpointAsync(path, entry.Id, cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private string GetHistoryPath(EditorProject project)
        => string.IsNullOrWhiteSpace(project.FilePath)
            ? Path.Combine(_historyRoot, $"{project.Id:N}.history.kadr")
            : Path.GetFullPath(project.FilePath);

    private static ProjectHistoryEntry ToEntry(
        Guid id,
        Guid projectId,
        DateTimeOffset createdAt,
        string message,
        string storagePath)
        => new()
        {
            Id = id,
            ProjectId = projectId,
            CreatedAt = createdAt,
            Message = message,
            StoragePath = storagePath
        };

    private static string NormalizeMessage(string message)
        => string.IsNullOrWhiteSpace(message) ? "Checkpoint" : message.Trim();
}

public sealed class ProjectHistoryEntry
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Message { get; set; } = string.Empty;
    public string CreatedLabel => CreatedAt.LocalDateTime.ToString("dd.MM.yyyy  HH:mm:ss");
    internal string? StoragePath { get; set; }
}
