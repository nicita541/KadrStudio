using System.Text;
using KadrStudio.Models;

namespace KadrStudio.Services;

public sealed class ProjectService
{
    private readonly string _autosavePath;

    public ProjectService()
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kadr Studio");
        Directory.CreateDirectory(dataDirectory);
        _autosavePath = Path.Combine(dataDirectory, "autosave.kadr");
    }

    public async Task SaveAsync(EditorProject project, string path, CancellationToken cancellationToken = default)
    {
        project.UpdatedAt = DateTimeOffset.Now;
        var json = ProjectJson.Serialize(project);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var temporaryPath = fullPath + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, json, new UTF8Encoding(false), cancellationToken);
        File.Move(temporaryPath, fullPath, overwrite: true);
        project.FilePath = fullPath;
        DeleteAutosave();
    }

    public async Task<EditorProject> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        var project = ProjectJson.Deserialize(json);
        if (project.FormatVersion > 1)
        {
            throw new InvalidDataException("Этот проект создан более новой версией Kadr Studio.");
        }

        project.FilePath = Path.GetFullPath(path);
        foreach (var asset in project.Media)
        {
            asset.IsMissing = !File.Exists(asset.Path);
        }

        return project;
    }

    public async Task SaveAutosaveAsync(EditorProject project, CancellationToken cancellationToken = default)
    {
        var json = ProjectJson.Serialize(project);
        await File.WriteAllTextAsync(_autosavePath, json, new UTF8Encoding(false), cancellationToken);
    }

    public bool AutosaveExists => File.Exists(_autosavePath);

    public async Task<EditorProject> OpenAutosaveAsync(CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(_autosavePath, cancellationToken);
        var project = ProjectJson.Deserialize(json);
        project.FilePath = null;
        foreach (var asset in project.Media)
        {
            asset.IsMissing = !File.Exists(asset.Path);
        }
        return project;
    }

    public void DeleteAutosave()
    {
        if (File.Exists(_autosavePath))
        {
            File.Delete(_autosavePath);
        }
    }

    public string CreateSnapshot(EditorProject project) => ProjectJson.Serialize(project);

    public EditorProject RestoreSnapshot(string snapshot, string? filePath)
    {
        var project = ProjectJson.Deserialize(snapshot);
        project.FilePath = filePath;
        return project;
    }
}
