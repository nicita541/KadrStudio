using System.Net;
using KadrStudio.AiServer.Configuration;

namespace KadrStudio.AiServer.Infrastructure;

public sealed class KadrApiAuthorizationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly AiServerOptions _options;

    public KadrApiAuthorizationMiddleware(RequestDelegate next, AiServerOptions options)
    {
        _next = next;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (string.Equals(
                context.Request.Path.Value,
                "/health/live",
                StringComparison.OrdinalIgnoreCase))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var remoteAddress = context.Connection.RemoteIpAddress;
        if (remoteAddress is not null && IPAddress.IsLoopback(remoteAddress))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_options.ApiKey) &&
            ApiKeyValidator.IsValidBearerHeader(
                context.Request.Headers["Authorization"].ToString(),
                _options.ApiKey))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new
        {
            error = "Remote access to Kadr AI Server requires a valid Bearer API key."
        }).ConfigureAwait(false);
    }
}
