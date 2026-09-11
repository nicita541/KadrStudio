namespace KadrStudio.Core.Tests;

public sealed class SourceArchitectureTests
{
    [Fact]
    public void Application_does_not_implement_operating_system_frame_transport()
    {
        var application = ReadSources(Path.Combine(SourceRoot(), "Kadr.Application"));
        Assert.DoesNotContain("MemoryMappedFile", application, StringComparison.Ordinal);
        Assert.DoesNotContain("new Mutex(", application, StringComparison.Ordinal);
    }

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
        Assert.DoesNotContain("new AiServerV2Client", viewModels, StringComparison.Ordinal);
        Assert.DoesNotContain("new AnalysisProxyBuilder", viewModels, StringComparison.Ordinal);
        Assert.DoesNotContain("new JsonlEditorialTelemetrySink", viewModels, StringComparison.Ordinal);
        Assert.DoesNotContain("new FileAgentDebugLog", viewModels, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_source_does_not_synchronously_block_tasks()
    {
        var source = ReadProductionSources();

        Assert.DoesNotContain(".Wait()", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".GetAwaiter().GetResult()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Desktop_agent_ui_depends_on_the_workflow_facade_only()
    {
        var mainWindow = File.ReadAllText(Path.Combine(
            SourceRoot(),
            "Kadr",
            "Views",
            "MainWindow.xaml.cs"));

        Assert.Contains("AgentWorkflow", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentPlanningLoop", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentExecutionLoop", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("AiAgentOrchestrator", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("PersistAgentTaskState", mainWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void Deterministic_draft_qc_has_no_model_or_tool_dependency()
    {
        var editorial = ReadSources(Path.Combine(
            SourceRoot(),
            "Kadr.Application", "Automation", "Editorial"));

        Assert.DoesNotContain("IAgentModel", editorial, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentTool", editorial, StringComparison.Ordinal);
        Assert.Contains("DeterministicDraftQualityAnalyzer", editorial, StringComparison.Ordinal);
        Assert.Contains("TimelineRangeTransformer", editorial, StringComparison.Ordinal);
    }

    [Fact]
    public void Removed_legacy_agent_and_v1_api_cannot_reappear()
    {
        var sourceRoot = SourceRoot();
        var source = ReadProductionSources();
        var forbiddenFiles = new[]
        {
            "AgentPlanningLoop.cs", "AgentExecutionLoop.cs", "AiServerAgentModel.cs",
            "AiMontage.cs", "AiMontageCommands.cs", "AiMontageContracts.cs",
            "MontagePlanCompiler.cs", "GameEditingProfiles.cs", "KadrV1Endpoints.cs",
            "KadrV1Contracts.cs", "OllamaRuntime.cs", "OllamaWarmupService.cs",
            "OllamaRequestRewriter.cs", "StructuredInferencePipeline.cs",
            "EditingCommandPlanner.cs", "VideoAnalysisService.cs", "MediaPyramid.cs"
        };
        var fileNames = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(forbiddenFiles, name => Assert.DoesNotContain(name, fileNames));

        Assert.DoesNotContain("/v1/inference/structured", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentToolRegistry", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WaitingForApproval", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ApproveAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("record MontagePlan(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MontagePlanId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentDraftCheckpoint", source, StringComparison.Ordinal);
        Assert.DoesNotContain("montage_plan_id", source, StringComparison.Ordinal);
        Assert.DoesNotContain("KADR_AI_OLLAMA", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HasPendingEditReview", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BeginEditPlanReview", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Desktop_repository_has_no_server_runtime_or_direct_model_execution()
    {
        var root = Directory.GetParent(SourceRoot())!.FullName;
        var source = ReadProductionSources();

        Assert.False(Directory.Exists(Path.Combine(root, "src", "Kadr.AiServer")));
        Assert.False(Directory.Exists(Path.Combine(root, "workers")));
        Assert.False(Directory.Exists(Path.Combine(root, ".kadr-ai")));
        Assert.DoesNotContain("PowerShellModelPackInstaller", source, StringComparison.Ordinal);
        Assert.DoesNotContain("KADR_AI_DATA_ROOT", source, StringComparison.Ordinal);
        Assert.DoesNotContain("animesr_upscale.py", source, StringComparison.Ordinal);
        Assert.DoesNotContain("python.exe", source, StringComparison.OrdinalIgnoreCase);
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
