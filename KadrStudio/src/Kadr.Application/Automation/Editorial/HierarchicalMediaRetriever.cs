using System.Collections.Immutable;
using System.Text.RegularExpressions;
using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Automation.Editorial;

/// <summary>
/// Builds a bounded, source-aware working set. Anime retrieval deliberately
/// reserves capacity for the head and tail of every source so an ending cannot
/// disappear merely because lexical scores are tied.
/// </summary>
public sealed class HierarchicalMediaRetriever(
    int maximumChapters = 12,
    int maximumScenes = 40,
    int maximumShots = 160,
    int maximumFacts = 640,
    ISemanticNodeRanker? semanticRanker = null) : IHierarchicalMediaRetriever
{
    public async Task<EditorialWorkingSet> RetrieveAsync(
        MediaUnderstandingCatalog indexes,
        EditorialBrief brief,
        ImmutableArray<EvidenceGap> gaps = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        ArgumentNullException.ThrowIfNull(brief);
        var allIndexes = indexes.Indexes;
        var anime = brief.Profile.Kind == MontageProfileKind.AnimeEpisode;
        var terms = Tokenize(string.Join(' ',
            brief.Goal,
            brief.Style,
            string.Join(' ', brief.ProtectedContent),
            string.Join(' ', brief.AcceptanceCriteria),
            anime ? "opening ending op ed credits post credits preview episode title music" : string.Empty));
        var requested = (gaps.IsDefault ? [] : gaps)
            .Select(item => new TemporalLocator(item.SourceId, item.SourceRange))
            .ToArray();
        var query = string.Join(' ',
            brief.Goal,
            brief.Style,
            string.Join(' ', brief.ProtectedContent),
            string.Join(' ', brief.AcceptanceCriteria),
            anime ? "anime opening ending credits post-credit preview recap" : string.Empty);
        var semanticScores = semanticRanker is null
            ? ImmutableDictionary<Guid, double>.Empty
            : await semanticRanker.RankAsync(
                query,
                BuildSemanticDocuments(allIndexes),
                cancellationToken).ConfigureAwait(false);

        var roleHypotheses = allIndexes
            .SelectMany(index => index.SegmentRoleHypotheses)
            .Where(item => !anime || item.Role is SegmentRole.Opening or SegmentRole.Ending or
                           SegmentRole.PostCredits or SegmentRole.Preview or SegmentRole.EpisodeBody)
            .OrderByDescending(item => RolePriority(item.Role))
            .ThenByDescending(item => item.Confidence)
            .ThenBy(item => item.SourceRange.Start)
            .ToImmutableArray();
        var reserved = roleHypotheses
            .Select(item => new TemporalLocator(item.SourceId, item.SourceRange))
            .Concat(requested)
            .ToArray();

        var chapterCandidates = Rank(
            allIndexes.SelectMany(index => index.Chapters),
            item => item.Id, item => item.SourceId, item => item.Summary, item => item.SourceRange,
            terms, reserved, semanticScores).ToArray();
        var chapters = TakeBalanced(
            chapterCandidates, maximumChapters, item => item.SourceId,
            item => item.SourceRange, allIndexes, anime).ToImmutableArray();

        var selectedSceneIds = chapters.SelectMany(item => item.SceneIds).ToHashSet();
        var sceneCandidates = Rank(
            allIndexes.SelectMany(index => index.Scenes)
                .Where(item => selectedSceneIds.Count == 0 || selectedSceneIds.Contains(item.Id) ||
                               reserved.Any(range => Overlaps(range, item.SourceId, item.SourceRange))),
            item => item.Id, item => item.SourceId, item => item.Summary, item => item.SourceRange,
            terms, reserved, semanticScores).ToArray();
        var scenes = TakeBalanced(
            sceneCandidates, maximumScenes, item => item.SourceId,
            item => item.SourceRange, allIndexes, anime).ToImmutableArray();

        var selectedShotIds = scenes.SelectMany(item => item.ShotIds).ToHashSet();
        var shotCandidates = Rank(
            allIndexes.SelectMany(index => index.Shots)
                .Where(item => selectedShotIds.Count == 0 || selectedShotIds.Contains(item.Id) ||
                               reserved.Any(range => Overlaps(range, item.SourceId, item.SourceRange))),
            item => item.Id, item => item.SourceId, item => item.Summary, item => item.SourceRange,
            terms, reserved, semanticScores).ToArray();
        var shots = TakeBalanced(
            shotCandidates, maximumShots, item => item.SourceId,
            item => item.SourceRange, allIndexes, anime).ToImmutableArray();

        var selectedRanges = scenes.Select(item => new TemporalLocator(item.SourceId, item.SourceRange))
            .Concat(shots.Select(item => new TemporalLocator(item.SourceId, item.SourceRange)))
            .Concat(reserved)
            .Distinct()
            .ToArray();
        var factCandidates = allIndexes.SelectMany(index => index.Facts)
            .Where(item => selectedRanges.Length == 0 ||
                           selectedRanges.Any(range => Overlaps(range, item.SourceId, item.SourceRange)))
            .OrderByDescending(item => requested.Any(range => Overlaps(range, item.SourceId, item.SourceRange)))
            .ThenByDescending(item => reserved.Any(range => Overlaps(range, item.SourceId, item.SourceRange)))
            .ThenByDescending(item => semanticScores.GetValueOrDefault(item.Id))
            .ThenByDescending(item => Score(item.Summary, terms))
            .ThenByDescending(item => item.Confidence)
            .ThenBy(item => item.SourceId)
            .ThenBy(item => item.SourceRange.Start)
            .ToArray();
        var facts = TakeBalanced(
            factCandidates, maximumFacts, item => item.SourceId,
            item => item.SourceRange, allIndexes, anime).ToImmutableArray();
        var counterEvidence = facts
            .Where(item => item.Confidence < 0.6 ||
                           item.Attributes.TryGetValue("contradicts", out var value) &&
                           value.Equals("true", StringComparison.OrdinalIgnoreCase))
            .ToImmutableArray();
        var factRanges = facts.Select(item => new TemporalLocator(item.SourceId, item.SourceRange)).ToArray();
        var hypotheses = allIndexes.SelectMany(index => index.SemanticHypotheses)
            .Where(item => selectedRanges.Length == 0 ||
                           selectedRanges.Any(range => Overlaps(range, item.SourceId, item.SourceRange)))
            .OrderByDescending(item => Score(item.Summary, terms))
            .ThenByDescending(item => item.Confidence)
            .Take(Math.Max(40, maximumFacts / 4))
            .ToImmutableArray();
        return new EditorialWorkingSet(
            allIndexes.Select(index => index.Id).ToImmutableArray(),
            chapters,
            scenes,
            shots,
            facts,
            hypotheses,
            allIndexes.SelectMany(index => index.AudioEvents)
                .Where(item => factRanges.Any(range => Overlaps(range, item.SourceId, item.SourceRange)))
                .ToImmutableArray(),
            allIndexes.SelectMany(index => index.SpeakerTurns)
                .Where(item => factRanges.Any(range => Overlaps(range, item.SourceId, item.SourceRange)))
                .ToImmutableArray(),
            counterEvidence,
            allIndexes.ToImmutableDictionary(
                index => index.SourceId,
                index => index.Coverage.Channels
                    .Where(channel => !channel.Value.IsDefaultOrEmpty)
                    .Select(channel => channel.Key)
                    .OrderBy(channel => channel)
                    .ToImmutableArray()),
            roleHypotheses);
    }

    private static IEnumerable<T> Rank<T>(
        IEnumerable<T> source,
        Func<T, Guid> id,
        Func<T, Guid> sourceId,
        Func<T, string> summary,
        Func<T, TimeRange> range,
        IReadOnlySet<string> terms,
        IReadOnlyCollection<TemporalLocator> requestedRanges,
        IReadOnlyDictionary<Guid, double> semanticScores)
        => source
            .OrderByDescending(item => requestedRanges.Any(requested =>
                Overlaps(requested, sourceId(item), range(item))))
            .ThenByDescending(item => semanticScores.GetValueOrDefault(id(item)))
            .ThenByDescending(item => Score(summary(item), terms))
            .ThenBy(item => sourceId(item))
            .ThenBy(item => range(item).Start);

    private static IEnumerable<T> TakeBalanced<T>(
        IReadOnlyList<T> ranked,
        int maximum,
        Func<T, Guid> sourceId,
        Func<T, TimeRange> range,
        ImmutableArray<MediaUnderstandingIndex> indexes,
        bool reservePositionStrata)
    {
        if (!reservePositionStrata || ranked.Count <= maximum) return ranked.Take(maximum);
        var reserved = new List<T>();
        foreach (var index in indexes)
        {
            var candidates = ranked.Where(item => sourceId(item) == index.SourceId).ToArray();
            if (candidates.Length == 0) continue;
            for (var stratum = 0; stratum < 4; stratum++)
            {
                var start = new TimelineTime(index.SourceDuration.Ticks * stratum / 4);
                var end = new TimelineTime(index.SourceDuration.Ticks * (stratum + 1) / 4);
                var selected = candidates.FirstOrDefault(item =>
                    range(item).Start < end && range(item).End > start);
                if (selected is not null && !reserved.Contains(selected)) reserved.Add(selected);
            }
        }
        foreach (var item in ranked)
        {
            if (reserved.Count >= maximum) break;
            if (!reserved.Contains(item)) reserved.Add(item);
        }
        return reserved.Take(maximum);
    }

    private static int RolePriority(SegmentRole role) => role switch
    {
        SegmentRole.Opening or SegmentRole.Ending => 3,
        SegmentRole.PostCredits or SegmentRole.Preview => 2,
        SegmentRole.EpisodeBody => 1,
        _ => 0
    };

    private static int Score(string value, IReadOnlySet<string> terms)
    {
        if (terms.Count == 0 || string.IsNullOrWhiteSpace(value)) return 0;
        var words = Tokenize(value);
        return words.Count(terms.Contains);
    }

    private static HashSet<string> Tokenize(string value)
        => Regex.Matches((value ?? string.Empty).ToLowerInvariant(), @"[\p{L}\p{N}]{3,}")
            .Select(match => match.Value)
            .ToHashSet(StringComparer.Ordinal);

    private static ImmutableArray<SemanticRetrievalDocument> BuildSemanticDocuments(
        ImmutableArray<MediaUnderstandingIndex> indexes)
        => indexes.SelectMany(index =>
                index.Chapters.Select(item => new SemanticRetrievalDocument(
                        item.Id, item.SourceId, item.SourceRange, item.Summary, item.EmbeddingReference))
                    .Concat(index.Scenes.Select(item => new SemanticRetrievalDocument(
                        item.Id, item.SourceId, item.SourceRange, item.Summary, item.EmbeddingReference)))
                    .Concat(index.Shots.Select(item => new SemanticRetrievalDocument(
                        item.Id, item.SourceId, item.SourceRange, item.Summary, item.EmbeddingReference)))
                    .Concat(index.Facts.Select(item => new SemanticRetrievalDocument(
                        item.Id, item.SourceId, item.SourceRange,
                        $"{item.Kind}: {item.Summary}", item.ArtifactReference))))
            .Where(item => !string.IsNullOrWhiteSpace(item.Text))
            .GroupBy(item => item.NodeId)
            .Select(group => group.First())
            .ToImmutableArray();

    private static bool Overlaps(TemporalLocator locator, Guid sourceId, TimeRange range)
        => locator.SourceId == sourceId && locator.Range.Overlaps(range);

    private sealed record TemporalLocator(Guid SourceId, TimeRange Range);
}
