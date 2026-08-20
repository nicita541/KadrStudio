using System.Collections.Immutable;
using KadrStudio.Application.Automation;
using KadrStudio.Core.Domain;

namespace KadrStudio.Core.Tests;

public sealed class AiMontageChatCoordinatorTests
{
    [Fact]
    public void Universal_profile_is_available_without_a_keyword_router()
    {
        Assert.Equal(MaterialProfileKind.General, GameEditingProfiles.Get("universal").Kind);
    }

    [Fact]
    public void Pending_questions_are_emitted_one_at_a_time()
    {
        var coordinator = new AiMontageChatCoordinator();
        var project = ProjectState.CreateNew("chat");
        var profile = GameEditingProfiles.BuiltIn.First();
        var first = Decision("Первый вопрос?");
        var second = Decision("Второй вопрос?");
        var plan = new MontagePlan(
            Guid.NewGuid(), Guid.NewGuid(), "План", "Проверка", MontagePlanStatus.NeedsInput,
            MontageTargetFormat.Source, TimelineTime.Zero, TimelineTime.Zero, TimelineTime.Zero,
            profile,
            new AutomationDependencyStamp(project.Id, project.ActiveSequenceId,
                project.ActiveSequence?.Revision, ImmutableDictionary<Guid, string>.Empty,
                "test", "model", profile.Id, profile.Version),
            [], [], [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            Decisions: [first, second]);
        var conversation = AiConversation.Create();

        var firstMessage = coordinator.FindNextQuestion(conversation, plan);
        Assert.NotNull(firstMessage);
        Assert.Equal(first.Id, firstMessage.DecisionId);
        conversation = coordinator.Append(conversation, firstMessage);

        var secondMessage = coordinator.FindNextQuestion(conversation, plan);
        Assert.NotNull(secondMessage);
        Assert.Equal(second.Id, secondMessage.DecisionId);
        conversation = coordinator.Append(conversation, secondMessage);

        Assert.Null(coordinator.FindNextQuestion(conversation, plan));
    }

    private static MontageDecision Decision(string prompt)
        => new(Guid.NewGuid(), MontageDecisionKind.SegmentClassification, prompt,
        [
            new MontageDecisionOption("keep", "Оставить"),
            new MontageDecisionOption("remove", "Удалить")
        ]);
}
