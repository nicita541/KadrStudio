namespace KadrStudio.UiAdapters.Tests;

public sealed class AiWorkspaceUiSourceTests
{
    [Fact]
    public void Upscale_and_boundary_dialogs_only_reference_existing_theme_resources()
    {
        var root = SourceRoot();
        var upscale = File.ReadAllText(Path.Combine(root, "Kadr", "Views", "UpscaleWindow.xaml"));
        var boundaries = File.ReadAllText(Path.Combine(root, "Kadr", "Views", "BoundaryConfirmationWindow.xaml"));

        Assert.Contains("{StaticResource WindowBrush}", upscale, StringComparison.Ordinal);
        Assert.Contains("{StaticResource WindowBrush}", boundaries, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowBackgroundBrush", upscale, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowBackgroundBrush", boundaries, StringComparison.Ordinal);
    }

    [Fact]
    public void Ai_workspace_is_a_single_v2_chat_with_draft_review_and_no_plan_approval()
    {
        var root = SourceRoot();
        var mainWindow = File.ReadAllText(Path.Combine(root, "Kadr", "Views", "MainWindow.xaml"));
        var mainWindowCode = File.ReadAllText(Path.Combine(root, "Kadr", "Views", "MainWindow.xaml.cs"));
        var app = File.ReadAllText(Path.Combine(root, "Kadr", "App.xaml.cs"));

        Assert.Contains("x:Name=\"AiChatMessagesListBox\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AiChatPromptTextBox\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"EditorialProfileComboBox\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"EditorialTargetComboBox\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Все серии на таймлайне", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("Определить по смыслу запроса", mainWindowCode, StringComparison.Ordinal);
        Assert.DoesNotContain("\"на всех сериях\"", mainWindowCode, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Tag=\"AnimeEpisode\"", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("Tag=\"Generic\"", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("Tag=\"FilmSeries\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Click=\"ModelPackInstall_Click\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Click=\"AiUpscale_Click\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"UpscaleNavButton\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"UpscalePanel\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Click=\"ShowUpscale_Click\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"UpscaleStatusText\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Content=\"Запустить апскейл дорожек\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TimelineVariantTabs\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Click=\"TimelineVariantTab_Click\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource TimelineVariantTabButton}\"", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("Click=\"RenditionToggle_Click\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Click=\"UpscaleCompare_Click\"", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("AiChatScenarioComboBox", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("AiChatContextComboBox", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("AiScenarioComboBox", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("AiChatSuggestionsPanel", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("Объединить серии", mainWindow, StringComparison.Ordinal);
        Assert.Contains("PreviewKeyDown=\"AiChatPrompt_PreviewKeyDown\"", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("AiChatQuestionOption_Click", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("Подтвердить план", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Click=\"AiChatOpenSource_Click\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Click=\"AiChatOpenDraft_Click\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Click=\"AiChatAcceptDraft_Click\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Click=\"AiChatDeleteDraft_Click\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Content=\"Показать монтажный черновик (B)\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Content=\"Вернуться к исходной серии (A)\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Content=\"Удалить только черновик\"", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("Margin=\"0,38,0,0\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Value=\"{Binding ProgressPercent, Mode=OneWay}\"", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"AiMontageTabControl\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RazorToolButton\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SnappingToggleButton\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource ToggleIconButton}\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("<Path", mainWindow, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, "Kadr", "Views", "MontageDecisionWindow.xaml")));
        Assert.Contains("Interlocked.Exchange(ref _unhandledErrorDialogOpen, 1)", app, StringComparison.Ordinal);

        var viewModel = File.ReadAllText(Path.Combine(root, "Kadr", "ViewModels", "MainViewModel.cs"));
        Assert.Contains("FormatRemovedRanges(result.Compilation.RemovedTimelineRanges)", viewModel, StringComparison.Ordinal);
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
