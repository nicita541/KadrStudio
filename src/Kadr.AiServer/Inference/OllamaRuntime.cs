using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KadrStudio.AiServer.Configuration;

namespace KadrStudio.AiServer.Inference;

public sealed class OllamaRuntime : IAsyncDisposable
{
    private readonly HttpClient _httpClient;
    private readonly AiServerOptions _options;
    private readonly ILogger<OllamaRuntime> _logger;
    private readonly SemaphoreSlim _ensureGate = new(1, 1);
    private readonly object _statusGate = new();
    private Process? _ownedProcess;
    private int _ready;
    private OllamaRuntimeStatus _status = new(
        OllamaRuntimeState.Starting,
        "AI backend has not been checked yet.",
        DateTimeOffset.UtcNow);

    public OllamaRuntime(
        HttpClient httpClient,
        AiServerOptions options,
        ILogger<OllamaRuntime> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    public OllamaRuntimeStatus Status
    {
        get
        {
            lock (_statusGate)
            {
                return _status;
            }
        }
    }

    public async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _ready) == 1)
        {
            return;
        }

        await _ensureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _ready) == 1)
            {
                return;
            }

            SetStatus(OllamaRuntimeState.Starting, "Checking Ollama backend.");

            if (!await IsBackendAvailableAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!_options.ManageOllama)
                {
                    throw new AiBackendException(
                        $"Ollama backend {_options.OllamaEndpoint} is unavailable and KADR_AI_MANAGE_OLLAMA is disabled.");
                }

                if (!_options.CanManageConfiguredBackend())
                {
                    throw new AiBackendException(
                        "Kadr AI Server can automatically start Ollama only when KADR_AI_OLLAMA_ENDPOINT points to local HTTP loopback.");
                }

                StartOwnedOllama();
                await WaitForBackendAsync(cancellationToken).ConfigureAwait(false);
            }

            await EnsureConfiguredModelAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _ready, 1);
            SetStatus(OllamaRuntimeState.Ready, "AI backend and model are ready.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Interlocked.Exchange(ref _ready, 0);
            SetStatus(OllamaRuntimeState.Failed, exception.Message);
            _logger.LogError(exception, "Kadr AI backend initialization failed.");
            throw;
        }
        finally
        {
            _ensureGate.Release();
        }
    }

    public async Task<JsonObject> ChatAsync(JsonObject publicRequest, CancellationToken cancellationToken)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        var backendRequest = OllamaRequestRewriter.RewriteChatRequest(
            publicRequest,
            _options.BackendModel);

        try
        {
            var backendResponse = await PostObjectAsync(
                "api/chat",
                backendRequest,
                cancellationToken).ConfigureAwait(false);
            return OllamaRequestRewriter.MaskChatResponse(
                backendResponse,
                _options.PublicModelAlias);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Interlocked.Exchange(ref _ready, 0);
            SetStatus(OllamaRuntimeState.Failed, exception.Message);
            throw;
        }
    }

    public async Task<JsonObject> GetPublicTagsAsync(CancellationToken cancellationToken)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow.ToString("O");
        return new JsonObject
        {
            ["models"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = _options.PublicModelAlias,
                    ["model"] = _options.PublicModelAlias,
                    ["modified_at"] = now,
                    ["size"] = 0,
                    ["digest"] = "kadr-managed-model"
                }
            }
        };
    }

    public async Task<JsonObject> GetPublicModelInfoAsync(CancellationToken cancellationToken)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        var backendInfo = await PostObjectAsync(
            "api/show",
            new JsonObject { ["model"] = _options.BackendModel },
            cancellationToken).ConfigureAwait(false);

        var capabilities = backendInfo["capabilities"]?.DeepClone() ??
                           new JsonArray(
                               JsonValue.Create("completion"),
                               JsonValue.Create("vision"));

        return new JsonObject
        {
            ["model"] = _options.PublicModelAlias,
            ["details"] = new JsonObject
            {
                ["family"] = "kadr-managed",
                ["parameter_size"] = "server-managed",
                ["quantization_level"] = "server-managed"
            },
            ["capabilities"] = capabilities
        };
    }

    public async Task PullPublicModelAsync(CancellationToken cancellationToken)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureConfiguredModelAsync(CancellationToken cancellationToken)
    {
        var tags = await GetObjectAsync("api/tags", cancellationToken).ConfigureAwait(false);
        var installed = tags["models"] is JsonArray models &&
                        models.OfType<JsonObject>().Any(model =>
                            string.Equals(
                                model["name"]?.GetValue<string>(),
                                _options.BackendModel,
                                StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(
                                model["model"]?.GetValue<string>(),
                                _options.BackendModel,
                                StringComparison.OrdinalIgnoreCase));

        if (!installed)
        {
            if (!_options.AutoPull)
            {
                throw new AiBackendException(
                    $"Configured model '{_options.BackendModel}' is not installed and KADR_AI_AUTO_PULL is disabled.");
            }

            SetStatus(
                OllamaRuntimeState.Starting,
                $"Downloading configured model '{_options.BackendModel}'. First launch can take a long time.");
            _logger.LogInformation("Pulling configured AI model {Model}.", _options.BackendModel);
            await PostObjectAsync(
                "api/pull",
                new JsonObject
                {
                    ["model"] = _options.BackendModel,
                    ["stream"] = false
                },
                cancellationToken).ConfigureAwait(false);
        }

        var show = await PostObjectAsync(
            "api/show",
            new JsonObject { ["model"] = _options.BackendModel },
            cancellationToken).ConfigureAwait(false);

        if (show["capabilities"] is JsonArray capabilities &&
            !capabilities.Any(item =>
                string.Equals(item?.GetValue<string>(), "vision", StringComparison.OrdinalIgnoreCase)))
        {
            throw new AiBackendException(
                $"Configured model '{_options.BackendModel}' does not report vision capability. Kadr Studio requires a vision model.");
        }
    }

    private async Task<bool> IsBackendAvailableAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1.5));
        try
        {
            using var response = await _httpClient.GetAsync("api/version", timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    private void StartOwnedOllama()
    {
        if (_ownedProcess is { HasExited: false })
        {
            return;
        }

        Directory.CreateDirectory(_options.ModelsRoot);
        var executable = FindOllamaExecutable();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("serve");
        startInfo.Environment["OLLAMA_HOST"] = _options.OllamaEndpoint.Authority;
        startInfo.Environment["OLLAMA_MODELS"] = _options.ModelsRoot;

        _logger.LogInformation(
            "Starting Ollama from {Executable}; models root: {ModelsRoot}; endpoint: {Endpoint}",
            executable,
            _options.ModelsRoot,
            _options.OllamaEndpoint);

        _ownedProcess = Process.Start(startInfo)
                        ?? throw new AiBackendException("Failed to start Ollama process.");
    }

    private async Task WaitForBackendAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + _options.StartupTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_ownedProcess is { HasExited: true })
            {
                throw new AiBackendException(
                    $"Ollama exited during startup with code {_ownedProcess.ExitCode}.");
            }

            if (await IsBackendAvailableAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        throw new AiBackendException(
            $"Ollama did not become ready within {_options.StartupTimeout.TotalSeconds:0} seconds.");
    }

    private string FindOllamaExecutable()
    {
        var fileName = OperatingSystem.IsWindows() ? "ollama.exe" : "ollama";
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(_options.OllamaExecutable))
        {
            candidates.Add(Environment.ExpandEnvironmentVariables(_options.OllamaExecutable));
        }

        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                candidates.Add(Path.Combine(
                    localAppData,
                    "KadrStudio",
                    "AiServer",
                    "ollama-runtime",
                    "ollama.exe"));
                candidates.Add(Path.Combine(localAppData, "Programs", "Ollama", "ollama.exe"));
            }
        }

        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory, fileName)));

        foreach (var candidate in candidates.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            try
            {
                var fullPath = Path.GetFullPath(candidate);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                _logger.LogDebug(exception, "Ignoring invalid Ollama executable candidate {Candidate}.", candidate);
            }
        }

        throw new FileNotFoundException(
            "Ollama executable was not found. Install Ollama or set KADR_AI_OLLAMA_EXE. " +
            "Kadr AI Server never stores Ollama inside the Kadr Studio project directory.");
    }

    private async Task<JsonObject> GetObjectAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(relativeUrl, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, body, relativeUrl);
        return ParseObject(body, relativeUrl);
    }

    private async Task<JsonObject> PostObjectAsync(
        string relativeUrl,
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        using var content = new StringContent(
            payload.ToJsonString(),
            Encoding.UTF8,
            "application/json");
        using var response = await _httpClient.PostAsync(relativeUrl, content, cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, body, relativeUrl);
        return ParseObject(body, relativeUrl);
    }

    private static JsonObject ParseObject(string body, string operation)
    {
        try
        {
            return JsonNode.Parse(body) as JsonObject
                   ?? throw new AiBackendException(
                       $"Ollama {operation} returned JSON that is not an object.");
        }
        catch (JsonException exception)
        {
            throw new AiBackendException(
                $"Ollama {operation} returned invalid JSON.",
                exception);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response, string body, string operation)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var details = body.Length <= 1200 ? body : body[..1200] + "…";
        throw new AiBackendException(
            $"Ollama {operation} failed with HTTP {(int)response.StatusCode} ({response.StatusCode}). {details}");
    }

    private void SetStatus(OllamaRuntimeState state, string message)
    {
        lock (_statusGate)
        {
            _status = new OllamaRuntimeStatus(state, message, DateTimeOffset.UtcNow);
        }
    }

    public ValueTask DisposeAsync()
    {
        _ensureGate.Dispose();
        if (_ownedProcess is null)
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            if (!_ownedProcess.HasExited)
            {
                _ownedProcess.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process has already exited.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Best effort during server shutdown.
        }
        finally
        {
            _ownedProcess.Dispose();
            _ownedProcess = null;
        }

        return ValueTask.CompletedTask;
    }
}
