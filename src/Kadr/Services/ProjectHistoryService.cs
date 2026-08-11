using System.Text;
using System.Text.Json;
using KadrStudio.Models;

namespace KadrStudio.Services;

public sealed class ProjectHistoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _historyRoot;

    public ProjectHistoryService(string? historyRoot = null)
    {
        _historyRoot = historyRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kadr Studio",
            "history");
    }

    public ProjectHistoryEntry CreateCheckpoint(
        EditorProject project,
        string message,
        string? existingSnapshot = null)
    {
        var entry = new ProjectHistoryEntry
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            CreatedAt = DateTimeOffset.Now,
            Message = string.IsNullOrWhiteSpace(message) ? "Контрольная точка" : message.Trim()
        };
        var document = new ProjectHistoryDocument
        {
            Id = entry.Id,
            ProjectId = entry.ProjectId,
            CreatedAt = entry.CreatedAt,
            Message = entry.Message,
            Snapshot = existingSnapshot ?? ProjectJson.Serialize(project)
        };
        var directory = GetProjectDirectory(project);
        try
        {
            WriteCheckpoint(entry, document, directory);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException &&
            !directory.Equals(GetLocalProjectDirectory(project.Id), StringComparison.OrdinalIgnoreCase))
        {
            WriteCheckpoint(entry, document, GetLocalProjectDirectory(project.Id));
        }
        return entry;
    }

    public IReadOnlyList<ProjectHistoryEntry> GetCheckpoints(EditorProject project)
    {
        var entries = new List<ProjectHistoryEntry>();
        var directories = new[] { GetProjectDirectory(project), GetLocalProjectDirectory(project.Id) }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists);
        foreach (var directory in directories)
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var document = JsonSerializer.Deserialize<ProjectHistoryDocument>(File.ReadAllText(path), JsonOptions);
                    if (document is null || document.ProjectId != project.Id || string.IsNullOrWhiteSpace(document.Snapshot))
                    {
                        continue;
                    }

                    entries.Add(new ProjectHistoryEntry
                    {
                        Id = document.Id,
                        ProjectId = document.ProjectId,
                        CreatedAt = document.CreatedAt,
                        Message = document.Message,
                        StoragePath = directory
                    });
                }
                catch (JsonException)
                {
                    // Повреждённая отдельная точка не должна ломать всю историю проекта.
                }
                catch (IOException)
                {
                    // Файл мог быть временно занят другим экземпляром приложения.
                }
            }
        }

        return entries
            .DistinctBy(entry => entry.Id)
            .OrderByDescending(entry => entry.CreatedAt)
            .ToList();
    }

    public EditorProject RestoreCheckpoint(ProjectHistoryEntry entry, string? projectFilePath)
    {
        var path = GetEntryPath(entry);
        var document = JsonSerializer.Deserialize<ProjectHistoryDocument>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Контрольная точка повреждена.");
        if (document.ProjectId != entry.ProjectId || string.IsNullOrWhiteSpace(document.Snapshot))
        {
            throw new InvalidDataException("Контрольная точка не принадлежит этому проекту.");
        }

        var project = ProjectJson.Deserialize(document.Snapshot);
        project.FilePath = projectFilePath;
        foreach (var asset in project.Media)
        {
            asset.IsMissing = !File.Exists(asset.Path);
            if (string.IsNullOrWhiteSpace(asset.PreviewSourcePath) || !File.Exists(asset.PreviewSourcePath))
            {
                asset.PreviewSourcePath = asset.Path;
            }
        }
        return project;
    }

    public void DeleteCheckpoint(ProjectHistoryEntry entry)
    {
        var path = GetEntryPath(entry);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string GetProjectDirectory(EditorProject project)
        => string.IsNullOrWhiteSpace(project.FilePath)
            ? GetLocalProjectDirectory(project.Id)
            : Path.GetFullPath(project.FilePath) + ".history";

    private string GetLocalProjectDirectory(Guid projectId) => Path.Combine(_historyRoot, projectId.ToString("N"));

    private string GetEntryPath(ProjectHistoryEntry entry)
        => Path.Combine(
            entry.StoragePath ?? GetLocalProjectDirectory(entry.ProjectId),
            $"{entry.CreatedAt:yyyyMMdd-HHmmss-fff}-{entry.Id:N}.json");

    private static void WriteCheckpoint(
        ProjectHistoryEntry entry,
        ProjectHistoryDocument document,
        string directory)
    {
        entry.StoragePath = directory;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{entry.CreatedAt:yyyyMMdd-HHmmss-fff}-{entry.Id:N}.json");
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions), new UTF8Encoding(false));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private sealed class ProjectHistoryDocument
    {
        public Guid Id { get; set; }
        public Guid ProjectId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public string Message { get; set; } = string.Empty;
        public string Snapshot { get; set; } = string.Empty;
    }
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
