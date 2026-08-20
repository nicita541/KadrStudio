using KadrStudio.Services;

namespace KadrStudio.UiAdapters.Tests;

public sealed class KadrLocalDataPathsTests
{
    [Fact]
    public void Workspace_root_is_never_used_for_runtime_data()
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

            var localData = Path.Combine(root, "outside-workspace");
            var resolved = KadrLocalDataPaths.ResolveRoot(
                configuredRoot: null,
                currentDirectory: nested,
                baseDirectory: nested,
                localDataDirectory: localData);

            Assert.Equal(
                Path.GetFullPath(Path.Combine(localData, "KadrStudio")),
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
    public void Missing_local_app_data_falls_back_to_temporary_user_directory()
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
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "KadrStudio", Environment.UserName)),
            resolved);
    }
}
