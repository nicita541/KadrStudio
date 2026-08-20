namespace KadrStudio.Services;

/// <summary>
/// Resolves mutable desktop data outside the source workspace.
///
/// Resolution order:
/// 1. KADR_STUDIO_DATA_ROOT environment override.
/// 2. Per-user local application data.
/// 3. A temporary per-user fallback.
/// </summary>
public static class KadrLocalDataPaths
{
    public static string Root =>
        ResolveRoot(
            Environment.GetEnvironmentVariable("KADR_STUDIO_DATA_ROOT"),
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public static string AgentLogsRoot =>
        EnsureDirectory(Path.Combine(Root, "Logs", "Agent"));

    public static string ResolveRoot(
        string? configuredRoot,
        string? currentDirectory,
        string? baseDirectory,
        string? localDataDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.GetFullPath(configuredRoot.Trim());
        }

        if (!string.IsNullOrWhiteSpace(localDataDirectory))
        {
            return Path.GetFullPath(Path.Combine(localDataDirectory, "KadrStudio"));
        }

        return Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "KadrStudio",
            Environment.UserName));
    }

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
