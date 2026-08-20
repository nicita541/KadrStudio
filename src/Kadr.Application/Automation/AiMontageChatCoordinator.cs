using System.Collections.Immutable;
using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Automation;

public sealed class AiMontageChatCoordinator
{
    public AiConversation Append(AiConversation conversation, AiChatMessage message)
        => conversation with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            Messages = conversation.Messages.Add(message)
        };

    public AiConversation Replace(AiConversation conversation, AiChatMessage message)
        => conversation with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            Messages = conversation.Messages
                .Select(item => item.Id == message.Id ? message : item)
                .ToImmutableArray()
        };

    public AiChatMessage? FindNextQuestion(AiConversation conversation, MontagePlan plan)
    {
        var asked = conversation.Messages
            .Where(message => message.DecisionId is not null)
            .Select(message => message.DecisionId!.Value)
            .ToHashSet();
        var decision = plan.Decisions.FirstOrDefault(item => !item.IsResolved && !asked.Contains(item.Id));
        return decision is null
            ? null
            : new AiChatMessage(
                Guid.NewGuid(), AiChatRole.Assistant, AiChatMessageKind.Question,
                decision.Prompt, DateTimeOffset.UtcNow, AiChatOperationState.Completed,
                DecisionId: decision.Id, PlanId: plan.Id);
    }

    public AiPlanCardSnapshot CreatePlanSnapshot(ProjectState project, MontagePlan plan, bool canCreateDraft)
    {
        var retained = plan.Items.OrderBy(item => item.Order).Select(item =>
        {
            var source = project.Sources.GetValueOrDefault(item.SourceId);
            return $"{item.Order + 1}. {RoleLabel(item.Role)} · {source?.Name ?? "Исходник"} · " +
                   $"{Format(item.SourceRange.Start)}–{Format(item.SourceRange.End)} · {item.Reason}";
        }).ToImmutableArray();

        var opening = plan.Items.FirstOrDefault(item => item.Role == MontageRole.Opening);
        var removed = plan.StructuralSegments
            .Where(segment => segment.Disposition == StructuralSegmentDisposition.Remove &&
                              (opening is null || segment.Id != opening.Id))
            .OrderBy(segment => project.Sources.GetValueOrDefault(segment.SourceId)?.Name)
            .ThenBy(segment => segment.SourceRange.Start)
            .Select(segment =>
            {
                var source = project.Sources.GetValueOrDefault(segment.SourceId);
                var evidence = segment.Evidence.FirstOrDefault()?.Summary;
                return $"{SegmentLabel(segment.Kind)} · {source?.Name ?? "Исходник"} · " +
                       $"{Format(segment.SourceRange.Start)}–{Format(segment.SourceRange.End)} · " +
                       $"{segment.Confidence:P0}{(string.IsNullOrWhiteSpace(evidence) ? string.Empty : $" · {evidence}")}";
            }).ToImmutableArray();

        return new AiPlanCardSnapshot(
            plan.Title, plan.Summary, plan.Duration, retained, removed,
            plan.Warnings.IsDefault ? [] : plan.Warnings, canCreateDraft);
    }

    private static string Format(TimelineTime time)
        => TimeSpan.FromSeconds(time.TotalSeconds).ToString(@"hh\:mm\:ss\.fff");

    private static string RoleLabel(MontageRole role) => role switch
    {
        MontageRole.Opening => "Опенинг",
        MontageRole.Hook => "Начало",
        MontageRole.Setup => "Завязка",
        MontageRole.Development => "Сюжет",
        MontageRole.Payoff => "Финал",
        MontageRole.Ending => "Завершение",
        _ => "Фрагмент"
    };

    private static string SegmentLabel(StructuralSegmentKind kind) => kind switch
    {
        StructuralSegmentKind.Opening => "Опенинг",
        StructuralSegmentKind.Ending => "Эндинг",
        StructuralSegmentKind.Recap => "Рекап",
        StructuralSegmentKind.Preview => "Превью",
        StructuralSegmentKind.Credits => "Титры",
        StructuralSegmentKind.PostCreditsStory => "Сцена после титров",
        StructuralSegmentKind.Story => "Сюжет",
        _ => "Неизвестный блок"
    };
}
