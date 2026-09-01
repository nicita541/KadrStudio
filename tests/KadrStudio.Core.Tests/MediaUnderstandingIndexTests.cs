using System.Collections.Immutable;
using KadrStudio.Application.Automation.Editorial;
using KadrStudio.Core.Domain;

namespace KadrStudio.Core.Tests;

public sealed class MediaUnderstandingIndexTests
{
    [Fact]
    public void Sparse_samples_do_not_claim_continuous_coverage()
    {
        var sourceId = Guid.NewGuid();
        var duration = TimelineTime.FromSeconds(120);
        var map = new CoverageMap(
            sourceId,
            "sha256:test",
            duration,
            ImmutableDictionary<CoverageChannel, ImmutableArray<CoverageInterval>>.Empty)
            .Add(
                CoverageChannel.Frames,
                CoverageInterval.Create(
                    new TimeRange(TimelineTime.Zero, duration),
                    sampleCount: 8,
                    isContinuous: false,
                    analyzerId: "contact-sheet",
                    analyzerVersion: "1"));

        Assert.True(map.Covers(CoverageChannel.Frames, new TimeRange(TimelineTime.Zero, duration)));
        Assert.False(map.Covers(
            CoverageChannel.Frames,
            new TimeRange(TimelineTime.Zero, duration),
            requireContinuous: true));
    }

    [Fact]
    public void Coverage_map_returns_exact_unmeasured_gaps()
    {
        var sourceId = Guid.NewGuid();
        var duration = TimelineTime.FromSeconds(120);
        var map = new CoverageMap(
            sourceId,
            "sha256:test",
            duration,
            ImmutableDictionary<CoverageChannel, ImmutableArray<CoverageInterval>>.Empty)
            .Add(
                CoverageChannel.Audio,
                CoverageInterval.Create(
                    new TimeRange(TimelineTime.Zero, TimelineTime.FromSeconds(30)),
                    sampleCount: 1_440_000,
                    isContinuous: true,
                    analyzerId: "audio-events",
                    analyzerVersion: "1"))
            .Add(
                CoverageChannel.Audio,
                CoverageInterval.Create(
                    new TimeRange(TimelineTime.FromSeconds(60), TimelineTime.FromSeconds(60)),
                    sampleCount: 2_880_000,
                    isContinuous: true,
                    analyzerId: "audio-events",
                    analyzerVersion: "1"));

        var gap = Assert.Single(map.MissingRanges(
            CoverageChannel.Audio,
            new TimeRange(TimelineTime.Zero, duration),
            requireContinuous: true));

        Assert.Equal(TimelineTime.FromSeconds(30), gap.Start);
        Assert.Equal(TimelineTime.FromSeconds(30), gap.Duration);
    }

    [Fact]
    public async Task Hierarchical_retrieval_uses_external_semantic_scores_instead_of_lexical_ties()
    {
        var sourceId = Guid.NewGuid();
        var first = new SceneNode(
            Guid.NewGuid(), sourceId,
            new TimeRange(TimelineTime.Zero, TimelineTime.FromSeconds(10)),
            [], [], "visually similar scene");
        var ending = new SceneNode(
            Guid.NewGuid(), sourceId,
            new TimeRange(TimelineTime.FromSeconds(90), TimelineTime.FromSeconds(10)),
            [], [], "visually similar scene");
        var now = DateTimeOffset.UtcNow;
        var index = new MediaUnderstandingIndex(
            Guid.NewGuid(), sourceId, "fingerprint", "v2.1", TimelineTime.FromSeconds(100),
            new CoverageMap(sourceId, "fingerprint", TimelineTime.FromSeconds(100),
                ImmutableDictionary<CoverageChannel, ImmutableArray<CoverageInterval>>.Empty),
            [], [], [first, ending], [], [], [], [], [], now, now);
        var profile = MontageProfileCatalog.Get(MontageProfileKind.Generic);
        var brief = new EditorialBrief(
            Guid.NewGuid(), "find the ending", "viewer", profile, null,
            "semantic", [], [], now);
        var ranker = new FixedSemanticRanker(ending.Id);
        var retriever = new HierarchicalMediaRetriever(maximumScenes: 1, semanticRanker: ranker);

        var workingSet = await retriever.RetrieveAsync(new MediaUnderstandingCatalog([index]), brief);

        Assert.Equal(ending.Id, Assert.Single(workingSet.Scenes).Id);
        Assert.Contains(ranker.Documents, item => item.NodeId == first.Id);
        Assert.Contains(ranker.Documents, item => item.NodeId == ending.Id);
    }

    private sealed class FixedSemanticRanker(Guid selectedId) : ISemanticNodeRanker
    {
        public ImmutableArray<SemanticRetrievalDocument> Documents { get; private set; } = [];

        public Task<ImmutableDictionary<Guid, double>> RankAsync(
            string query,
            ImmutableArray<SemanticRetrievalDocument> documents,
            CancellationToken cancellationToken)
        {
            Documents = documents;
            return Task.FromResult(ImmutableDictionary<Guid, double>.Empty.Add(selectedId, 0.99));
        }
    }
}
