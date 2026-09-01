using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using KadrStudio.Application.Automation.Agent.Diagnostics;

namespace KadrStudio.Services.Agent;

/// <summary>
/// Local JSONL diagnostic log for the AI agent.
///
/// Diagnostics are best-effort: logging must never crash or block the editor.
/// Each application run gets a unique file so parallel Kadr instances cannot
/// overwrite one another. latest-path.txt points to the newest session log.
/// </summary>
public sealed class FileAgentDebugLog : IAgentDebugLog
{
    private const int MaximumDetailsCharacters = 16_000;
    private const int MaximumExceptionCharacters = 16_000;
    private const int MaximumSessionLogs = 20;
    private const long MaximumSessionLogBytes = 2 * 1024 * 1024;
    private const long MaximumTotalLogBytes = 10 * 1024 * 1024;

    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly object _gate = new();

    public FileAgentDebugLog(string? rootDirectory = null)
    {
        try
        {
            RootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
                ? KadrLocalDataPaths.AgentLogsRoot
                : Path.GetFullPath(rootDirectory);

            Directory.CreateDirectory(RootDirectory);

            var fileName =
                $"agent-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-" +
                $"{Environment.ProcessId}-{Guid.NewGuid():N}.jsonl";

            CurrentLogPath = Path.Combine(RootDirectory, fileName);
            File.WriteAllText(CurrentLogPath, string.Empty, Utf8NoBom);

            File.WriteAllText(
                Path.Combine(RootDirectory, "latest-path.txt"),
                CurrentLogPath,
                Utf8NoBom);

            PruneOldSessionLogs();

            Write(new AgentDebugLogEntry(
                DateTimeOffset.UtcNow,
                "session",
                "started",
                Message: "Kadr Studio AI agent debug session started.",
                Details:
                    $"process_id={Environment.ProcessId}; " +
                    $"framework={RuntimeInformation.FrameworkDescription}; " +
                    $"os={RuntimeInformation.OSDescription}"));
        }
        catch
        {
            // Diagnostics are best-effort and must never prevent Kadr from starting.
            RootDirectory = string.Empty;
            CurrentLogPath = null;
        }
    }

    public string RootDirectory { get; }

    public string? CurrentLogPath { get; }

    public void Write(AgentDebugLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (string.IsNullOrWhiteSpace(CurrentLogPath))
        {
            return;
        }

        try
        {
            var normalized = entry with
            {
                Area = Limit(entry.Area, 128) ?? "agent",
                EventName = Limit(entry.EventName, 128) ?? "event",
                Phase = Limit(entry.Phase, 128),
                Message = Limit(entry.Message, 16_000),
                Details = Limit(entry.Details, MaximumDetailsCharacters),
                Exception = Limit(entry.Exception, MaximumExceptionCharacters)
            };

            var json = JsonSerializer.Serialize(normalized, JsonOptions);

            lock (_gate)
            {
                if (new FileInfo(CurrentLogPath!).Length >= MaximumSessionLogBytes)
                {
                    return;
                }

                File.AppendAllText(
                    CurrentLogPath!,
                    json + Environment.NewLine,
                    Utf8NoBom);
            }
        }
        catch
        {
            // Never let logging change application behavior.
        }
    }

    private void PruneOldSessionLogs()
    {
        try
        {
            var currentPath = CurrentLogPath;
            var oldFiles = new DirectoryInfo(RootDirectory)
                .EnumerateFiles("agent-*.jsonl", SearchOption.TopDirectoryOnly)
                .Where(file =>
                    !string.Equals(
                        file.FullName,
                        currentPath,
                        StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(MaximumSessionLogs - 1)
                .ToArray();

            foreach (var file in oldFiles)
            {
                try
                {
                    file.Delete();
                }
                catch
                {
                }
            }


            var filesByAge = new DirectoryInfo(RootDirectory)
                .EnumerateFiles("agent-*.jsonl", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToArray();
            var retainedBytes = 0L;
            foreach (var file in filesByAge)
            {
                retainedBytes += file.Length;
                if (retainedBytes <= MaximumTotalLogBytes ||
                    string.Equals(file.FullName, currentPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    file.Delete();
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private static string? Limit(string? value, int maximumCharacters)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (value.Length <= maximumCharacters)
        {
            return value;
        }

        return value[..maximumCharacters] + "\n...[diagnostic field truncated]";
    }
}
