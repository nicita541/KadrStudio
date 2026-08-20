using System.Collections.Immutable;
using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Automation;

/// <summary>
/// Manual analysis presets contain technical timing limits only. Legacy genre
/// ids are accepted for migration but never restore their former AI rules.
/// </summary>
public static class GameEditingProfiles
{
    private static readonly HashSet<string> LegacyIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "rust", "terraria", "mobile-legends", "generic-survival",
        "generic-sandbox", "generic-moba", "anime"
    };

    public static ImmutableArray<GameEditingProfile> BuiltIn { get; } =
    [
        CreateTechnical("universal", "Сбалансированный анализ", 1, 30, 4, 4),
        CreateTechnical("technical-fast", "Быстрый технический анализ", 0.5, 12, 2, 2),
        CreateTechnical("technical-detailed", "Подробный технический анализ", 2, 60, 8, 8)
    ];

    public static GameEditingProfile Get(string id)
    {
        var profile = BuiltIn.FirstOrDefault(item =>
            item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (profile is not null)
        {
            return profile;
        }

        if (LegacyIds.Contains(id))
        {
            return BuiltIn[0];
        }

        throw new KeyNotFoundException($"Профиль анализа «{id}» не найден.");
    }

    public static GameEditingProfile ValidateCustom(GameEditingProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Id) ||
            string.IsNullOrWhiteSpace(profile.DisplayName) ||
            profile.Version < 1)
        {
            throw new ArgumentException("Пользовательский профиль должен иметь ID, название и положительную версию.");
        }

        if (profile.MinimumSegmentSeconds is <= 0 or > 120 ||
            profile.MaximumSegmentSeconds < profile.MinimumSegmentSeconds ||
            profile.MaximumSegmentSeconds > 600 ||
            profile.ContextBeforeSeconds is < 0 or > 120 ||
            profile.ContextAfterSeconds is < 0 or > 120)
        {
            throw new ArgumentException("Временные параметры пользовательского профиля некорректны.");
        }

        return profile with
        {
            Id = profile.Id.Trim().ToLowerInvariant(),
            DisplayName = profile.DisplayName.Trim(),
            GameFamily = "Technical",
            EventTags = ImmutableArray<string>.Empty,
            EventWeights = ImmutableDictionary<string, double>.Empty,
            PlanningGuidance = string.Empty,
            Kind = MaterialProfileKind.General
        };
    }

    private static GameEditingProfile CreateTechnical(
        string id,
        string displayName,
        double minimum,
        double maximum,
        double before,
        double after)
        => new(
            id,
            2,
            displayName,
            "Technical",
            ImmutableArray<string>.Empty,
            ImmutableDictionary<string, double>.Empty,
            minimum,
            maximum,
            before,
            after,
            string.Empty,
            MaterialProfileKind.General);
}
