using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using KadrStudio.AiServer.Api;
using KadrStudio.AiServer.Configuration;
using KadrStudio.AiServer.Inference;
using KadrStudio.AiServer.Storage;
using KadrStudio.AiServer.Workers;

namespace KadrStudio.AiServer.Jobs;

public sealed class AnalyzerJobService : BackgroundService
{
    private readonly string _root;
    private readonly ContentAddressedAssetStore _assets;
    private readonly ContentAddressedArtifactStore _artifacts;
    private readonly IWorkerGateway _workers;
    private readonly AiServerOptions _options;
    private readonly ModelCapabilityGate _modelGate;
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    private readonly ConcurrentDictionary<Guid, AnalyzerJobDocument> _jobs = new();
    private readonly ConcurrentDictionary<string, Guid> _requestIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancellations = new();
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public AnalyzerJobService(
        AiServerOptions options,
        ContentAddressedAssetStore assets,
        ContentAddressedArtifactStore artifacts,
        IWorkerGateway workers,
        ModelCapabilityGate modelGate)
    {
        _root = Path.GetFullPath(Path.Combine(options.DataRoot, "jobs"));
        Directory.CreateDirectory(_root);
        _assets = assets;
        _artifacts = artifacts;
        _workers = workers;
        _options = options;
        _modelGate = modelGate;
    }

    public async Task<AnalyzerJobResponse> CreateAsync(
        AnalyzerJobRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!WorkerAnalyzers.Allowed.Contains(request.Analyzer))
            throw new AnalyzerJobException("analyzer_invalid", $"Analyzer '{request.Analyzer}' is not allowed.");
        if (string.IsNullOrWhiteSpace(request.AnalyzerVersion) || request.AnalyzerVersion.Length > 100)
            throw new AnalyzerJobException("analyzer_version_invalid", "Analyzer version is required.");
        if (request.AssetIds is null || request.AssetIds.Length > 128 ||
            request.AssetIds.Length == 0 && !request.Analyzer.Equals("embedding", StringComparison.OrdinalIgnoreCase))
            throw new AnalyzerJobException(
                "assets_invalid",
                "A media analyzer needs between 1 and 128 analysis assets; embedding may use a text corpus without media assets.");
        if (request.Parameters.ValueKind != JsonValueKind.Object)
            throw new AnalyzerJobException("parameters_invalid", "Job parameters must be a JSON object.");
        foreach (var assetId in request.AssetIds.Distinct(StringComparer.Ordinal))
        {
            try
            {
                if (_assets.Find(assetId) is null)
                    throw new AnalyzerJobException("asset_not_found", $"Analysis asset '{assetId}' is incomplete or missing.");
            }
            catch (AssetUploadException exception)
            {
                throw new AnalyzerJobException("asset_invalid", exception.Message);
            }
        }
        var runtimeFingerprint = await EnsureAnalyzerModelAsync(
            request.Analyzer, request.AnalyzerVersion, request.Parameters,
            request.RequireProduction, cancellationToken).ConfigureAwait(false);

        var requestFingerprint = RequestFingerprint(request, runtimeFingerprint);
        if (_requestIds.TryGetValue(requestFingerprint, out var reusableId) &&
            _jobs.TryGetValue(reusableId, out var reusable) &&
            reusable.State is AnalyzerJobState.Queued or AnalyzerJobState.Running or AnalyzerJobState.Succeeded)
            return ToResponse(reusable);
        _requestIds.TryRemove(requestFingerprint, out _);

