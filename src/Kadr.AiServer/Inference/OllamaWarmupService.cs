namespace KadrStudio.AiServer.Inference;

public sealed class OllamaWarmupService : BackgroundService
{
    private readonly OllamaRuntime _runtime;
    private readonly ILogger<OllamaWarmupService> _logger;

    public OllamaWarmupService(
        OllamaRuntime runtime,
        ILogger<OllamaWarmupService> logger)
    {
        _runtime = runtime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _runtime.EnsureReadyAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            // The web server stays alive so /health/ready can expose the failure and
            // a later request can retry initialization after the operator fixes Ollama.
            _logger.LogError(exception, "Initial AI backend warmup failed.");
        }
    }
}
