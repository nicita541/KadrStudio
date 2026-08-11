using System.Text;
using System.Text.Json;

namespace KadrStudio.Services;

public sealed class RecentProjectsService
{
    private const int MaximumEntries = 12;
    private readonly string _storagePath;

    public RecentProjectsService()
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kadr Studio");
        Directory.CreateDirectory(dataDirectory);
        _storagePath = Path.Combine(dataDirectory, "recent-projects.json");
    }

    public IReadOnlyList<RecentProjectEntry> Load()
    {
        try
        {
            if (!File.Exists(_storagePath))
            {
                return [];
            }

            var json = File.ReadAllText(_storagePath);
            return (JsonSerializer.Deserialize<List<RecentProjectEntry>>(json) ?? [])
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Path))
                .OrderByDescending(entry => entry.LastOpenedAt)
                .Take(MaximumEntries)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public void Add(string path, string projectName)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var entries = Load()
                .Where(entry => !string.Equals(entry.Path, fullPath, StringComparison.OrdinalIgnoreCase))
                .ToList();
            entries.Insert(0, new RecentProjectEntry
            {
                Path = fullPath,
                Name = string.IsNullOrWhiteSpace(projectName) ? Path.GetFileNameWithoutExtension(fullPath) : projectName.Trim(),
                LastOpenedAt = DateTimeOffset.Now
            });
            Save(entries.Take(MaximumEntries));
        }
        catch
        {
            // Ошибка списка недавних не должна мешать работе с проектом.
        }
    }

    public void Remove(string path)
    {
        try
        {
            var entries = Load()
                .Where(entry => !string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase));
            Save(entries);
        }
        catch
        {
            // Повреждённый список будет пересоздан при следующем успешном сохранении.
        }
    }

    private void Save(IEnumerable<RecentProjectEntry> entries)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(entries.ToList(), options);
        File.WriteAllText(_storagePath, json, new UTF8Encoding(false));
    }
}

public sealed class RecentProjectEntry
{
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset LastOpenedAt { get; set; }

    public string LocationLabel => System.IO.Path.GetDirectoryName(Path) ?? Path;
}
