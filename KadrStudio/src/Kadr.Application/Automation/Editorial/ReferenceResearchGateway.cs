using System.Collections.Immutable;
using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Automation.Editorial;

public sealed record ReferenceResearchRequest(
    string Query,
    string Purpose,
    bool UserExplicitlyRequested);

public sealed record ReferenceSearchResult(
    string Url,
    string Title,
    string Citation);

public interface IExternalReferenceSearchProvider
{
    Task<ImmutableArray<ReferenceSearchResult>> SearchAsync(
        string query,
        CancellationToken cancellationToken);
}

public interface IReferenceResearchGateway
{
    Task<ImmutableArray<ExternalReference>> ResearchAsync(
        ReferenceResearchRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Opt-in boundary for public reference research. Requests contain text only:
/// local frames, audio, proxy assets and paths are never accepted by this API.
/// </summary>
public sealed class ReferenceResearchGateway(
    IExternalReferenceSearchProvider provider,
    bool enabled = false) : IReferenceResearchGateway
{
    private readonly IExternalReferenceSearchProvider _provider =
        provider ?? throw new ArgumentNullException(nameof(provider));

    public async Task<ImmutableArray<ExternalReference>> ResearchAsync(
        ReferenceResearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.UserExplicitlyRequested)
            throw new InvalidOperationException("Внешний поиск разрешён только по явному запросу пользователя.");
        if (!enabled)
            throw new InvalidOperationException("ReferenceResearchGateway выключен в настройках.");
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new ArgumentException("Reference research query is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Purpose))
            throw new ArgumentException("Reference research purpose is required.", nameof(request));

        var query = request.Query.Trim();
        var results = await _provider.SearchAsync(query, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        return (results.IsDefault ? [] : results)
            .Where(IsSafePublicResult)
            .Take(20)
            .Select(item => new ExternalReference(
                Guid.NewGuid(), item.Url.Trim(), item.Title.Trim(), item.Citation.Trim(),
                now, true, query))
            .ToImmutableArray();
    }

    private static bool IsSafePublicResult(ReferenceSearchResult item)
        => Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) &&
           uri.Scheme is "http" or "https" &&
           !string.IsNullOrWhiteSpace(item.Title) &&
           !string.IsNullOrWhiteSpace(item.Citation);
}
