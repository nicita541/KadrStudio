namespace KadrStudio.UiAdapters.Tests;

public sealed class AnimePresetUiSourceTests
{
    [Fact]
    public void Ai_workspace_is_a_single_chat_with_inline_plan_and_questions()
    {
        var root = SourceRoot();
        var mainWindow = File.ReadAllText(Path.Combine(root, "Kadr", "Views", "MainWindow.xaml"));
        var app = File.ReadAllText(Path.Combine(root, "Kadr", "App.xaml.cs"));

        Assert.Contains("x:Name=\"AiChatMessagesListBox\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AiChatPromptTextBox\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AiChatScenarioComboBox\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Авто · Универсальный", File.ReadAllText(
            Path.Combine(root, "Kadr", "Views", "MainWindow.xaml.cs")), StringComparison.Ordinal);
        Assert.Contains("PreviewKeyDown=\"AiChatPrompt_PreviewKeyDown\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Click=\"AiChatQuestionOption_Click\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Content=\"−1 кадр\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Content=\"+1 кадр\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Content=\"Подтвердить этот кадр\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Content=\"Создать черновик\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Value=\"{Binding ProgressPercent, Mode=OneWay}\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AiMontageTabControl\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"Collapsed\"", mainWindow, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, "Kadr", "Views", "MontageDecisionWindow.xaml")));
        Assert.Contains("Interlocked.Exchange(ref _unhandledErrorDialogOpen, 1)", app, StringComparison.Ordinal);
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
