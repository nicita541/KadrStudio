namespace KadrStudio.Services;

/// <summary>
/// Resolves all mutable desktop data into one portable directory owned by the
/// project/application folder. The editor must not silently write to AppData.
///
/// Resolution order:
/// 1. KADR_STUDIO_DATA_ROOT environment override.
/// 2. LocalData below the nearest KadrStudio.sln directory.
/// 3. LocalData beside the running application.
/// </summary>
public static class KadrLocalDataPaths
{
    public static string Root =>
        ResolveRoot(
            Environment.GetEnvironmentVariable("KADR_STUDIO_DATA_ROOT"),
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory);

    public static string AgentLogsRoot =>
        EnsureDirectory(Path.Combine(Root, "Logs", "Agent"));

    public static string SettingsRoot =>
        EnsureDirectory(Path.Combine(Root, "Settings"));

    public static string CacheRoot =>
        EnsureDirectory(Path.Combine(Root, "Cache"));

    public static string ArtifactsRoot =>
        EnsureDirectory(Path.Combine(Root, "Artifacts"));

    public static string HistoryRoot =>
        EnsureDirectory(Path.Combine(Root, "History"));

    public static string RecoveryRoot =>
        EnsureDirectory(Path.Combine(Root, "Recovery"));

    public static string TempRoot =>
        EnsureDirectory(Path.Combine(Root, "Temp"));

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

        foreach (var candidate in new[] { currentDirectory, baseDirectory })
        {
            var workspace = FindWorkspaceRoot(candidate);
            if (workspace is not null)
            {
                return Path.Combine(workspace, "LocalData");
            }
        }

        var applicationDirectory = !string.IsNullOrWhiteSpace(baseDirectory)
            ? baseDirectory
            : currentDirectory;
        if (string.IsNullOrWhiteSpace(applicationDirectory))
        {
            applicationDirectory = AppContext.BaseDirectory;
        }

        return Path.GetFullPath(Path.Combine(applicationDirectory, "LocalData"));
    }

    private static string? FindWorkspaceRoot(string? startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return null;
        }

        var current = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "KadrStudio.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
