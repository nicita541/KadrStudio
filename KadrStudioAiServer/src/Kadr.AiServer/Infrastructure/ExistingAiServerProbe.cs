using System.Text.Json;

namespace KadrStudio.AiServer.Infrastructure;

public static class ExistingAiServerProbe
{
    public static bool IsStandaloneExecutable()
        => string.Equals(
            Path.GetFileNameWithoutExtension(Environment.ProcessPath),
            "KadrStudio.AiServer",
            StringComparison.OrdinalIgnoreCase);

    public static async Task<Uri?> FindAsync(
        string listenUrls,
        CancellationToken cancellationToken = default)
    {
        using var handler = new SocketsHttpHandler { UseProxy = false };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMilliseconds(800)
        };

        foreach (var raw in listenUrls.Split(
                     ';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") ||
                !uri.IsLoopback)
            {
                continue;
            }

            try
            {
                using var response = await client.GetAsync(
                    new Uri(uri, "/health/live"),
                    cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                await using var stream = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                using var json = await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                var root = json.RootElement;
                if (root.TryGetProperty("status", out var status) &&
                    status.GetString() == "live" &&
                    root.TryGetProperty("service", out var service) &&
                    service.GetString() == "kadr-ai-server")
                {
                    return new Uri(uri.GetLeftPart(UriPartial.Authority));
                }
            }
            catch (Exception exception) when (
                exception is HttpRequestException or TaskCanceledException or JsonException)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        return null;
    }
}
