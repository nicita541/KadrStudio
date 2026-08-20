using KadrStudio.Application.Automation.Agent;

namespace KadrStudio.Core.Tests;

public sealed class AiAgentOrchestratorTests
{
    [Fact]
    public void Only_one_non_terminal_task_can_be_active()
    {
        var coordinator = new AiAgentOrchestrator();

        coordinator.StartTask(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Сделай монтаж.");

        Assert.Throws<AgentTaskTransitionException>(() =>
            coordinator.StartTask(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "Вторая задача."));
    }

    [Fact]
    public void Question_pauses_task_and_answer_resumes_previous_phase()
    {
        var coordinator = new AiAgentOrchestrator();

        coordinator.StartTask(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Сделай монтаж.");
        coordinator.BeginInvestigation();

        var waiting = coordinator.AskQuestion(
            "Какую версию сцены оставить?",
            "Две версии выглядят одинаково вероятными.");

        Assert.Equal(AgentTaskPhase.WaitingForUserInput, waiting.Phase);
        Assert.Equal(AgentTaskPhase.Investigating, waiting.ResumePhase);
        Assert.True(waiting.HasOpenQuestion);

        var question = Assert.Single(waiting.Questions);
        var resumed = coordinator.AnswerQuestion(
            question.Id,
            "Оставь вторую.");

        Assert.Equal(AgentTaskPhase.Investigating, resumed.Phase);
        Assert.False(resumed.HasOpenQuestion);
        Assert.Equal("Оставь вторую.", Assert.Single(resumed.Questions).Answer);
    }

    [Fact]
    public void Task_remembers_source_sequence_revision_for_safe_approval()
    {
        var coordinator = new AiAgentOrchestrator();

        var task = coordinator.StartTask(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Подготовь план.",
            sourceSequenceRevision: 42);

        Assert.Equal(42, task.SourceSequenceRevision);
    }

    [Fact]
    public void New_investigation_can_refresh_source_revision()
    {
        var coordinator = new AiAgentOrchestrator();

        coordinator.StartTask(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Подготовь план.",
            sourceSequenceRevision: 3);
        coordinator.BeginPlanning();
        coordinator.PublishPlan(CreatePlan());

        var refreshed = coordinator.BeginInvestigation(
            "Исходный таймлайн изменился; проверить план заново.",
            sourceSequenceRevision: 4);

        Assert.Equal(AgentTaskPhase.Investigating, refreshed.Phase);
        Assert.Equal(4, refreshed.SourceSequenceRevision);
        Assert.False(refreshed.HasApprovedPlan);
    }

    [Fact]
    public void Execution_requires_user_approved_plan_and_separate_draft()
    {
        var coordinator = new AiAgentOrchestrator();
        var sourceSequenceId = Guid.NewGuid();

        coordinator.StartTask(
            Guid.NewGuid(),
            sourceSequenceId,
            "Удали лишние части, остальное не трогай.");
        coordinator.BeginInvestigation();
        coordinator.BeginPlanning();
        coordinator.PublishPlan(CreatePlan());

        Assert.Throws<AgentTaskTransitionException>(() =>
            coordinator.BeginExecution(Guid.NewGuid()));

        coordinator.ApprovePlan();

        Assert.Throws<AgentTaskTransitionException>(() =>
            coordinator.BeginExecution(sourceSequenceId));

        var executing = coordinator.BeginExecution(Guid.NewGuid());

        Assert.Equal(AgentTaskPhase.Executing, executing.Phase);
        Assert.NotNull(executing.DraftSequenceId);
        Assert.True(executing.IsDraftReadOnlyForUser);
        Assert.NotEqual(executing.SourceSequenceId, executing.DraftSequenceId);
    }

    [Fact]
    public void Revising_approved_plan_invalidates_approval()
    {
        var coordinator = new AiAgentOrchestrator();

        coordinator.StartTask(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Сделай монтаж.");
        coordinator.BeginPlanning();
        coordinator.PublishPlan(CreatePlan());
        var approved = coordinator.ApprovePlan();

        Assert.True(approved.HasApprovedPlan);
        Assert.Equal(AgentTaskPhase.Approved, approved.Phase);

        var revised = coordinator.RevisePlan(
            AgentPlanDraft.Create(
                "Собрать аккуратный черновик.",
                "План исправлен пользователем.",
                new[] { "Не менять основной таймлайн." },
                new[]
                {
                    new AgentPlanStepDraft(
                        "Проверить материал",
                        "Изучить только необходимые диапазоны."),
                    new AgentPlanStepDraft(
                        "Собрать черновик",
                        "Выполнить изменения в отдельной последовательности.")
                }),
            AgentPlanRevisionSource.User,
            "Пользователь изменил второй шаг.");

        Assert.Equal(AgentTaskPhase.WaitingForApproval, revised.Phase);
        Assert.False(revised.HasApprovedPlan);
        Assert.Equal(2, revised.Plan!.Version);
        Assert.Equal(AgentPlanRevisionSource.User, revised.Plan.LastRevisionSource);
    }

    [Fact]
    public void Question_can_pause_and_resume_execution()
    {
        var coordinator = CreateApprovedCoordinator();
        var executing = coordinator.BeginExecution(Guid.NewGuid());

        Assert.Equal(AgentTaskPhase.Executing, executing.Phase);

        var waiting = coordinator.AskQuestion(
            "Граница неоднозначна. Какой вариант выбрать?");

        Assert.Equal(AgentTaskPhase.WaitingForUserInput, waiting.Phase);
        Assert.Equal(AgentTaskPhase.Executing, waiting.ResumePhase);
        Assert.True(waiting.IsDraftReadOnlyForUser);

        var question = Assert.Single(waiting.Questions);
        var resumed = coordinator.AnswerQuestion(
            question.Id,
            "Используй более позднюю границу.");

        Assert.Equal(AgentTaskPhase.Executing, resumed.Phase);
        Assert.True(resumed.IsDraftReadOnlyForUser);
    }

    [Fact]
    public void Completion_requires_verification()
    {
        var coordinator = CreateApprovedCoordinator();
        coordinator.BeginExecution(Guid.NewGuid());

        Assert.Throws<AgentTaskTransitionException>(() =>
            coordinator.Complete("Готово."));

        var verifying = coordinator.BeginVerification();

        Assert.Equal(AgentTaskPhase.Verifying, verifying.Phase);
        Assert.True(verifying.IsDraftReadOnlyForUser);

        var completed = coordinator.Complete(
            "Черновик выполнен и проверен.");

        Assert.Equal(AgentTaskPhase.Completed, completed.Phase);
        Assert.False(completed.IsDraftReadOnlyForUser);
        Assert.Equal(
            "Черновик выполнен и проверен.",
            completed.CompletionSummary);
    }

    [Fact]
    public void Stopped_task_is_archived_when_next_task_starts()
    {
        var coordinator = new AiAgentOrchestrator();

        var first = coordinator.StartTask(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Первая задача.");

        coordinator.Stop("Остановлено пользователем.");

        var second = coordinator.StartTask(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Вторая задача.");

        Assert.NotEqual(first.Id, second.Id);
        var archived = Assert.Single(coordinator.History);
        Assert.Equal(first.Id, archived.Id);
        Assert.Equal(AgentTaskPhase.Stopped, archived.Phase);
    }

    [Fact]
    public void Task_changed_event_exposes_live_state_updates_for_future_ui()
    {
        var coordinator = new AiAgentOrchestrator();
        var observed = new List<AgentTaskPhase>();

        coordinator.TaskChanged += (_, args) =>
            observed.Add(args.State.Phase);

        coordinator.StartTask(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Сделай монтаж.");
        coordinator.BeginInvestigation();
        coordinator.BeginPlanning();

        Assert.Equal(
            new[]
            {
                AgentTaskPhase.Understanding,
                AgentTaskPhase.Investigating,
                AgentTaskPhase.Planning
            },
            observed);
    }

    [Fact]
    public void Failed_planning_can_retry_same_task_without_creating_a_draft()
    {
        var coordinator = new AiAgentOrchestrator();
        var started = coordinator.StartTask(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Исследуй материал и предложи план.");
        coordinator.Fail("Повреждённый ответ модели.");

        var retried = coordinator.RetryFailedPlanning();

        Assert.Equal(started.Id, retried.Id);
        Assert.Equal(AgentTaskPhase.Understanding, retried.Phase);
        Assert.Null(retried.DraftSequenceId);
        Assert.Null(retried.FailureMessage);
    }

    [Fact]
    public void Failed_execution_with_a_draft_cannot_be_retried_automatically()
    {
        var coordinator = CreateApprovedCoordinator();
        coordinator.BeginExecution(Guid.NewGuid());
        coordinator.Fail("Детерминированное действие отклонено.");

        var error = Assert.Throws<AgentTaskTransitionException>(
            coordinator.RetryFailedPlanning);

        Assert.Contains("Agent Draft", error.Message, StringComparison.Ordinal);
        Assert.Equal(AgentTaskPhase.Failed, coordinator.CurrentTask!.Phase);
    }

    private static AiAgentOrchestrator CreateApprovedCoordinator()
    {
        var coordinator = new AiAgentOrchestrator();

        coordinator.StartTask(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Сделай монтаж.");
        coordinator.BeginPlanning();
        coordinator.PublishPlan(CreatePlan());
        coordinator.ApprovePlan();

        return coordinator;
    }

    private static AgentPlanDraft CreatePlan()
    {
        return AgentPlanDraft.Create(
            "Собрать безопасный черновик.",
            "Основной таймлайн остаётся нетронутым.",
            new[]
            {
                "Не менять исходную последовательность.",
                "Вопрос задавать только при реальной неопределённости."
            },
            new[]
            {
                new AgentPlanStepDraft(
                    "Исследовать материал",
                    "Собрать только информацию, необходимую для задачи."),
                new AgentPlanStepDraft(
                    "Выполнить изменения",
                    "Работать только в отдельном Agent Draft."),
                new AgentPlanStepDraft(
                    "Проверить результат",
                    "Проверить соответствие утверждённому плану.")
            });
    }
}
