using System.Text.Json;
using KadrStudio.Application.Automation.Editorial;

namespace KadrStudio.Services.Editorial;

/// <summary>
/// Content-free local metrics. No prompts, transcript text, paths, frames or audio are written.
/// </summary>
public sealed class JsonlEditorialTelemetrySink : IEditorialTelemetrySink
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _sync = new();
    private readonly string _path;

    public JsonlEditorialTelemetrySink(string? path = null)
    {
        _path = Path.GetFullPath(path ?? Path.Combine(
            KadrLocalDataPaths.EditorialTelemetryRoot,
            "telemetry.jsonl"));
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public void Record(EditorialTelemetryEvent telemetryEvent)
    {
        ArgumentNullException.ThrowIfNull(telemetryEvent);
        var line = JsonSerializer.Serialize(telemetryEvent, Json) + Environment.NewLine;
        lock (_sync)
            File.AppendAllText(_path, line);
    }
}
