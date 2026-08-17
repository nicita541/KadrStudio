using System.Collections.Immutable;
using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Automation;

public static class GameEditingProfiles
{
    public static ImmutableArray<GameEditingProfile> BuiltIn { get; } =
    [
        Create(
            "rust", "Rust", "Survival",
            ["loot", "building", "pvp", "raid", "death", "recovery", "payoff"],
            [("raid", 1.0), ("pvp", 0.92), ("payoff", 0.95), ("death", 0.78), ("loot", 0.62)],
            3, 28, 6, 5,
            "Строй историю через подготовку, риск и результат. Перед рейдом, PvP и потерей оставляй контекст; пустое перемещение сокращай."),
        Create(
            "terraria", "Terraria", "Sandbox",
            ["exploration", "crafting", "upgrade", "boss", "failure", "victory", "payoff"],
            [("boss", 1.0), ("victory", 1.0), ("upgrade", 0.8), ("failure", 0.72), ("crafting", 0.55)],
            2, 20, 4, 4,
            "Показывай прогресс: цель, подготовку, улучшение экипировки, попытку и итог. Повторяющийся фарм сокращай."),
        Create(
            "mobile-legends", "Mobile Legends", "MOBA",
            ["kill", "death", "assist", "teamfight", "objective", "comeback", "result"],
            [("teamfight", 1.0), ("comeback", 1.0), ("objective", 0.88), ("kill", 0.82), ("result", 0.9)],
            1.2, 12, 2.5, 2,
            "Держи высокий темп, но сохраняй короткий заход перед командной дракой или объектом и показывай результат события."),
        Create(
            "generic-survival", "Generic Survival", "Survival",
            ["exploration", "resource", "danger", "combat", "loss", "success"],
            [("danger", 0.9), ("combat", 0.9), ("loss", 0.75), ("success", 0.85)],
            3, 25, 5, 4,
            "Собирай причинно-следственную историю выживания, сохраняя подготовку перед риском и результат после него."),
        Create(
            "generic-sandbox", "Generic Sandbox", "Sandbox",
            ["goal", "build", "discovery", "progress", "failure", "result"],
            [("result", 1.0), ("discovery", 0.86), ("progress", 0.76), ("failure", 0.7)],
            2, 22, 4, 4,
            "Выделяй цель, заметные этапы прогресса и финальный результат; рутинные повторы сокращай."),
        Create(
            "generic-moba", "Generic MOBA", "MOBA",
            ["kill", "death", "teamfight", "objective", "turnaround", "result"],
            [("teamfight", 1.0), ("turnaround", 1.0), ("objective", 0.88), ("result", 0.9)],
            1.2, 12, 2.5, 2,
            "Используй быстрый темп, отбирай драки и цели, добавляя минимальный контекст для понимания результата.")
    ];

    public static GameEditingProfile Get(string id)
        => BuiltIn.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
           ?? throw new KeyNotFoundException($"Профиль игры «{id}» не найден.");

    public static GameEditingProfile ValidateCustom(GameEditingProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.DisplayName) || profile.Version < 1)
            throw new ArgumentException("Пользовательский профиль должен иметь ID, название и положительную версию.");
        if (profile.MinimumSegmentSeconds is <= 0 or > 120 ||
            profile.MaximumSegmentSeconds < profile.MinimumSegmentSeconds || profile.MaximumSegmentSeconds > 600 ||
            profile.ContextBeforeSeconds is < 0 or > 120 || profile.ContextAfterSeconds is < 0 or > 120)
            throw new ArgumentException("Временные параметры пользовательского профиля некорректны.");
        if (profile.EventWeights.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Value is < 0 or > 1))
            throw new ArgumentException("Веса событий должны находиться в диапазоне 0–1.");
        return profile with
        {
            Id = profile.Id.Trim().ToLowerInvariant(),
            DisplayName = profile.DisplayName.Trim(),
            GameFamily = profile.GameFamily.Trim(),
            PlanningGuidance = profile.PlanningGuidance.Trim()
        };
    }

    private static GameEditingProfile Create(
        string id,
        string displayName,
        string family,
        string[] tags,
        (string Key, double Value)[] weights,
        double minimum,
        double maximum,
        double before,
        double after,
        string guidance)
        => new(
            id,
            1,
            displayName,
            family,
            tags.ToImmutableArray(),
            weights.ToImmutableDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase),
            minimum,
            maximum,
            before,
            after,
            guidance);
}
