using System.Collections.Immutable;
using KadrStudio.Application.Automation.Editorial;

namespace KadrStudio.Services.Editorial;

/// <summary>
/// Runs semantic ranking next to the external vector store. Only compact node
/// text and IDs cross the trust gateway; vectors never enter the model context.
/// </summary>
public sealed class AiServerSemanticNodeRanker(AiServerV2Client client) : ISemanticNodeRanker
{
    public async Task<ImmutableDictionary<Guid, double>> RankAsync(
        string query,
        ImmutableArray<SemanticRetrievalDocument> documents,
        CancellationToken cancellationToken)
    {
        if (documents.IsDefaultOrEmpty || string.IsNullOrWhiteSpace(query))
            return ImmutableDictionary<Guid, double>.Empty;

        var jobId = await client.StartJobAsync(
            "embedding",
            "2",
            [],
            new
            {
                query = query.Trim(),
                topK = Math.Min(documents.Length, 1024),
                documents = documents.Select(item => new
                {
                    id = item.NodeId,
                    sourceId = item.SourceId,
                    startTicks = item.SourceRange.Start.Ticks,
                    durationTicks = item.SourceRange.Duration.Ticks,
                    text = item.Text
                })
            },
            cancellationToken).ConfigureAwait(false);
        var job = await client.WaitForJobAsync(jobId, null, cancellationToken).ConfigureAwait(false);
        var allowed = documents.Select(item => item.NodeId).ToHashSet();
        foreach (var artifactId in job.ArtifactIds)
        {
            using var artifact = await client.GetArtifactAsync(artifactId, cancellationToken).ConfigureAwait(false);
            var root = artifact.RootElement;
            if (!root.TryGetProperty("kind", out var kind) ||
                !string.Equals(kind.GetString(), "semantic-search", StringComparison.Ordinal) ||
                !root.TryGetProperty("hits", out var hits))
                continue;
            var result = ImmutableDictionary.CreateBuilder<Guid, double>();
            foreach (var hit in hits.EnumerateArray())
            {
                if (!Guid.TryParse(hit.GetProperty("id").GetString(), out var id) || !allowed.Contains(id))
                    throw new InvalidDataException("Embedding worker returned a node outside the requested corpus.");
                var score = hit.GetProperty("score").GetDouble();
                if (!double.IsFinite(score) || score is < -1 or > 1)
                    throw new InvalidDataException("Embedding worker returned an invalid cosine score.");
                result[id] = score;
            }
            return result.ToImmutable();
        }
        throw new InvalidDataException("Embedding worker completed without a semantic-search artifact.");
    }
}
