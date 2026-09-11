using KadrStudio.Services;

namespace KadrStudio.UiAdapters.Tests;

public sealed class KadrLocalDataPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "KadrStudio-Paths", Guid.NewGuid().ToString("N"));
    public KadrLocalDataPathsTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Installed_mode_ignores_working_directory_and_solution_ancestors()
    {
        File.WriteAllText(Path.Combine(_root, "KadrStudio.sln"), "");
        var app = Path.Combine(_root, "release");
        Directory.CreateDirectory(app);
        Assert.Equal(Path.Combine(_root, "AppData", "KadrStudio"),
            KadrLocalDataPaths.ResolveRoot(null, _root, app, Path.Combine(_root, "AppData")));
    }

    [Fact]
    public void Portable_marker_beside_executable_selects_local_data()
    {
        File.WriteAllText(Path.Combine(_root, "KadrStudio.portable"), "");
        Assert.Equal(Path.Combine(_root, "LocalData"),
            KadrLocalDataPaths.ResolveRoot(null, null, _root, Path.Combine(_root, "AppData")));
    }

    [Fact]
    public void Working_directory_marker_does_not_select_portable_mode()
    {
        File.WriteAllText(Path.Combine(_root, "KadrStudio.portable"), "");
        Assert.Equal(Path.Combine(_root, "AppData", "KadrStudio"),
            KadrLocalDataPaths.ResolveRoot(null, _root, Path.Combine(_root, "app"), Path.Combine(_root, "AppData")));
    }

    [Fact]
    public void Override_wins_over_portable_marker()
    {
        File.WriteAllText(Path.Combine(_root, "KadrStudio.portable"), "");
        Assert.Equal(Path.Combine(_root, "custom"),
            KadrLocalDataPaths.ResolveRoot(Path.Combine(_root, "custom"), null, _root));
    }
}
