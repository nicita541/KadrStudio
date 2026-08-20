namespace KadrStudio.AiServer.Inference;

public sealed class OllamaWarmupService : BackgroundService
{
    private readonly OllamaRuntime _runtime;
    private readonly ILogger<OllamaWarmupService> _logger;
    private readonly IHostApplicationLifetime _lifetime;

    public OllamaWarmupService(
        OllamaRuntime runtime,
        ILogger<OllamaWarmupService> logger,
        IHostApplicationLifetime lifetime)
    {
        _runtime = runtime;
        _logger = logger;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (!_lifetime.ApplicationStarted.IsCancellationRequested)
            {
                using var startOrStop = CancellationTokenSource.CreateLinkedTokenSource(
                    _lifetime.ApplicationStarted,
                    stoppingToken);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, startOrStop.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // ApplicationStarted is a notification token and is cancelled
                    // once Kestrel has successfully bound its endpoints.
                }
            }

            if (stoppingToken.IsCancellationRequested ||
                !_lifetime.ApplicationStarted.IsCancellationRequested)
            {
                return;
            }

            await _runtime.EnsureReadyAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            // Starting or probing Ollama can surface HttpRequestException or an
            // exited-process error instead of OperationCanceledException while
            // the host is shutting down. Those are shutdown noise, not a failed
            // warmup that should be reported after logging has begun disposal.
        }
        catch (Exception exception)
        {
            // The web server stays alive so /health/ready can expose the failure and
            // a later request can retry initialization after the operator fixes Ollama.
            _logger.LogError(exception, "Initial AI backend warmup failed.");
        }
    }
}
