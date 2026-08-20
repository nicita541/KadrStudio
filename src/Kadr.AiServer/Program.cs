using System.Net;
using KadrStudio.AiServer.Api;
using KadrStudio.AiServer.Configuration;
using KadrStudio.AiServer.Infrastructure;
using KadrStudio.AiServer.Inference;

var builder = WebApplication.CreateBuilder(args);
var options = AiServerOptions.FromEnvironment();

// The server is distributed as an interactive, self-contained console process.
// The default Windows host also registers EventLogLoggerProvider, whose native
// EventLog handle can be disposed before a cancelling BackgroundService finishes.
// Keeping a single console provider both avoids that shutdown race and makes the
// installed runtime logs visible to the operator who launched it.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(consoleOptions =>
{
    consoleOptions.SingleLine = true;
    consoleOptions.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});

var configuredUrls = builder.Configuration["urls"];
var effectiveListenUrls = configuredUrls;
if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KADR_AI_URLS")))
{
    builder.WebHost.UseUrls(options.ListenUrls);
    effectiveListenUrls = options.ListenUrls;
}
else if (string.IsNullOrWhiteSpace(configuredUrls))
{
    builder.WebHost.UseUrls(AiServerOptions.DefaultListenUrls);
    effectiveListenUrls = AiServerOptions.DefaultListenUrls;
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
builder.Services.AddSingleton<IInferenceChatRuntime>(serviceProvider =>
    serviceProvider.GetRequiredService<OllamaRuntime>());
builder.Services.AddSingleton<StructuredInferencePipeline>();
builder.Services.AddHostedService<OllamaWarmupService>();

var app = builder.Build();

if (ExistingAiServerProbe.IsStandaloneExecutable() &&
    await ExistingAiServerProbe.FindAsync(
        effectiveListenUrls ?? AiServerOptions.DefaultListenUrls) is { } runningServer)
{
    Console.WriteLine($"Kadr AI Server is already running at {runningServer}.");
    Console.WriteLine("Start KadrStudio.exe; do not launch a second server instance.");
    return;
}

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
