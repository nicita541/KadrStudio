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

    public ProjectHistoryService(string? historyRoot = null)
    {
        _historyRoot = Path.GetFullPath(historyRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kadr Studio", "History"));
        Directory.CreateDirectory(_historyRoot);
    }

    public ProjectHistoryEntry CreateCheckpoint(
        EditorProject project,
        string message,
        string? existingSnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var checkpointProject = existingSnapshot is null ? project : ProjectJson.Deserialize(existingSnapshot);
        var path = EnsureHistoryDocument(project);
        var core = _mapper.ToCore(checkpointProject);
        var info = _store.CreateCheckpointAsync(path, core, NormalizeMessage(message)).GetAwaiter().GetResult();
        return ToEntry(info.Id, info.ProjectId, info.CreatedAt, info.Name, path);
    }

    public IReadOnlyList<ProjectHistoryEntry> GetCheckpoints(EditorProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var path = GetHistoryPath(project);
        if (!File.Exists(path)) return [];
        return _store.GetCheckpointsAsync(path).GetAwaiter().GetResult()
            .Select(item => ToEntry(item.Id, item.ProjectId, item.CreatedAt, item.Name, path))
            .ToArray();
    }

    public EditorProject RestoreCheckpoint(ProjectHistoryEntry entry, string? projectFilePath)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var path = entry.StoragePath ?? throw new InvalidOperationException("The checkpoint storage path is missing.");
        var core = _store.RestoreCheckpointAsync(path, entry.Id).GetAwaiter().GetResult();
        return _mapper.ToUi(core, projectFilePath);
    }

    public void DeleteCheckpoint(ProjectHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var path = entry.StoragePath ?? throw new InvalidOperationException("The checkpoint storage path is missing.");
        _store.DeleteCheckpointAsync(path, entry.Id).GetAwaiter().GetResult();
    }

    private string EnsureHistoryDocument(EditorProject project)
    {
        var path = GetHistoryPath(project);
        if (!File.Exists(path))
            _store.SaveAsync(path, _mapper.ToCore(project)).GetAwaiter().GetResult();
        return path;
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
