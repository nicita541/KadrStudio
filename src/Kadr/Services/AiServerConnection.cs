using System.Net.Http.Headers;
using System.Net.Http;

namespace KadrStudio.Services;

public sealed class AiServerConnection : IDisposable
{
    public const string DefaultPlannerModelAlias = "kadr-planner:latest";
    private readonly HttpClient _client;

    public AiServerConnection(
        AiServerClientOptions? options = null,
        HttpMessageHandler? messageHandler = null)
    {
        var resolved = options ?? AiServerClientOptions.FromEnvironment();
        var endpoint = resolved.Endpoint ?? AiServerClientOptions.DefaultServerEndpoint;
        _client = new HttpClient(messageHandler ?? new HttpClientHandler { UseProxy = !endpoint.IsLoopback })
        {
            BaseAddress = endpoint,
            Timeout = TimeSpan.FromHours(2)
        };
        if (!string.IsNullOrWhiteSpace(resolved.ApiKey))
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", resolved.ApiKey);
    }

    public Uri Endpoint => _client.BaseAddress!;
    internal HttpClient AuthorizedClient => _client;

    public async Task<bool> TryReleaseLocalAcceleratorAsync(CancellationToken cancellationToken = default)
    {
        if (!Endpoint.IsLoopback) return false;
        try
        {
            using var response = await _client.PostAsync(
                "v2/accelerator/release", new ByteArrayContent([]), cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return false;
            response.EnsureSuccessStatusCode();
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }
    public void Dispose() => _client.Dispose();
}

public sealed record AiServerClientOptions(Uri? Endpoint = null, string? ApiKey = null)
{
    public static readonly Uri DefaultServerEndpoint = new("http://127.0.0.1:5080/");

    public static AiServerClientOptions FromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable("KADR_STUDIO_AI_ENDPOINT");
        var endpoint = DefaultServerEndpoint;
        if (!string.IsNullOrWhiteSpace(value))
        {
            if (!Uri.TryCreate(value.TrimEnd('/') + "/", UriKind.Absolute, out var parsed) ||
                parsed is null || parsed.Scheme is not ("http" or "https"))
                throw new InvalidOperationException("KADR_STUDIO_AI_ENDPOINT must be an absolute HTTP(S) address.");
            endpoint = parsed;
        }
        return new AiServerClientOptions(endpoint, Environment.GetEnvironmentVariable("KADR_STUDIO_AI_API_KEY")?.Trim());
    }
}
