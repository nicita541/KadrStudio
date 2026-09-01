using System.Collections.Immutable;
using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Automation.Editorial;

public sealed class MontageGraphTargetResolver
{
    public MontageGraph Resolve(ProjectState project, MontageGraph graph)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(graph);
        var sequence = project.FindSequence(graph.SourceSequenceId)
            ?? throw new InvalidOperationException("MontageGraph source sequence was not found.");
        if (sequence.Revision != graph.SourceSequenceRevision)
            throw new InvalidOperationException("MontageGraph target resolution requires the directed sequence revision.");

        var resolved = ImmutableArray.CreateBuilder<EditDecision>();
        var order = 0;
        foreach (var decision in graph.Decisions.OrderBy(item => item.Order).ThenBy(item => item.Id))
        {
            if (decision.Target is not null)
            {
                resolved.Add(decision with { Order = order++ });
                continue;
            }

            var occurrences = sequence.MediaClips
                .Where(item => item.SourceId == decision.SourceId &&
                               item.SourceIn < decision.SourceRange.End &&
                               item.SourceIn + item.Duration > decision.SourceRange.Start)
                .GroupBy(item => new
                {
                    item.SourceId,
                    TimelineSourceOffsetTicks = (item.Start - item.SourceIn).Ticks,
                    item.LinkGroupId
                })
                .OrderBy(group => group.Key.TimelineSourceOffsetTicks)
                .ThenBy(group => group.Key.LinkGroupId)
                .ToArray();
            if (occurrences.Length == 0)
            {
                resolved.Add(decision with { Order = order++ });
                continue;
            }

            foreach (var occurrence in occurrences)
            {
                var occurrenceSourceStart = occurrence.Min(item => item.SourceIn);
                var occurrenceSourceEnd = occurrence.Max(item => item.SourceIn + item.Duration);
                var sourceStart = occurrenceSourceStart > decision.SourceRange.Start
                    ? occurrenceSourceStart
                    : decision.SourceRange.Start;
                var sourceEnd = occurrenceSourceEnd < decision.SourceRange.End
                    ? occurrenceSourceEnd
                    : decision.SourceRange.End;
                if (sourceEnd <= sourceStart) continue;
                var sourceRange = new TimeRange(sourceStart, sourceEnd - sourceStart);
                var timelineRange = new TimeRange(
                    new TimelineTime(checked(sourceStart.Ticks + occurrence.Key.TimelineSourceOffsetTicks)),
                    sourceRange.Duration);
                var timelineObjectIds = occurrence.Select(item => item.Id)
                    .Concat(sequence.SubtitleClips.Where(item =>
                        item.SourceId == decision.SourceId &&
                        (occurrence.Key.LinkGroupId is { } group
                            ? item.LinkGroupId == group
                            : (item.Start - item.SourceIn).Ticks == occurrence.Key.TimelineSourceOffsetTicks) &&
                        item.SourceIn < sourceRange.End && item.SourceIn + item.Duration > sourceRange.Start)
                        .Select(item => item.Id))
                    .Order()
                    .ToImmutableArray();
                var target = new EditDecisionTarget(
                    sequence.Id,
                    sequence.Revision,
                    timelineRange,
                    decision.SourceId,
                    sourceRange,
                    timelineObjectIds);
                resolved.Add(decision with
                {
                    Id = occurrences.Length == 1 ? decision.Id : Guid.NewGuid(),
                    SourceRange = sourceRange,
                    Target = target,
                    Order = order++
                });
            }
        }

        return graph with { Decisions = resolved.ToImmutable() };
    }
}
