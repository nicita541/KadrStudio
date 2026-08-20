using System.Net;
using KadrStudio.AiServer.Api;
using KadrStudio.AiServer.Configuration;
using KadrStudio.AiServer.Infrastructure;
using KadrStudio.AiServer.Inference;

var builder = WebApplication.CreateBuilder(args);
var options = AiServerOptions.FromEnvironment();

var configuredUrls = builder.Configuration["urls"];
if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KADR_AI_URLS")))
{
    builder.WebHost.UseUrls(options.ListenUrls);
}
else if (string.IsNullOrWhiteSpace(configuredUrls))
{
    builder.WebHost.UseUrls(AiServerOptions.DefaultListenUrls);
}

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.MaxRequestBodySize = options.MaxRequestBodyBytes;
});

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(_ =>
{
    var handler = new SocketsHttpHandler
    {
        UseProxy = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    };
    return new HttpClient(handler, disposeHandler: true)
    {
        BaseAddress = options.OllamaEndpoint,
        Timeout = options.RequestTimeout
    };
});
builder.Services.AddSingleton<OllamaRuntime>();
builder.Services.AddHostedService<OllamaWarmupService>();

var app = builder.Build();

app.UseMiddleware<KadrApiAuthorizationMiddleware>();

app.MapGet("/health/live", () => Results.Json(new
{
    status = "live",
    service = "kadr-ai-server",
    version = "0.1.0"
}));

app.MapGet("/health/ready", (OllamaRuntime runtime) =>
{
    var status = runtime.Status;
    var statusCode = status.State == OllamaRuntimeState.Ready
        ? StatusCodes.Status200OK
        : StatusCodes.Status503ServiceUnavailable;
    return Results.Json(new
    {
        status = status.State.ToString().ToLowerInvariant(),
        message = status.Message,
        updatedAt = status.UpdatedAt
    }, statusCode: statusCode);
});

app.MapGet("/health", (OllamaRuntime runtime) =>
{
    var status = runtime.Status;
    var statusCode = status.State == OllamaRuntimeState.Ready
        ? StatusCodes.Status200OK
        : StatusCodes.Status503ServiceUnavailable;
    return Results.Json(new
    {
        status = status.State.ToString().ToLowerInvariant(),
        message = status.Message
    }, statusCode: statusCode);
});

app.MapKadrV1Endpoints();

app.Run();

public partial class Program
{
}
