using KadrStudio.Application.Automation.Agent.Tools;

namespace KadrStudio.Application.Automation.Agent.Runtime;

internal static class AgentObservationRetention
{
    public static void Trim(
        List<AgentModelObservation> observations,
        AgentTaskState task,
        int maximumCount,
        int maximumCharacters)
    {
        var pinnedSequences = task.Plan?.Steps
            .SelectMany(step => step.EvidenceObservationSequences.IsDefault
                ? []
                : step.EvidenceObservationSequences)
            .ToHashSet() ?? [];

        while (observations.Count > maximumCount ||
               EstimateCharacters(observations) > maximumCharacters)
        {
            var removable = observations
                .Select((observation, index) => new
                {
                    Observation = observation,
                    Index = index,
                    Score = Score(observation, pinnedSequences)
                })
                .Where(candidate => candidate.Score < int.MaxValue)
                .OrderBy(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Observation.Sequence)
                .FirstOrDefault();
            if (removable is null)
            {
                break;
            }

            observations.RemoveAt(removable.Index);
        }
    }

    private static int Score(
        AgentModelObservation observation,
        IReadOnlySet<int> pinnedSequences)
    {
        if (pinnedSequences.Contains(observation.Sequence))
        {
            return int.MaxValue;
        }

        var score = Math.Max(0, observation.Sequence);
        if (string.Equals(
                observation.ToolName,
                "inspect_editor_context",
                StringComparison.OrdinalIgnoreCase))
        {
            score += 100_000;
        }

        if (observation.Status != AgentToolResultStatus.Succeeded)
        {
            score += 10_000;
        }

        return score;
    }

    private static int EstimateCharacters(IEnumerable<AgentModelObservation> observations)
        => observations.Sum(observation =>
            observation.ToolName.Length +
            observation.Summary.Length +
            (observation.ErrorCode?.Length ?? 0) +
            (observation.Data?.GetRawText().Length ?? 0));
}
