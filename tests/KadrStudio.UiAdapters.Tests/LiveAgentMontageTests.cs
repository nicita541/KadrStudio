using KadrStudio.Application.Automation.Agent;
using KadrStudio.Models;
using KadrStudio.Services;
using KadrStudio.ViewModels;
using System.Globalization;
using System.Text.Json;
using Xunit.Abstractions;

namespace KadrStudio.UiAdapters.Tests;

/// <summary>
/// Opt-in smoke test for the complete local stack: real media probing, real
/// AI Server planning, approved editing tools, receipts and verification.
/// </summary>
public sealed class LiveAgentMontageTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Real_vision_covers_the_entire_discovery_window()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("KADR_CHECK_LIVE_VISION"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var videoPath = Environment.GetEnvironmentVariable("KADR_LIVE_AGENT_VIDEO");
        Assert.True(File.Exists(videoPath), $"Live agent video was not found: {videoPath}");
        await using var viewModel = new MainViewModel();
        var importErrors = await viewModel.ImportFilesAsync([videoPath!]);
        Assert.Empty(importErrors);
        var asset = Assert.Single(viewModel.Project.Media, item =>
            string.Equals(item.Path, Path.GetFullPath(videoPath!), StringComparison.OrdinalIgnoreCase));

        const int discoveryWindows = 12;
        var window = asset.Duration / discoveryWindows;
        var start = ReadOptionalSeconds("KADR_LIVE_VISION_START") ?? window;
        var end = ReadOptionalSeconds("KADR_LIVE_VISION_END") ?? window * 2;
        Assert.True(start >= 0 && end > start && end <= asset.Duration);
        var baseline = await viewModel.AutomationOrchestrator.InspectTechnicalRangeAsync(
            new VideoAnalysisRequest(
                asset,
                start,
                end,
                "Technical discovery-window coverage."),
            cancellationToken: CancellationToken.None);
        var inspection = await viewModel.AutomationOrchestrator.InspectRangeAsync(
            asset,
            baseline.Result,
            viewModel.AiVideoAnalysisService.PreferredModel,
            cancellationToken: CancellationToken.None);

        output.WriteLine(
            $"Vision window {start:0.###}-{end:0.###}; observations={inspection.Observations.Count}; " +
            $"coverage_complete={inspection.CoverageComplete}");
        foreach (var observation in inspection.Observations)
        {
            output.WriteLine(
                $"{observation.Start:0.###}-{observation.End:0.###}: " +
                $"{observation.Title} — {observation.Description}");
        }

        Assert.True(inspection.UsedVision);
        Assert.True(inspection.CoverageComplete);
    }

    private static double? ReadOptionalSeconds(string variableName)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        return double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var seconds) &&
            double.IsFinite(seconds)
                ? seconds
                : null;
    }

    [Fact]
    public async Task Real_agent_removes_opening_and_ending_and_saves_verified_project()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("KADR_RUN_LIVE_AGENT"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var videoPath = Environment.GetEnvironmentVariable("KADR_LIVE_AGENT_VIDEO");
        var projectPath = Environment.GetEnvironmentVariable("KADR_LIVE_AGENT_PROJECT");
        Assert.True(File.Exists(videoPath), $"Live agent video was not found: {videoPath}");
        Assert.False(string.IsNullOrWhiteSpace(projectPath));

        await using var viewModel = new MainViewModel();
        var importErrors = await viewModel.ImportFilesAsync([videoPath!]);
        Assert.Empty(importErrors);
        var asset = Assert.Single(
            viewModel.Project.Media,
            item => string.Equals(
                item.Path,
                Path.GetFullPath(videoPath!),
                StringComparison.OrdinalIgnoreCase));
        viewModel.AddAssetToTimeline(asset.Id);
        viewModel.EnsureSequenceWorkspace();
        var source = viewModel.CoreState.ActiveSequence!;
        output.WriteLine($"Source: {source.Id}, duration={source.Duration.TotalSeconds:0.###}s");

        AgentTaskState state;
        try
        {
            var request = Environment.GetEnvironmentVariable("KADR_LIVE_AGENT_REQUEST");
            if (string.IsNullOrWhiteSpace(request))
            {
                request =
                    "Удали опенинг и эндинг из этого видео. Остальной материал не меняй. " +
                    "Исследуй весь материал coarse-to-fine, не предполагая расположение частей; " +
                    "подготовь точный план с доказательствами, " +
                    "затем выполни его в Agent Draft и проверь результат.";
            }

            state = await viewModel.AgentWorkflow.StartAsync(
                request,
                CancellationToken.None);
            for (var retry = 1;
                 retry <= 3 && state.Phase == AgentTaskPhase.Failed && state.DraftSequenceId is null;
                 retry++)
            {
                output.WriteLine($"Planning response was retryable; retry {retry}/3.");
                state = await viewModel.AgentWorkflow.RetryAsync(CancellationToken.None);
            }
            output.WriteLine($"Planning stopped at: {state.Phase}");
            DumpState(state);
            Assert.Equal(AgentTaskPhase.WaitingForApproval, state.Phase);
            AssertExpectedOpeningRange(state.Plan!);

            state = await viewModel.AgentWorkflow.ApproveAsync(
                CancellationToken.None);
            output.WriteLine($"Execution stopped at: {state.Phase}");
            output.WriteLine(state.CompletionSummary ?? state.FailureMessage ?? string.Empty);
            Assert.Equal(AgentTaskPhase.Completed, state.Phase);

            var draft = viewModel.CoreState.FindSequence(state.DraftSequenceId!.Value)!;
            Assert.True(draft.Duration < source.Duration);
            Assert.NotEmpty(draft.AgentCheckpoint!.Receipts);
            Assert.Equal(
                KadrStudio.Core.Domain.AgentDraftExecutionStatus.Completed,
                draft.AgentCheckpoint.Status);
        }
        finally
        {
            Directory.CreateDirectory(Path.GetDirectoryName(projectPath!)!);
            await viewModel.SaveProjectAsync(
                projectPath!,
                CancellationToken.None);
        }
    }

    [Fact]
    public async Task Resume_saved_live_agent_reuses_evidence_and_finishes_verified_draft()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("KADR_RESUME_LIVE_AGENT"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var projectPath = Environment.GetEnvironmentVariable("KADR_LIVE_AGENT_PROJECT");
        Assert.True(File.Exists(projectPath), $"Live agent project was not found: {projectPath}");

        await using var viewModel = new MainViewModel();
        await viewModel.OpenProjectAsync(projectPath!, CancellationToken.None);
        var state = Assert.IsType<AgentTaskState>(viewModel.CurrentAgentTask);
        Assert.Equal(AgentTaskPhase.Failed, state.Phase);

        try
        {
            for (var retry = 1; retry <= 4; retry++)
            {
                output.WriteLine($"Resuming persisted planning evidence; attempt {retry}/4.");
                state = await viewModel.AgentWorkflow.RetryAsync(CancellationToken.None);
                output.WriteLine($"Planning stopped at: {state.Phase}");
                DumpState(state);
                if (state.Phase != AgentTaskPhase.Failed)
                {
                    break;
                }
            }

            Assert.Equal(AgentTaskPhase.WaitingForApproval, state.Phase);
            AssertExpectedOpeningRange(state.Plan!);

            state = await viewModel.AgentWorkflow.ApproveAsync(CancellationToken.None);
            output.WriteLine($"Execution stopped at: {state.Phase}");
            output.WriteLine(state.CompletionSummary ?? state.FailureMessage ?? string.Empty);
            Assert.Equal(AgentTaskPhase.Completed, state.Phase);

            var source = viewModel.CoreState.FindSequence(state.SourceSequenceId)!;
            var draft = viewModel.CoreState.FindSequence(state.DraftSequenceId!.Value)!;
            Assert.True(draft.Duration < source.Duration);
            Assert.NotEmpty(draft.AgentCheckpoint!.Receipts);
            Assert.Equal(
                KadrStudio.Core.Domain.AgentDraftExecutionStatus.Completed,
                draft.AgentCheckpoint.Status);
        }
        finally
        {
            await viewModel.SaveProjectAsync(projectPath!, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Saved_live_project_reports_agent_diagnostics()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("KADR_INSPECT_LIVE_PROJECT"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var projectPath = Environment.GetEnvironmentVariable("KADR_LIVE_AGENT_PROJECT");
        Assert.True(File.Exists(projectPath), $"Live agent project was not found: {projectPath}");
        await using var viewModel = new MainViewModel();
        await viewModel.OpenProjectAsync(projectPath!, CancellationToken.None);
        var state = Assert.IsType<AgentTaskState>(viewModel.CurrentAgentTask);
        DumpState(state);
    }

    private void DumpState(AgentTaskState state)
    {
        output.WriteLine($"Failure: {state.FailureMessage ?? "<none>"}");
        output.WriteLine($"Completion: {state.CompletionSummary ?? "<none>"}");
        output.WriteLine($"Evidence count: {state.Evidence.Length}");
        foreach (var evidence in state.Evidence.OrderBy(item => item.Sequence))
        {
            output.WriteLine(
                $"E{evidence.Sequence} {evidence.ToolName} [{evidence.Capabilities}] " +
                $"{evidence.StartSeconds:0.###}-{evidence.EndSeconds:0.###}: {evidence.Summary}");
        }
        foreach (var entry in state.Journal.TakeLast(20))
        {
            output.WriteLine($"J {entry.Kind}: {entry.Message}");
        }
    }

    private static void AssertExpectedOpeningRange(AgentPlan plan)
    {
        var startValue = Environment.GetEnvironmentVariable("KADR_EXPECTED_OPENING_START");
        var endValue = Environment.GetEnvironmentVariable("KADR_EXPECTED_OPENING_END");
        if (!double.TryParse(startValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var expectedStart) ||
            !double.TryParse(endValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var expectedEnd))
        {
            return;
        }

        var ranges = plan.Steps.SelectMany(step => ExtractRanges(step.ExpectedEditingArguments)).ToArray();
        Assert.Contains(ranges, range =>
            Math.Abs(range.Start - expectedStart) <= 12 &&
            Math.Abs(range.End - expectedEnd) <= 12);
    }

    private static IEnumerable<(double Start, double End)> ExtractRanges(JsonElement? arguments)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } value)
        {
            yield break;
        }
        if (TryReadRange(value, out var direct))
        {
            yield return direct;
        }
        if (!value.TryGetProperty("ranges", out var ranges) || ranges.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }
        foreach (var item in ranges.EnumerateArray())
        {
            if (TryReadRange(item, out var range))
            {
                yield return range;
            }
        }
    }

    private static bool TryReadRange(JsonElement value, out (double Start, double End) range)
    {
        range = default;
        if (!value.TryGetProperty("start_seconds", out var start) || !start.TryGetDouble(out var startSeconds) ||
            !value.TryGetProperty("end_seconds", out var end) || !end.TryGetDouble(out var endSeconds))
        {
            return false;
        }
        range = (startSeconds, endSeconds);
        return true;
    }
}
