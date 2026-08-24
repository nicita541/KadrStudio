using System.Collections.Immutable;
using System.Text.Json;
using KadrStudio.Application.Automation.Agent.Tools;

namespace KadrStudio.Application.Automation.Agent.Verification;

public sealed record AgentVerificationProbe(
    Guid StepId,
    int StepOrder,
    double DraftBoundarySeconds,
    double WindowSeconds,
    string Query,
    AgentEvidenceCapabilities RequiredCapabilities);

/// <summary>
/// Maps approved edit coordinates to read-only post-edit probes. Each editing
/// capability can add an adapter here without teaching the workflow about a
/// particular genre or creative task.
/// </summary>
public static class AgentVerificationProbePlanner
{
    public static ImmutableArray<AgentVerificationProbe> Create(AgentPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var ranges = ImmutableArray.CreateBuilder<(AgentPlanStep Step, double Start, double End)>();

        foreach (var step in plan.Steps.OrderBy(item => item.Order))
        {
            if (step.ExpectedEditingArguments is not { ValueKind: JsonValueKind.Object } arguments)
            {
                continue;
            }
            if (step.EvidenceRequirement == AgentEvidenceRequirement.Timeline)
            {
                // Pure geometry is covered by receipts, integrity and sequence diff.
                // Every content channel approved during planning is measured again
                // at the resulting draft junction after the edit.
                continue;
            }

            if (string.Equals(step.ExpectedEditingTool, "ripple_delete_range", StringComparison.OrdinalIgnoreCase) &&
                TryReadRange(arguments, out var start, out var end))
            {
                ranges.Add((step, start, end));
            }
            else if (string.Equals(step.ExpectedEditingTool, "ripple_delete_ranges", StringComparison.OrdinalIgnoreCase) &&
                     arguments.TryGetProperty("ranges", out var rangeArray) &&
                     rangeArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var range in rangeArray.EnumerateArray())
                {
                    if (TryReadRange(range, out start, out end))
                    {
                        ranges.Add((step, start, end));
                    }
                }
            }
        }

        var probes = ImmutableArray.CreateBuilder<AgentVerificationProbe>();
        var removedBefore = 0d;
        foreach (var item in ranges
                     .OrderBy(item => item.Start)
                     .ThenBy(item => item.End))
        {
            var boundary = Math.Max(0, item.Start - removedBefore);
            foreach (var channel in RequiredChannels(item.Step.EvidenceRequirement))
            {
                probes.Add(new AgentVerificationProbe(
                    item.Step.Id,
                    item.Step.Order,
                    boundary,
                    5,
                    BuildObservationQuery(item.Step, channel),
                    channel));
            }
            removedBefore += item.End - item.Start;
        }

        return probes.ToImmutable();
    }

    private static bool TryReadRange(
        JsonElement value,
        out double start,
        out double end)
    {
        start = 0;
        end = 0;
        return value.ValueKind == JsonValueKind.Object &&
               value.TryGetProperty("start_seconds", out var startValue) &&
               startValue.TryGetDouble(out start) &&
               value.TryGetProperty("end_seconds", out var endValue) &&
               endValue.TryGetDouble(out end) &&
               double.IsFinite(start) &&
               double.IsFinite(end) &&
               start >= 0 &&
               end > start;
    }

    private static string BuildObservationQuery(
        AgentPlanStep step,
        AgentEvidenceCapabilities channel)
    {
        var instruction = channel switch
        {
            AgentEvidenceCapabilities.Frames =>
                "Опиши видимые кадры и изменения непосредственно до и после стыка с таймкодами.",
            AgentEvidenceCapabilities.Audio =>
                "Верни измеренные аудиособытия непосредственно до и после стыка: уровни, тишину и границы.",
            AgentEvidenceCapabilities.Transcript =>
                "Верни transcript cues непосредственно до и после стыка с точными таймкодами.",
            _ => "Верни только измеренные факты непосредственно до и после стыка."
        };
        return
            $"Постмонтажная проверка утверждённого шага {step.Order} «{step.Title}». " +
            $"Контекст цели шага: {step.Description}. {instruction} " +
            "Не решай, правильный ли стык, не классифицируй материал и не выставляй pass/fail.";
    }

    private static IEnumerable<AgentEvidenceCapabilities> RequiredChannels(
        AgentEvidenceRequirement requirement)
        => requirement switch
        {
            AgentEvidenceRequirement.Frames => [AgentEvidenceCapabilities.Frames],
            AgentEvidenceRequirement.Audio => [AgentEvidenceCapabilities.Audio],
            AgentEvidenceRequirement.Transcript => [AgentEvidenceCapabilities.Transcript],
            AgentEvidenceRequirement.All =>
                [AgentEvidenceCapabilities.Frames,
                 AgentEvidenceCapabilities.Audio,
                 AgentEvidenceCapabilities.Transcript],
            _ => [AgentEvidenceCapabilities.Timeline]
        };
}
