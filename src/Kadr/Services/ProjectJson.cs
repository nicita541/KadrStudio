using System.Text.Json;
using System.Text.Json.Serialization;
using KadrStudio.Models;

namespace KadrStudio.Services;

public static class ProjectJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Serialize(EditorProject project) => JsonSerializer.Serialize(project, Options);

    public static EditorProject Deserialize(string json)
    {
        var project = JsonSerializer.Deserialize<EditorProject>(json, Options)
                      ?? throw new InvalidDataException("Файл проекта пуст или повреждён.");
        project.Media ??= [];
        project.Clips ??= [];
        project.Markers ??= [];
        return project;
    }
}
