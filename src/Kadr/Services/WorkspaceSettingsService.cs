using System.Text.Json;

namespace KadrStudio.Services;

public sealed record WorkspaceSettings(string ArtifactRoot, long ArtifactDiskBudgetBytes)
{
    public static WorkspaceSettings Default => new(
        ThumbnailService.DefaultArtifactRoot(), 8L * 1024 * 1024 * 1024);
}

public sealed class WorkspaceSettingsService
{
    private readonly string _path;

    public WorkspaceSettingsService(string? path = null)
    {
        _path = Path.GetFullPath(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kadr Studio", "settings.json"));
    }

    public WorkspaceSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return WorkspaceSettings.Default;
            var settings = JsonSerializer.Deserialize<WorkspaceSettings>(File.ReadAllText(_path));
            if (settings is null || string.IsNullOrWhiteSpace(settings.ArtifactRoot) ||
                settings.ArtifactDiskBudgetBytes < 1024 * 1024)
                return WorkspaceSettings.Default;
            return settings with { ArtifactRoot = Path.GetFullPath(settings.ArtifactRoot) };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return WorkspaceSettings.Default;
        }
    }

    public async Task SaveAsync(WorkspaceSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings with { ArtifactRoot = Path.GetFullPath(settings.ArtifactRoot) };
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = _path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(normalized, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
        }
    }
}