        var now = DateTimeOffset.UtcNow;
        var document = new AnalyzerJobDocument(
            Guid.NewGuid(), request.Analyzer.Trim(), request.AnalyzerVersion.Trim(),
            request.AssetIds.Distinct(StringComparer.Ordinal).ToArray(),
            request.Parameters.Clone(), AnalyzerJobState.Queued, 0,
            "queued", [], null, null, now, now, requestFingerprint);
        document = document with
        {
            RequireProduction = request.RequireProduction,
            RuntimeFingerprint = runtimeFingerprint
        };
        _jobs[document.Id] = document;
        if (!_requestIds.TryAdd(requestFingerprint, document.Id) &&
            _requestIds.TryGetValue(requestFingerprint, out reusableId) &&
            _jobs.TryGetValue(reusableId, out reusable))
        {
            _jobs.TryRemove(document.Id, out _);
            return ToResponse(reusable);
        }
        await SaveAsync(document, cancellationToken).ConfigureAwait(false);
        await _queue.Writer.WriteAsync(document.Id, cancellationToken).ConfigureAwait(false);
        return ToResponse(document);
    }

    public AnalyzerJobResponse? Find(Guid id)
        => _jobs.TryGetValue(id, out var document) ? ToResponse(document) : null;

    public async Task<AnalyzerJobResponse?> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!_jobs.TryGetValue(id, out var document)) return null;
        if (IsTerminal(document.State)) return ToResponse(document);
        if (_cancellations.TryGetValue(id, out var source)) source.Cancel();
        document = document with
        {
            State = AnalyzerJobState.Cancelled,
            Message = "cancelled",
            UpdatedAt = DateTimeOffset.UtcNow,
            ErrorCode = "cancelled"
        };
        _jobs[id] = document;
        RemoveRequestIndex(document);
        await SaveAsync(document, cancellationToken).ConfigureAwait(false);
        return ToResponse(document);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken).ConfigureAwait(false);
        var workerCount = Math.Clamp(Environment.ProcessorCount / 4, 2, 8);
        await Task.WhenAll(Enumerable.Range(0, workerCount)
            .Select(_ => ProcessQueueAsync(stoppingToken))).ConfigureAwait(false);
    }

    private async Task ProcessQueueAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            if (!_jobs.TryGetValue(id, out var document) || IsTerminal(document.State)) continue;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _cancellations[id] = linked;
            try
            {
                document = document with
                {
                    State = AnalyzerJobState.Running,
                    Progress = 0,
                    Message = "worker starting",
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                _jobs[id] = document;
                await SaveAsync(document, stoppingToken).ConfigureAwait(false);
                var currentRuntimeFingerprint = await EnsureAnalyzerModelAsync(
                    document.Analyzer, document.AnalyzerVersion, document.Parameters,
                    document.RequireProduction, linked.Token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(document.RuntimeFingerprint) &&
                    !document.RuntimeFingerprint.Equals(currentRuntimeFingerprint, StringComparison.Ordinal))
                    throw new AnalyzerJobException(
                        "worker_runtime_changed",
                        "Analyzer model/eval manifest changed after the job was queued; submit a fresh job.");
                var workerJob = new WorkerJob(
                    id, document.Analyzer, document.AnalyzerVersion,
                    document.AssetIds.Select(assetId => _assets.Find(assetId)!).ToArray(),
                    document.Parameters);
                var progress = new Progress<double>(value => UpdateProgress(id, value));
                var result = await ExecuteWorkerWithRetriesAsync(
                    workerJob, progress, linked.Token).ConfigureAwait(false);
                var artifactIds = result.ArtifactIds
                    .Where(id => _artifacts.Find(id) is not null)
                    .ToList();
                if (result.ResultJson.Length > 0)
                    artifactIds.Add(await _artifacts.PutJsonAsync(result.ResultJson, stoppingToken).ConfigureAwait(false));
                document = document with
                {
                    State = AnalyzerJobState.Succeeded,
                    Progress = 1,
                    Message = "completed",
                    ArtifactIds = artifactIds.Distinct(StringComparer.Ordinal).ToArray(),
                    UpdatedAt = DateTimeOffset.UtcNow
                };
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                document = document with
                {
                    State = AnalyzerJobState.Cancelled,
                    Message = "cancelled",
                    ErrorCode = "cancelled",
                    UpdatedAt = DateTimeOffset.UtcNow
                };
            }
            catch (Exception exception)
            {
                document = document with
                {
                    State = AnalyzerJobState.Failed,
                    Message = "failed",
                    ErrorCode = exception is AnalyzerJobException jobException
                        ? jobException.ErrorCode
                        : exception is WorkerUnavailableException ? "worker_unavailable" : "worker_failed",
                    Error = exception.Message,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
            }
            finally
            {
                _cancellations.TryRemove(id, out _);
                _jobs[id] = document;
                if (document.State is AnalyzerJobState.Failed or AnalyzerJobState.Cancelled)
                    RemoveRequestIndex(document);
                try { await SaveAsync(document, CancellationToken.None).ConfigureAwait(false); } catch { }
            }
        }
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        foreach (var path in Directory.EnumerateFiles(_root, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var document = JsonSerializer.Deserialize<AnalyzerJobDocument>(
                    await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), _json);
                if (document is null) continue;
                if (document.State is AnalyzerJobState.Running or AnalyzerJobState.Queued)
                    document = document with
                    {
                        State = AnalyzerJobState.Queued,
                        Progress = 0,
                        Message = "recovered after restart",
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                if (string.IsNullOrWhiteSpace(document.RequestFingerprint))
                    document = document with
                    {
                        RequestFingerprint = RequestFingerprint(new AnalyzerJobRequest(
                            document.Analyzer,
                            document.AnalyzerVersion,
                            document.AssetIds,
                            document.Parameters,
                            document.RequireProduction),
                            string.IsNullOrWhiteSpace(document.RuntimeFingerprint)
                                ? "legacy-runtime"
                                : document.RuntimeFingerprint)
                    };
                _jobs[document.Id] = document;
                if (document.State is AnalyzerJobState.Queued or AnalyzerJobState.Running or AnalyzerJobState.Succeeded)
                    _requestIds.TryAdd(document.RequestFingerprint, document.Id);
                if (document.State == AnalyzerJobState.Queued)
                    await _queue.Writer.WriteAsync(document.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                // A corrupt job record is isolated; other resumable jobs remain available.
            }
        }
    }

    private void UpdateProgress(Guid id, double progress)
    {
        if (!_jobs.TryGetValue(id, out var document) || document.State != AnalyzerJobState.Running) return;
        _jobs[id] = document with
        {
            Progress = Math.Clamp(progress, 0, 0.99),
            Message = "running",
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private async Task<WorkerJobResult> ExecuteWorkerWithRetriesAsync(
        WorkerJob job,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await _workers.ExecuteAsync(job, progress, cancellationToken).ConfigureAwait(false);
                if (!result.IsSuccess)
                    throw new AnalyzerJobException(
                        string.IsNullOrWhiteSpace(result.ErrorCode) ? "worker_failed" : result.ErrorCode,
                        string.IsNullOrWhiteSpace(result.Error) ? "Analyzer worker failed." : result.Error);
                if (result.ResultJson.Length > 0)
                {
                    try
                    {
                        using var _ = JsonDocument.Parse(result.ResultJson);
                    }
                    catch (JsonException exception)
                    {
                        throw new AnalyzerJobException(
                            "worker_invalid_json",
                            "Analyzer worker returned damaged JSON: " + exception.Message);
                    }
                }
                return result;
            }
            catch (Exception exception) when (
                attempt < 3 && exception is WorkerUnavailableException or AnalyzerJobException)
            {
                last = exception;
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
        throw last ?? new AnalyzerJobException("worker_failed", "Analyzer worker failed after retries.");
    }

    private async Task SaveAsync(AnalyzerJobDocument document, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, document.Id.ToString("N") + ".json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(document, _json), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static AnalyzerJobResponse ToResponse(AnalyzerJobDocument document)
        => new(
            document.Id, document.Analyzer, document.AnalyzerVersion,
            document.State, document.Progress, document.Message,
            document.ArtifactIds, document.ErrorCode, document.Error,
            document.CreatedAt, document.UpdatedAt);

    private static bool IsTerminal(AnalyzerJobState state)
        => state is AnalyzerJobState.Succeeded or AnalyzerJobState.Failed or AnalyzerJobState.Cancelled;

    private void RemoveRequestIndex(AnalyzerJobDocument document)
    {
        if (!string.IsNullOrWhiteSpace(document.RequestFingerprint) &&
            _requestIds.TryGetValue(document.RequestFingerprint, out var indexedId) &&
            indexedId == document.Id)
            _requestIds.TryRemove(document.RequestFingerprint, out _);
    }

    private async Task<string> EnsureAnalyzerModelAsync(
        string analyzer,
        string analyzerVersion,
        JsonElement parameters,
        bool requireProduction,
        CancellationToken cancellationToken)
    {
        if (!analyzer.Equals("video-understanding", StringComparison.OrdinalIgnoreCase))
            return $"{analyzer.Trim().ToLowerInvariant()}:{analyzerVersion.Trim()}";
        var profile = parameters.TryGetProperty("profile", out var profileElement) &&
                      profileElement.ValueKind == JsonValueKind.String &&
                      !string.IsNullOrWhiteSpace(profileElement.GetString())
            ? profileElement.GetString()!.Trim()
            : "generic";
        var gate = await _modelGate.CheckAsync(
            _options.VisionBackendModel,
            "VideoUnderstanding",
            profile,
            requireProduction,
            cancellationToken).ConfigureAwait(false);
        if (!gate.IsAllowed)
            throw new AnalyzerJobException("model_not_qualified", gate.Error);
        var manifestIdentity = gate.Manifest is null
            ? "development"
            : $"{gate.Manifest.ModelHash}|{gate.Manifest.MontageEvalRevision}";
        return $"{_options.VisionBackendModel}|{manifestIdentity}";
    }

    private static string RequestFingerprint(AnalyzerJobRequest request, string runtimeFingerprint)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            analyzer = request.Analyzer.Trim().ToLowerInvariant(),
            analyzerVersion = request.AnalyzerVersion.Trim(),
            assetIds = request.AssetIds,
            parameters = request.Parameters,
            request.RequireProduction,
            runtimeFingerprint
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private sealed record AnalyzerJobDocument(
        Guid Id,
        string Analyzer,
        string AnalyzerVersion,
        string[] AssetIds,
        JsonElement Parameters,
        AnalyzerJobState State,
        double Progress,
        string Message,
        string[] ArtifactIds,
        string? ErrorCode,
        string? Error,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        string RequestFingerprint = "",
        bool RequireProduction = true,
        string RuntimeFingerprint = "");
}

public sealed class AnalyzerJobException(string errorCode, string message) : InvalidOperationException(message)
{
    public string ErrorCode { get; } = errorCode;
}
