using KadrStudio.Services;

namespace KadrStudio.UiAdapters.Tests;

public sealed class KadrLocalDataPathsTests
{
    [Fact]
    public void Workspace_uses_one_ignored_local_data_directory()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "KadrStudio-LocalDataPathsTests",
            Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(root, "release", "KadrStudio-win-x64");

        try
        {
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(root, "KadrStudio.sln"), string.Empty);

            var resolved = KadrLocalDataPaths.ResolveRoot(
                configuredRoot: null,
                currentDirectory: nested,
                baseDirectory: nested,
                localDataDirectory: Path.Combine(root, "ignored-app-data"));

            Assert.Equal(
                Path.GetFullPath(Path.Combine(root, "LocalData")),
                resolved);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void Explicit_data_root_override_has_priority()
    {
        var configured = Path.Combine(
            Path.GetTempPath(),
            "KadrStudio-ConfiguredData",
            Guid.NewGuid().ToString("N"));

        var resolved = KadrLocalDataPaths.ResolveRoot(
            configured,
            currentDirectory: @"C:\ignored",
            baseDirectory: @"C:\ignored");

        Assert.Equal(
            Path.GetFullPath(configured),
            resolved);
    }

    [Fact]
    public void Missing_workspace_falls_back_beside_application()
    {
        var portable = Path.Combine(
            Path.GetTempPath(),
            "KadrStudio-PortableData",
            Guid.NewGuid().ToString("N"));

        var resolved = KadrLocalDataPaths.ResolveRoot(
            configuredRoot: null,
            currentDirectory: null,
            baseDirectory: portable,
            localDataDirectory: null);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(portable, "LocalData")),
            resolved);
    }
}
