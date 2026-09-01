using KadrStudio.AiServer.Api;
using KadrStudio.AiServer.Configuration;
using KadrStudio.AiServer.Infrastructure;
using KadrStudio.AiServer.Inference;
using KadrStudio.AiServer.Jobs;
using KadrStudio.AiServer.Storage;
using KadrStudio.AiServer.Workers;

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
builder.Services.AddSingleton(serviceProvider =>
{
    var configured = serviceProvider.GetRequiredService<AiServerOptions>();
    return new ContentAddressedAssetStore(configured.DataRoot, configured.MaxAssetBytes);
});
builder.Services.AddSingleton(serviceProvider =>
{
    var configured = serviceProvider.GetRequiredService<AiServerOptions>();
    return new ContentAddressedArtifactStore(configured.DataRoot);
});
builder.Services.AddSingleton<IWorkerGateway, LoopbackGrpcWorkerGateway>();
builder.Services.AddSingleton<RoleStructuredReasoningService>();
builder.Services.AddSingleton<ModelCapabilityGate>();
builder.Services.AddSingleton<AnalyzerJobService>();
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<AnalyzerJobService>());

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

app.MapGet("/health/ready", async (AiServerOptions configured, ModelCapabilityGate gate, CancellationToken token) =>
{
    var requiredWorkers = new[] { "video-understanding", "audio-events", "embedding", "director", "critic" };
    var missingWorkers = requiredWorkers.Where(name =>
        !File.Exists(Path.Combine(configured.WorkersRoot, name, "worker-manifest.json"))).ToArray();
    var model = await gate.CheckAsync(
        configured.PlannerBackendModel, "Director", "anime-episode", true, token);
    var vision = await gate.CheckAsync(
        configured.VisionBackendModel, "VideoUnderstanding", "anime-episode", true, token);
    // The exact anime profile uses Qwen3-VL for candidate classification and
    // the 30B planner for a bounded Director brief. The heavy models are still
    // isolated by the worker gateway and are never resident simultaneously.
    var ready = missingWorkers.Length == 0 && vision.IsAllowed && model.IsAllowed;
    var animeSrModel = configured.AnimeSrModelPath;
    var upscaleReady = File.Exists(animeSrModel) &&
                       File.Exists(Environment.GetEnvironmentVariable("KADR_AI_FFMPEG")) &&
                       File.Exists(Environment.GetEnvironmentVariable("KADR_AI_FFPROBE"));
    return Results.Json(new
    {
        status = ready ? "ready" : "not_ready",
        missingWorkers,
        plannerRequired = true,
        plannerModelError = model.Error,
        visionModelError = vision.Error,
        capabilities = new
        {
            structuredReasoning = model.IsAllowed,
            videoUnderstanding = vision.IsAllowed,
            animeUpscale = upscaleReady
        },
        animeUpscaleError = upscaleReady ? null : "AnimeSR-X model or server-owned FFmpeg tools are unavailable."
    }, statusCode: ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/health", () => Results.Redirect("/health/ready"));

app.MapKadrV2Endpoints();

app.Run();

public partial class Program
{
}
