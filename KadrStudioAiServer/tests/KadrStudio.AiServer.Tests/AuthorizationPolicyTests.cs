using System.Net;
using KadrStudio.AiServer.Configuration;
using KadrStudio.AiServer.Infrastructure;
using Microsoft.AspNetCore.Http;

namespace KadrStudio.AiServer.Tests;

public sealed class AuthorizationPolicyTests
{
    [Fact]
    public async Task Local_mode_allows_loopback_without_bearer()
    {
        var result = await InvokeAsync(
            new AiServerOptions { AccessMode = AiServerAccessMode.Local },
            IPAddress.Loopback);

        Assert.True(result.NextCalled);
        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
    }

    [Fact]
    public async Task Remote_mode_requires_bearer_through_a_loopback_reverse_proxy()
    {
        var result = await InvokeAsync(RemoteOptions(), IPAddress.Loopback);

        Assert.False(result.NextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);
    }

    [Fact]
    public async Task Remote_mode_rejects_invalid_bearer_even_when_direct_peer_is_loopback()
    {
        var result = await InvokeAsync(RemoteOptions(), IPAddress.Loopback, "Bearer wrong");

        Assert.False(result.NextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);
    }

    [Fact]
    public async Task Remote_mode_accepts_valid_bearer_for_direct_remote_peer()
    {
        var result = await InvokeAsync(RemoteOptions(), IPAddress.Parse("203.0.113.10"), "Bearer secret");

        Assert.True(result.NextCalled);
        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
    }

    [Fact]
    public async Task Remote_live_health_also_requires_bearer()
    {
        var result = await InvokeAsync(RemoteOptions(), IPAddress.Loopback, path: "/health/live");

        Assert.False(result.NextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);
    }

    private static AiServerOptions RemoteOptions() => new()
    {
        AccessMode = AiServerAccessMode.Remote,
        ApiKey = "secret",
        ListenUrls = "https://0.0.0.0:5080"
    };

    private static async Task<MiddlewareResult> InvokeAsync(
        AiServerOptions options,
        IPAddress remoteAddress,
        string? authorization = null,
        string path = "/v2/jobs")
    {
        var nextCalled = false;
        var middleware = new KadrApiAuthorizationMiddleware(
            context =>
            {
                nextCalled = true;
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            options);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = remoteAddress;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        if (authorization is not null)
            context.Request.Headers.Authorization = authorization;

        await middleware.InvokeAsync(context);

        return new MiddlewareResult(nextCalled, context.Response.StatusCode);
    }

    private sealed record MiddlewareResult(bool NextCalled, int StatusCode);
}
