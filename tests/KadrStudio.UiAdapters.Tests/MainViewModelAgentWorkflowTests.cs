using KadrStudio.Application.Automation.Agent;
using CoreSequenceStatus = KadrStudio.Core.Domain.SequenceStatus;
using KadrStudio.Models;
using KadrStudio.ViewModels;

namespace KadrStudio.UiAdapters.Tests;

public sealed class MainViewModelAgentWorkflowTests
{
    [Fact]
    public async Task Approved_agent_plan_creates_locked_separate_draft_and_never_mutates_source()
    {
        var sourcePath = Path.Combine(
            Path.GetTempPath(),
            $"kadr-agent-workflow-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(sourcePath, [0]);

        try
        {
            await using var viewModel = new MainViewModel();
            var asset = new MediaAsset
            {
                Path = sourcePath,
                Name = "episode.mp4",
                Kind = MediaKind.Video,
                Duration = 60,
                HasAudio = true,
                Width = 1920,
                Height = 1080,
                FrameRate = 30
            };

            Assert.True(viewModel.RegisterImportedMedia(asset));
            viewModel.AddAssetToTimeline(asset.Id);

            var started = viewModel.StartAgentTask(
                "Измени только то, что будет утверждено в плане.");
            var sourceBefore = viewModel.CoreState
                .FindSequence(started.SourceSequenceId)!;

            viewModel.AiAgentOrchestrator.BeginPlanning();
            viewModel.AiAgentOrchestrator.PublishPlan(
                AgentPlanDraft.Create(
                    "Безопасный тестовый Agent Draft.",
                    "Основной таймлайн не меняется.",
                    new[] { "Не менять исходную последовательность." },
                    new[]
                    {
                        new AgentPlanStepDraft(
                            "Подготовить черновик",
                            "Работать только в отдельной последовательности."),
                        new AgentPlanStepDraft(
                            "Проверить",
                            "Проверить итоговый Agent Draft перед завершением.")
                    }));

            var draft = viewModel.ApproveAgentPlanAndCreateDraft();
            var sourceAfter = viewModel.CoreState
                .FindSequence(started.SourceSequenceId)!;

            Assert.NotEqual(sourceAfter.Id, draft.Id);
            Assert.Equal(CoreSequenceStatus.Draft, draft.Status);
            Assert.Equal(sourceAfter.Id, draft.ParentSequenceId);
            Assert.Equal(sourceBefore.MediaClips, sourceAfter.MediaClips);
            Assert.Equal(sourceBefore.Duration, sourceAfter.Duration);
            Assert.Equal(draft.Id, viewModel.CoreState.ActiveSequenceId);
            Assert.True(viewModel.IsAgentDraftEditingLocked);

            Assert.False(viewModel.ActivateSequence(sourceAfter.Id));
            Assert.Equal(draft.Id, viewModel.CoreState.ActiveSequenceId);

            viewModel.StopAgentTask();

            Assert.False(viewModel.IsAgentDraftEditingLocked);
            Assert.True(viewModel.ActivateSequence(sourceAfter.Id));
            Assert.Equal(sourceAfter.Id, viewModel.CoreState.ActiveSequenceId);
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }
}
