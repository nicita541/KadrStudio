namespace KadrStudio.Services;

/// <summary>
/// Resolves local Kadr data next to the active workspace/application instead
/// of placing large AI data and debug logs on the system drive.
///
/// Resolution order:
/// 1. KADR_STUDIO_DATA_ROOT environment override.
/// 2. Nearest KadrStudio workspace root (KadrStudio.sln or .git).
/// 3. Application directory for a portable build.
/// </summary>
public static class KadrLocalDataPaths
{
    public static string Root =>
        ResolveRoot(
            Environment.GetEnvironmentVariable("KADR_STUDIO_DATA_ROOT"),
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory);

    public static string AiModelsRoot =>
        EnsureDirectory(Path.Combine(Root, "AI", "models"));

    public static string AgentLogsRoot =>
        EnsureDirectory(Path.Combine(Root, "Logs", "Agent"));

    public static string ResolveRoot(
        string? configuredRoot,
        string? currentDirectory,
        string? baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.GetFullPath(configuredRoot.Trim());
        }

        foreach (var start in new[] { currentDirectory, baseDirectory }
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var workspace = FindWorkspaceRoot(start!);
            if (workspace is not null)
            {
                return workspace;
            }
        }

        var fallback = string.IsNullOrWhiteSpace(baseDirectory)
            ? AppContext.BaseDirectory
            : baseDirectory;

        return Path.GetFullPath(fallback!);
    }

    private static string? FindWorkspaceRoot(string start)
    {
        DirectoryInfo? directory;

        try
        {
            directory = new DirectoryInfo(Path.GetFullPath(start));
        }
        catch
        {
            return null;
        }

        for (var level = 0; directory is not null && level < 10; level++)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KadrStudio.sln")) ||
                Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
