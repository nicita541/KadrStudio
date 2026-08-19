using System.Text.RegularExpressions;

namespace KadrStudio.UiAdapters.Tests;

public sealed class UiStyleSourceTests
{
    [Fact]
    public void Every_view_button_uses_an_explicit_application_style()
    {
        foreach (var path in Directory.EnumerateFiles(Path.Combine(SourceRoot(), "Kadr", "Views"), "*.xaml"))
        {
            var source = File.ReadAllText(path);
            var buttons = Regex.Matches(source, @"<Button\b(?!\.)(?:(?!>).)*>", RegexOptions.Singleline);
            Assert.All(buttons.Cast<Match>(), button =>
                Assert.Contains("Style=", button.Value, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Analysis_has_no_single_thread_ffmpeg_override_and_ui_continuations_stay_on_owner_context()
    {
        var analysis = File.ReadAllText(Path.Combine(SourceRoot(), "Kadr", "Services", "VideoAnalysisService.cs"));
        var viewModel = File.ReadAllText(Path.Combine(SourceRoot(), "Kadr", "ViewModels", "MainViewModel.cs"));

        Assert.DoesNotContain("\"-threads\", \"1\"", analysis, StringComparison.Ordinal);
        Assert.DoesNotContain("\"-filter_threads\", \"1\"", analysis, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigureAwait(false)", viewModel, StringComparison.Ordinal);
        Assert.Contains("timelineAssetIds", viewModel, StringComparison.Ordinal);
    }

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
