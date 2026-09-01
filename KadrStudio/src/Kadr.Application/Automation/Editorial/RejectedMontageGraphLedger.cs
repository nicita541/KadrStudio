using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Automation.Editorial;

public sealed class RejectedMontageGraphLedger
{
    private readonly ConcurrentDictionary<string, string> _rejections = new(StringComparer.Ordinal);

    public void Reject(MontageGraph graph, MediaUnderstandingCatalog indexes)
        => _rejections[graph.Fingerprint()] = indexes.EvidenceFingerprint();

    public bool CanSubmit(MontageGraph graph, MediaUnderstandingCatalog indexes)
        => !_rejections.TryGetValue(graph.Fingerprint(), out var rejectedEvidence) ||
           !rejectedEvidence.Equals(indexes.EvidenceFingerprint(), StringComparison.Ordinal);

    public static string EvidenceFingerprint(MediaUnderstandingIndex index)
    {
        var canonical = string.Join('|',
            index.SourceFingerprint,
            index.PipelineVersion,
            index.UpdatedAt.ToUnixTimeMilliseconds(),
            string.Join(',', index.Analyzers
                .OrderBy(item => item.Id)
                .ThenBy(item => item.Version)
                .Select(item => $"{item.Id}:{item.Version}")),
            string.Join(',', index.Facts
                .OrderBy(item => item.Id)
                .Select(item => item.Id.ToString("N"))),
            string.Join(',', index.SemanticHypotheses
                .OrderBy(item => item.Id)
                .Select(item => item.Id.ToString("N"))),
            string.Join(',', index.Coverage.Channels
                .OrderBy(item => item.Key)
                .SelectMany(item => item.Value)
                .OrderBy(item => item.Range.Start)
                .Select(item => $"{item.Range.Start.Ticks}:{item.Range.Duration.Ticks}:{item.SampleCount}")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
