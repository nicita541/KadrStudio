using System.Net;
using System.Net.Http;
using System.Text.Json;
using KadrStudio.Services;
using KadrStudio.Services.Editorial;

namespace KadrStudio.UiAdapters.Tests;

public sealed class OwnedJobClientTests
{
    [Fact]
    public async Task Sends_owner_and_request_identity_and_escapes_cancel_owner()
    {
        var handler = new Handler();
        using var connection = new AiServerConnection(new AiServerClientOptions(new Uri("http://localhost/")), handler);
        var client = new AiServerV2Client(connection);
        var id = await client.StartJobAsync("embedding", "2", [], new { query = "fixture" }, default, "owner & русский", "request-1");
        Assert.Equal(handler.Id, id);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("owner & русский", body.RootElement.GetProperty("ownerId").GetString());
        Assert.Equal("request-1", body.RootElement.GetProperty("requestId").GetString());
        await client.CancelJobAsync(id, default, "owner & русский");
        Assert.Equal("?ownerId=" + Uri.EscapeDataString("owner & русский"), handler.CancelUri!.Query);
    }

    [Theory]
    [InlineData(0, "owner & русский")]
    [InlineData(1, "different-owner")]
    public async Task Rejects_active_job_without_confirmed_ownership(int version, string owner)
    {
        var handler = new Handler(version, owner);
        using var connection = new AiServerConnection(new AiServerClientOptions(new Uri("http://localhost/")), handler);
        var client = new AiServerV2Client(connection);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.StartJobAsync("embedding", "2", [], new { query = "fixture" }, default,
            "owner & русский", "request-1"));
        Assert.Null(handler.CancelUri);
    }

    private sealed class Handler(int version = 1, string owner = "owner & русский") : HttpMessageHandler
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string? Body { get; private set; }
        public Uri? CancelUri { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                Body = await request.Content!.ReadAsStringAsync(cancellationToken);
                return new(HttpStatusCode.Accepted) { Content = new StringContent(JsonSerializer.Serialize(new { id = Id,
                    state = "Queued", ownerId = owner, ownershipProtocolVersion = version })) };
            }
            CancelUri = request.RequestUri;
            return new(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }
}
