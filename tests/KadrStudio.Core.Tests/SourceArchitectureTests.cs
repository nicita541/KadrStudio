namespace KadrStudio.Core.Tests;

public sealed class SourceArchitectureTests
{
    [Fact]
    public void Production_source_contains_no_removed_mutable_project_or_json_undo_bridge()
    {
        var source = ReadProductionSources();

        Assert.DoesNotContain("EditorProject", source, StringComparison.Ordinal);
        Assert.DoesNotContain("EditorProjectMapper", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProjectJson", source, StringComparison.Ordinal);
    }

    [Fact]
    public void View_models_do_not_construct_process_or_file_adapters()
    {
        var viewModels = ReadSources(Path.Combine(SourceRoot(), "Kadr", "ViewModels"));

        Assert.DoesNotContain("new FfmpegLocator", viewModels, StringComparison.Ordinal);
        Assert.DoesNotContain("new ProcessRunner", viewModels, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", viewModels, StringComparison.Ordinal);
        Assert.DoesNotContain("File.", viewModels, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_source_does_not_synchronously_block_tasks()
    {
        var source = ReadProductionSources();

        Assert.DoesNotContain(".Wait()", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".GetAwaiter().GetResult()", source, StringComparison.Ordinal);
    }

    private static string ReadProductionSources() => ReadSources(SourceRoot());

    private static string ReadSources(string directory)
        => string.Join('\n', Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(File.ReadAllText));

    private static string SourceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "src");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(current.FullName, "KadrStudio.sln")))
                return candidate;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Kadr Studio repository root was not found.");
    }
}
