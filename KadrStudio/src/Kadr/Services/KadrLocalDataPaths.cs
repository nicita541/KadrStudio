namespace KadrStudio.Services;

/// <summary>Environment override, explicit portable marker, then per-user installed data.</summary>
public static class KadrLocalDataPaths
{
    public const string PortableMarkerName = "KadrStudio.portable";
    public static string Root => ResolveRoot(
        Environment.GetEnvironmentVariable("KADR_STUDIO_DATA_ROOT"),
        Directory.GetCurrentDirectory(), AppContext.BaseDirectory);
    public static string AgentLogsRoot => EnsureDirectory(Path.Combine(Root, "Logs", "Agent"));
    public static string EditorialTelemetryRoot => EnsureDirectory(Path.Combine(Root, "Logs", "Editorial"));
    public static string SettingsRoot => EnsureDirectory(Path.Combine(Root, "Settings"));
    public static string CacheRoot => EnsureDirectory(Path.Combine(Root, "Cache"));
    public static string ArtifactsRoot => EnsureDirectory(Path.Combine(Root, "Artifacts"));
    public static string HistoryRoot => EnsureDirectory(Path.Combine(Root, "History"));
    public static string RecoveryRoot => EnsureDirectory(Path.Combine(Root, "Recovery"));
    public static string TempRoot => EnsureDirectory(Path.Combine(Root, "Temp"));

    public static string ResolveRoot(string? configuredRoot, string? currentDirectory,
        string? baseDirectory, string? localDataDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredRoot))
            return Path.GetFullPath(configuredRoot.Trim());

        var applicationDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(baseDirectory)
            ? AppContext.BaseDirectory : baseDirectory);
        if (File.Exists(Path.Combine(applicationDirectory, PortableMarkerName)))
            return Path.Combine(applicationDirectory, "LocalData");

        var userDirectory = localDataDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(userDirectory))
            throw new InvalidOperationException("Local application data directory is unavailable. Set KADR_STUDIO_DATA_ROOT explicitly.");
        return Path.GetFullPath(Path.Combine(userDirectory, "KadrStudio"));
    }

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
