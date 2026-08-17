using System.Text.Json;

namespace KadrStudio.Services;

public sealed record WhisperConfiguration(string ExecutablePath, string ModelPath)
{
    public bool IsValid => File.Exists(ExecutablePath) && File.Exists(ModelPath);

    public static WhisperConfiguration? Load()
    {
        try
        {
            var path = SettingsPath();
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<WhisperConfiguration>(File.ReadAllText(path));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }

    public static void Save(string executablePath, string modelPath)
    {
        var configuration = new WhisperConfiguration(
            Path.GetFullPath(executablePath), Path.GetFullPath(modelPath));
        if (!configuration.IsValid)
            throw new FileNotFoundException("Не найден whisper.cpp или выбранная модель.");
        var path = SettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(configuration));
        File.Move(temporary, path, overwrite: true);
    }

    private static string SettingsPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KadrStudio", "settings", "whisper.json");
}

public sealed record WhisperAvailability(
    bool IsReady,
    string? ExecutablePath,
    string? ModelPath,
    string Message);
