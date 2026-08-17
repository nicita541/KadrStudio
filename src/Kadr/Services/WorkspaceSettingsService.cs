using System.Collections.Immutable;
using System.Text.Json;
using KadrStudio.Application.Automation;
using KadrStudio.Core.Domain;

namespace KadrStudio.Services;

public sealed record WorkspaceSettings(
    string ArtifactRoot,
    long ArtifactDiskBudgetBytes,
    ImmutableArray<GameEditingProfile> CustomGameProfiles = default)
{
    public static WorkspaceSettings Default => new(
        ThumbnailService.DefaultArtifactRoot(), 8L * 1024 * 1024 * 1024, []);
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
            var profiles = settings.CustomGameProfiles.IsDefault
                ? ImmutableArray<GameEditingProfile>.Empty
                : settings.CustomGameProfiles.Select(GameEditingProfiles.ValidateCustom).ToImmutableArray();
            return settings with
            {
                ArtifactRoot = Path.GetFullPath(settings.ArtifactRoot),
                CustomGameProfiles = profiles
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return WorkspaceSettings.Default;
        }
    }

    public async Task SaveAsync(WorkspaceSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings with
        {
            ArtifactRoot = Path.GetFullPath(settings.ArtifactRoot),
            CustomGameProfiles = settings.CustomGameProfiles.IsDefault
                ? ImmutableArray<GameEditingProfile>.Empty
                : settings.CustomGameProfiles.Select(GameEditingProfiles.ValidateCustom).ToImmutableArray()
        };
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

    public IReadOnlyList<GameEditingProfile> LoadGameEditingProfiles()
        => GameEditingProfiles.BuiltIn.Concat(Load().CustomGameProfiles)
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    public Task SaveCustomGameProfilesAsync(
        IEnumerable<GameEditingProfile> profiles,
        CancellationToken cancellationToken = default)
    {
        var settings = Load();
        var validated = profiles.Select(GameEditingProfiles.ValidateCustom).ToImmutableArray();
        return SaveAsync(settings with { CustomGameProfiles = validated }, cancellationToken);
    }
}
