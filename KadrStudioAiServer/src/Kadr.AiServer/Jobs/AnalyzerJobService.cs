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
    private const string PipelineVersion = "kadr-analysis-pipeline-v2";
    private readonly string _root;
    private readonly ContentAddressedAssetStore _assets;
    private readonly ContentAddressedArtifactStore _artifacts;
    private readonly IWorkerGateway _workers;
    private readonly AiServerOptions _options;
    private readonly ModelCapabilityGate _modelGate;
    private readonly DataRootQuota _quota;
    private readonly Channel<Guid> _queue;
    private readonly ConcurrentDictionary<Guid, AnalyzerJobDocument> _jobs = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _stateGates = new();
    private readonly ConcurrentDictionary<string, Guid> _requestIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancellations = new();
    private readonly ConcurrentDictionary<Guid, AnalyzerJobDocument> _pendingCommits = new();
    private readonly ConcurrentDictionary<Guid, byte> _pendingStarts = new();
    private readonly ConcurrentDictionary<string, byte> _pendingRecoveryReads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, string> _storageFailures = new();
    private readonly SemaphoreSlim _creationGate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private int _queuedCount;
    private volatile bool _pendingRecoveryScan;
    private volatile bool _recoveryCompleted;
    private long _cleanupCount;

    public AnalyzerJobService(
        AiServerOptions options,
        ContentAddressedAssetStore assets,
        ContentAddressedArtifactStore artifacts,
        IWorkerGateway workers,
        ModelCapabilityGate modelGate,
        DataRootQuota? quota = null)
    {
        _root = Path.GetFullPath(Path.Combine(options.DataRoot, "jobs"));
        Directory.CreateDirectory(_root);
        _assets = assets;
        _artifacts = artifacts;
        _workers = workers;
        _options = options;
        _modelGate = modelGate;
        _quota = quota ?? new DataRootQuota(options.DataRoot, options.MaxDataRootBytes);
        _queue = Channel.CreateBounded<Guid>(new BoundedChannelOptions(options.MaxQueuedJobs)
        {
            SingleReader = options.MaxActiveJobs == 1,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public async Task<AnalyzerJobResponse> CreateAsync(
        AnalyzerJobRequest request,
        CancellationToken cancellationToken)
    {
        await _creationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await CreateCoreAsync(request, cancellationToken).ConfigureAwait(false); }
        finally { _creationGate.Release(); }
    }

    private async Task<AnalyzerJobResponse> CreateCoreAsync(
        AnalyzerJobRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if ((request.OwnerId is null) != (request.RequestId is null) ||
            request.OwnerId is not null && (string.IsNullOrWhiteSpace(request.OwnerId) || request.OwnerId.Length > 128 ||
                string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 128))
            throw new AnalyzerJobException("job_identity_invalid", "OwnerId and RequestId must both be nonempty and at most 128 characters.");
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
        if (request.Analyzer.Equals("anime-upscale", StringComparison.OrdinalIgnoreCase) && request.AssetIds.Length != 1)
            throw new AnalyzerJobException("assets_invalid", "AnimeSR-X jobs require exactly one bounded video asset.");
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
        if (request.RequestId is not null)
        {
            var prior = _jobs.Values.FirstOrDefault(job =>
                job.OwnerId == request.OwnerId && job.RequestIds?.Contains(request.RequestId, StringComparer.Ordinal) == true ||
                job.SharedRequestBindings?.Any(binding => binding.OwnerId == request.OwnerId && binding.RequestId == request.RequestId) == true);
            if (prior is not null)
            {
                if (prior.RequestFingerprint != requestFingerprint)
                    throw new AnalyzerJobException("request_identity_conflict", "RequestId already identifies different inputs.");
                return ToResponse(prior);
            }
        }
        var completed = _jobs.Values.FirstOrDefault(job => job.State == AnalyzerJobState.Succeeded &&
            job.RequestFingerprint == requestFingerprint);
        var requestKey = ActiveRequestKey(requestFingerprint, request.OwnerId);
        AnalyzerJobDocument? reusable = completed;
        if (reusable is null && _requestIds.TryGetValue(requestKey, out var indexedId))
            _jobs.TryGetValue(indexedId, out reusable);
        if (reusable is not null)
        {
            var gate = _stateGates.GetOrAdd(reusable.Id, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Cancellation or worker completion can change state while creation waits.
                reusable = _jobs[reusable.Id];
                if (reusable.State is AnalyzerJobState.Queued or AnalyzerJobState.Running or AnalyzerJobState.Succeeded)
                {
                    if (request.RequestId is not null)
                    {
                        if ((reusable.RequestIds?.Length ?? 0) + (reusable.SharedRequestBindings?.Length ?? 0) >= 128)
                            throw new AnalyzerJobException("job_request_limit", "This job already has the maximum number of request aliases.");
                        reusable = reusable.OwnerId == request.OwnerId
                            ? reusable with { RequestIds = [.. reusable.RequestIds ?? [], request.RequestId], Version = reusable.Version + 1 }
                            : reusable with { SharedRequestBindings = [.. reusable.SharedRequestBindings ?? [], new(request.OwnerId!, request.RequestId)], Version = reusable.Version + 1 };
                        await SaveAsync(reusable, cancellationToken).ConfigureAwait(false);
                        _jobs[reusable.Id] = reusable;
                    }
                    return ToResponse(reusable);
                }
            }
            finally { gate.Release(); }
        }
        _requestIds.TryRemove(requestKey, out _);

        if (!TryReserveQueueSlot())
            throw new AnalyzerJobException("queue_full", "Analyzer job queue is full; retry later.");

        var now = DateTimeOffset.UtcNow;
        var document = new AnalyzerJobDocument(
            Guid.NewGuid(), request.Analyzer.Trim(), request.AnalyzerVersion.Trim(),
            request.AssetIds.Distinct(StringComparer.Ordinal).ToArray(),
            request.Parameters.Clone(), AnalyzerJobState.Queued, 0,
            "queued", [], null, null, now, now, requestFingerprint);
        document = document with
        {
            RequireProduction = request.RequireProduction,
            RuntimeFingerprint = runtimeFingerprint,
            OwnerId = request.OwnerId,
            RequestIds = request.RequestId is null ? [] : [request.RequestId]
        };
        var enqueued = false;
        try
        {
            _stateGates.TryAdd(document.Id, new SemaphoreSlim(1, 1));
            if (!_requestIds.TryAdd(requestKey, document.Id) &&
                _requestIds.TryGetValue(requestKey, out var reusableId) &&
                _jobs.TryGetValue(reusableId, out reusable))
            {
                _jobs.TryRemove(document.Id, out _);
                return ToResponse(reusable);
            }
            await SaveAsync(document, cancellationToken).ConfigureAwait(false);
            _jobs[document.Id] = document;
            if (!_queue.Writer.TryWrite(document.Id))
                throw new AnalyzerJobException("queue_full", "Analyzer job queue is full; retry later.");
            enqueued = true;
            return ToResponse(document);
        }
        finally
        {
            if (!enqueued)
            {
                Interlocked.Decrement(ref _queuedCount);
                _jobs.TryRemove(document.Id, out _);
                RemoveRequestIndex(document);
                _stateGates.TryRemove(document.Id, out var abandonedGate);
                abandonedGate?.Dispose();
                try { File.Delete(Path.Combine(_root, document.Id.ToString("N") + ".json")); } catch { }
            }
        }
    }

    public AnalyzerJobResponse? Find(Guid id)
        => _jobs.TryGetValue(id, out var document)
            ? ToResponse(document) with { StorageError = _storageFailures.GetValueOrDefault(id) }
            : null;

    public JobStorageHealth GetStorageHealth()
        => new(_recoveryCompleted, _pendingRecoveryScan, _pendingRecoveryReads.Count,
            _pendingCommits.Count, _storageFailures.Count);

    public ServerResourceMetrics GetMetrics()
        => new(
            Math.Max(0, Volatile.Read(ref _queuedCount)),
            _jobs.Values.Count(document => document.State == AnalyzerJobState.Running),
            _jobs.Count,
            _quota.GetStorageBytes(),
            _quota.GetPartialBytes(),
            Interlocked.Read(ref _cleanupCount));

    public async Task<AnalyzerJobResponse?> CancelAsync(Guid id, CancellationToken cancellationToken, string? ownerId = null)
    {
        if (!_jobs.ContainsKey(id)) return null;
        var gate = _stateGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_jobs.TryGetValue(id, out var document)) return null;
            if (IsTerminal(document.State)) return ToResponse(document);
            if (!string.Equals(document.OwnerId, ownerId, StringComparison.Ordinal))
                throw new AnalyzerJobException("job_owner_mismatch", "Active job belongs to a different task owner.");
            if (_cancellations.TryGetValue(id, out var source)) source.Cancel();
            document = document with
            {
                State = AnalyzerJobState.Cancelled,
                Message = "cancelled",
                UpdatedAt = DateTimeOffset.UtcNow,
                ErrorCode = "cancelled",
                Version = document.Version + 1
            };
            try
            {
                await SaveAsync(document, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Keep the last committed public state, but prevent execution while cancellation
                // awaits storage. The caller still receives the failed commit, not a false success.
                _pendingCommits[id] = document;
                _pendingStarts.TryRemove(id, out _);
                _storageFailures[id] = exception.Message;
                throw;
            }
            _jobs[id] = document;
            _pendingCommits.TryRemove(id, out _);
            _pendingStarts.TryRemove(id, out _);
            _storageFailures.TryRemove(id, out _);
            RemoveRequestIndex(document);
            return ToResponse(document);
        }
        finally
        {
            gate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken).ConfigureAwait(false);
        await RunCleanupAsync(stoppingToken).ConfigureAwait(false);
        var workers = Enumerable.Range(0, _options.MaxActiveJobs)
            .Select(_ => ProcessQueueAsync(stoppingToken));
        await Task.WhenAll(workers.Append(CleanupLoopAsync(stoppingToken))
            .Append(RetryStorageAsync(stoppingToken))).ConfigureAwait(false);
    }

    private async Task ProcessQueueAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            Interlocked.Decrement(ref _queuedCount);
            if (!_jobs.ContainsKey(id)) continue;
            var stateGate = _stateGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            AnalyzerJobDocument document;
            await stateGate.WaitAsync(stoppingToken).ConfigureAwait(false);
            try
            {
                if (!_jobs.TryGetValue(id, out var queued) || queued.State != AnalyzerJobState.Queued ||
                    _pendingCommits.ContainsKey(id))
                    continue;
                _cancellations[id] = linked;
                document = queued with
                {
                    State = AnalyzerJobState.Running,
                    Progress = 0,
                    Message = "worker starting",
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Version = queued.Version + 1
                };
                await SaveAsync(document, CancellationToken.None).ConfigureAwait(false);
                _jobs[id] = document;
                _storageFailures.TryRemove(id, out _);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _cancellations.TryRemove(id, out _);
                _storageFailures[id] = exception.Message;
                _pendingStarts[id] = 0;
                continue;
            }
            finally
            {
                stateGate.Release();
            }
            try
            {
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
                result = await ImportWorkerArtifactsAsync(document, result, linked.Token).ConfigureAwait(false);
                var artifactIds = result.ArtifactIds
                    .Where(id => _artifacts.Find(id) is not null)
                    .ToList();
                if (result.ResultJson.Length > 0)
                    artifactIds.Add(await _artifacts.PutJsonAsync(result.ResultJson, linked.Token).ConfigureAwait(false));
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
                await stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (_jobs.TryGetValue(id, out var current) && current.State == AnalyzerJobState.Running)
                    {
                        // A failed explicit cancellation commit takes precedence over a late
                        // worker result, including workers that finish despite cancellation.
                        if (_pendingCommits.TryGetValue(id, out var pending) &&
                            pending.State == AnalyzerJobState.Cancelled)
                            document = pending;
                        document = document with { Version = current.Version + 1, RequestIds = current.RequestIds };
                        try
                        {
                            await SaveAsync(document, CancellationToken.None).ConfigureAwait(false);
                            _jobs[id] = document;
                            _pendingCommits.TryRemove(id, out _);
                            _storageFailures.TryRemove(id, out _);
                            if (document.State is AnalyzerJobState.Failed or AnalyzerJobState.Cancelled)
                                RemoveRequestIndex(document);
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            _pendingCommits[id] = document;
                            _storageFailures[id] = exception.Message;
                        }
                    }
                    else if (current is not null)
                    {
                        document = current;
                    }
                }
                finally
                {
                    _cancellations.TryRemove(id, out _);
                    stateGate.Release();
                }
            }
        }
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string[] paths;
        try
        {
            // Materialize inside the guard: enumeration can fail on MoveNext as well as creation.
            paths = Directory.GetFiles(_root, "*.json", SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _pendingRecoveryScan = true;
            return;
        }
        _pendingRecoveryScan = false;
        foreach (var path in paths)
            await RecoverRecordAsync(path, cancellationToken).ConfigureAwait(false);
        _recoveryCompleted = true;
    }

    private async Task RecoverRecordAsync(string path, CancellationToken cancellationToken)
    {
        await _creationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                var document = JsonSerializer.Deserialize<AnalyzerJobDocument>(
                    await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), _json);
                _pendingRecoveryReads.TryRemove(path, out _);
                if (document is null || !IsValidRecoveredDocument(document, path))
                {
                    System.Diagnostics.Trace.TraceWarning("Ignoring structurally invalid analyzer job record: {0}", Path.GetFileName(path));
                    return;
                }
                if (_jobs.ContainsKey(document.Id)) return;
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
                _stateGates.TryAdd(document.Id, new SemaphoreSlim(1, 1));
                if (document.State is AnalyzerJobState.Queued or AnalyzerJobState.Running or AnalyzerJobState.Succeeded)
                    _requestIds.TryAdd(ActiveRequestKey(document.RequestFingerprint, document.OwnerId), document.Id);
                if (document.State is AnalyzerJobState.Queued or AnalyzerJobState.Running)
                {
                    var reserved = TryReserveQueueSlot();
                    var recovered = reserved ? document with
                    {
                        State = AnalyzerJobState.Queued,
                        Progress = 0,
                        Message = "recovered after restart",
                        UpdatedAt = DateTimeOffset.UtcNow,
                        Version = document.Version + 1
                    } : document with
                    {
                        State = AnalyzerJobState.Failed,
                        Message = "recovery queue full",
                        ErrorCode = "queue_full",
                        Error = "Recovered job exceeded the configured queue capacity.",
                        UpdatedAt = DateTimeOffset.UtcNow,
                        Version = document.Version + 1
                    };
                    var gate = _stateGates[document.Id];
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (IsTerminal(_jobs[document.Id].State)) return;
                        await SaveAsync(recovered, cancellationToken).ConfigureAwait(false);
                        _jobs[document.Id] = recovered;
                        if (recovered.State == AnalyzerJobState.Queued)
                        {
                            if (_queue.Writer.TryWrite(document.Id)) reserved = false;
                            else _pendingStarts[document.Id] = 0;
                        }
                        else RemoveRequestIndex(recovered);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        _pendingCommits[document.Id] = recovered;
                        _storageFailures[document.Id] = exception.Message;
                    }
                    finally
                    {
                        if (reserved) Interlocked.Decrement(ref _queuedCount);
                        gate.Release();
                    }
                }
            }
            catch (JsonException)
            {
                // A corrupt job record is isolated; other resumable jobs remain available.
                _pendingRecoveryReads.TryRemove(path, out _);
            }
            catch (FileNotFoundException)
            {
                _pendingRecoveryReads.TryRemove(path, out _);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _pendingRecoveryReads[path] = 0;
            }
        }
        finally { _creationGate.Release(); }
    }

    private static bool IsValidRecoveredDocument(AnalyzerJobDocument document, string path)
        => document.Id != Guid.Empty &&
           Path.GetFileNameWithoutExtension(path).Equals(document.Id.ToString("N"), StringComparison.OrdinalIgnoreCase) &&
           !string.IsNullOrWhiteSpace(document.Analyzer) &&
           !string.IsNullOrWhiteSpace(document.AnalyzerVersion) &&
           document.AssetIds is not null && !document.AssetIds.Any(string.IsNullOrWhiteSpace) &&
           document.ArtifactIds is not null && !document.ArtifactIds.Any(string.IsNullOrWhiteSpace) &&
           document.Parameters.ValueKind == JsonValueKind.Object &&
           (document.OwnerId is null || !string.IsNullOrWhiteSpace(document.OwnerId) && document.OwnerId.Length <= 128) &&
           (document.RequestIds is null || document.RequestIds.Length <= 128 &&
               !document.RequestIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128)) &&
           (document.RequestIds?.Length ?? 0) + (document.SharedRequestBindings?.Length ?? 0) <= 128 &&
           (document.SharedRequestBindings is null || !document.SharedRequestBindings.Any(binding =>
               binding is null || string.IsNullOrWhiteSpace(binding.OwnerId) || binding.OwnerId.Length > 128 ||
               string.IsNullOrWhiteSpace(binding.RequestId) || binding.RequestId.Length > 128)) &&
           Enum.IsDefined(document.State) && double.IsFinite(document.Progress) &&
           document.Progress is >= 0 and <= 1 && document.Version >= 0;

    public async Task RunCleanupAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow - _options.JobRetention;
        var terminal = _jobs.Values
            .Where(document => IsTerminal(document.State))
            .OrderByDescending(document => document.UpdatedAt)
            .ToArray();
        var removed = 0;
        for (var index = 0; index < terminal.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = terminal[index];
            if (document.UpdatedAt > cutoff && index < _options.MaxJobHistory) continue;
            // Match recovery's lock order so it cannot resurrect a record being removed.
            await _creationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_stateGates.TryGetValue(document.Id, out var gate)) continue;
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (!_jobs.TryGetValue(document.Id, out var current) || !IsTerminal(current.State) ||
                        current.Version != document.Version) continue;
                    try
                    {
                        File.Delete(Path.Combine(_root, current.Id.ToString("N") + ".json"));
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        _storageFailures[current.Id] = exception.Message;
                        continue;
                    }
                    // Forget history only after durable removal. Failed deletions remain visible
                    // and eligible for the next cleanup pass.
                    _jobs.TryRemove(current.Id, out _);
                    RemoveRequestIndex(current);
                    _storageFailures.TryRemove(current.Id, out _);
                    _pendingCommits.TryRemove(current.Id, out _);
                    _pendingStarts.TryRemove(current.Id, out _);
                    _stateGates.TryRemove(current.Id, out _);
                    // Existing callers may still hold/wait on this managed semaphore. Let it
                    // be collected after they finish instead of disposing it beneath them.
                    removed++;
                }
                finally { gate.Release(); }
            }
            finally { _creationGate.Release(); }
        }
        removed += _assets.CleanupAbandonedUploads(_options.PartialUploadTtl);
        if (removed > 0) Interlocked.Add(ref _cleanupCount, removed);
    }

    private async Task RetryStorageAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            if (_pendingRecoveryScan)
                await RecoverAsync(stoppingToken).ConfigureAwait(false);
            foreach (var path in _pendingRecoveryReads.Keys)
                await RecoverRecordAsync(path, stoppingToken).ConfigureAwait(false);
            foreach (var id in _pendingStarts.Keys)
            {
                if (!_jobs.TryGetValue(id, out var current) || current.State != AnalyzerJobState.Queued ||
                    _pendingCommits.ContainsKey(id))
                {
                    _pendingStarts.TryRemove(id, out _);
                    continue;
                }
                if (!TryReserveQueueSlot()) continue;
                if (!_pendingStarts.TryRemove(id, out _))
                {
                    Interlocked.Decrement(ref _queuedCount);
                    continue;
                }
                if (!_queue.Writer.TryWrite(id))
                {
                    _pendingStarts.TryAdd(id, 0);
                    Interlocked.Decrement(ref _queuedCount);
                }
            }
            foreach (var id in _pendingCommits.Keys)
            {
                var gate = _stateGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(stoppingToken).ConfigureAwait(false);
                try
                {
                    // Re-read under the gate: Cancel may have replaced an earlier pending result.
                    if (!_pendingCommits.TryGetValue(id, out var pending)) continue;
                    if (!_jobs.TryGetValue(id, out var current) || IsTerminal(current.State))
                    {
                        _pendingCommits.TryRemove(id, out _);
                        _storageFailures.TryRemove(id, out _);
                        continue;
                    }
                    var committed = pending with { Version = current.Version + 1, RequestIds = current.RequestIds };
                    await SaveAsync(committed, stoppingToken).ConfigureAwait(false);
                    _jobs[id] = committed;
                    _pendingCommits.TryRemove(id, out _);
                    _storageFailures.TryRemove(id, out _);
                    if (committed.State == AnalyzerJobState.Queued)
                        _pendingStarts[id] = 0;
                    if (committed.State is AnalyzerJobState.Failed or AnalyzerJobState.Cancelled)
                        RemoveRequestIndex(committed);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _storageFailures[id] = exception.Message;
                }
                finally { gate.Release(); }
            }
        }
    }

    private async Task CleanupLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            await RunCleanupAsync(stoppingToken).ConfigureAwait(false);
    }

    private bool TryReserveQueueSlot()
    {
        while (true)
        {
            var current = Volatile.Read(ref _queuedCount);
            if (current >= _options.MaxQueuedJobs) return false;
            if (Interlocked.CompareExchange(ref _queuedCount, current + 1, current) == current) return true;
        }
    }

    private void UpdateProgress(Guid id, double progress)
    {
        while (_jobs.TryGetValue(id, out var document) && document.State == AnalyzerJobState.Running)
        {
            var updated = document with
            {
                Progress = Math.Clamp(progress, 0, 0.99),
                Message = "running",
                UpdatedAt = DateTimeOffset.UtcNow,
                Version = document.Version + 1
            };
            if (_jobs.TryUpdate(id, updated, document)) return;
        }
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
        var payload = JsonSerializer.Serialize(document, _json);
        try
        {
            await _quota.ExecuteWriteAsync(
                Encoding.UTF8.GetByteCount(payload),
                async () =>
                {
                    await File.WriteAllTextAsync(temporary, payload, cancellationToken).ConfigureAwait(false);
                    File.Move(temporary, path, overwrite: true);
                    return true;
                }, cancellationToken).ConfigureAwait(false);
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
            document.CreatedAt, document.UpdatedAt) { OwnerId = document.OwnerId };

    private static bool IsTerminal(AnalyzerJobState state)
        => state is AnalyzerJobState.Succeeded or AnalyzerJobState.Failed or AnalyzerJobState.Cancelled;

    private void RemoveRequestIndex(AnalyzerJobDocument document)
    {
        if (!string.IsNullOrWhiteSpace(document.RequestFingerprint) &&
            _requestIds.TryGetValue(ActiveRequestKey(document.RequestFingerprint, document.OwnerId), out var indexedId) &&
            indexedId == document.Id)
            _requestIds.TryRemove(ActiveRequestKey(document.RequestFingerprint, document.OwnerId), out _);
    }

    private async Task<string> EnsureAnalyzerModelAsync(
        string analyzer,
        string analyzerVersion,
        JsonElement parameters,
        bool requireProduction,
        CancellationToken cancellationToken)
    {
        var normalizedAnalyzer = analyzer.Trim().ToLowerInvariant();
        var implementationIdentity = $"{normalizedAnalyzer}|impl:{analyzerVersion.Trim()}|pipeline:{PipelineVersion}";
        if (analyzer.Equals("anime-upscale", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(_options.AnimeSrModelPath))
                throw new AnalyzerJobException("model_unavailable", "AnimeSR-X production checkpoint is missing.");
            var identity = await _modelGate.ResolveRuntimeIdentityAsync(
                _options.AnimeSrModelPath,
                "AnimeSR-X_first_short_run_best-experimental",
                AiServerOptions.AnimeSrModelSha256,
                cancellationToken).ConfigureAwait(false);
            if (!identity.IsValid)
                throw new AnalyzerJobException("model_invalid", identity.Error);
            return $"{implementationIdentity}|{identity.Identity!.Fingerprint}";
        }
        if (normalizedAnalyzer is "asr-align" or "embedding" or "diarization")
        {
            var (path, revision) = normalizedAnalyzer switch
            {
                "asr-align" => (_options.AsrModelPath, _options.AsrModelRevision),
                "embedding" => (_options.EmbeddingModelPath, _options.EmbeddingModelRevision),
                _ => (_options.DiarizationModelPath, _options.DiarizationModelRevision)
            };
            if (string.IsNullOrWhiteSpace(path))
            {
                if (requireProduction)
                    throw new AnalyzerJobException("model_unavailable", $"{normalizedAnalyzer} model is not configured.");
                return $"{implementationIdentity}|model:development-unconfigured";
            }
            var identity = await _modelGate.ResolveRuntimeIdentityAsync(
                path, revision, null, cancellationToken).ConfigureAwait(false);
            if (!identity.IsValid)
                throw new AnalyzerJobException("model_unavailable", identity.Error);
            return $"{implementationIdentity}|{identity.Identity!.Fingerprint}";
        }
        if (normalizedAnalyzer is not ("video-understanding" or "director" or "critic"))
            return implementationIdentity;
        var profile = parameters.TryGetProperty("profile", out var profileElement) &&
                      profileElement.ValueKind == JsonValueKind.String &&
                      !string.IsNullOrWhiteSpace(profileElement.GetString())
            ? profileElement.GetString()!.Trim()
            : "generic";
        var isVision = normalizedAnalyzer == "video-understanding";
        var configuredModel = isVision ? _options.VisionBackendModel : _options.PlannerBackendModel;
        var role = isVision ? "VideoUnderstanding" : normalizedAnalyzer == "critic" ? "Critic" : "Director";
        var gate = await _modelGate.CheckAsync(
            configuredModel,
            role,
            profile,
            requireProduction,
            cancellationToken).ConfigureAwait(false);
        if (!gate.IsAllowed)
            throw new AnalyzerJobException("model_not_qualified", gate.Error);
        var manifestIdentity = gate.Manifest is null
            ? "development"
            : $"{gate.Manifest.ModelHash}|{gate.Manifest.MontageEvalRevision}|" +
              $"{gate.Manifest.VerifiedIdentity?.Revision ?? "unverified"}";
        return $"{implementationIdentity}|{configuredModel}|{manifestIdentity}|profile:{profile}";
    }

    private async Task<WorkerJobResult> ImportWorkerArtifactsAsync(
        AnalyzerJobDocument document,
        WorkerJobResult result,
        CancellationToken cancellationToken)
    {
        if (!result.IsSuccess || !document.Analyzer.Equals("anime-upscale", StringComparison.OrdinalIgnoreCase))
            return result;
        using var payload = JsonDocument.Parse(result.ResultJson);
        var root = payload.RootElement;
        if (!root.TryGetProperty("outputPath", out var outputElement) ||
            string.IsNullOrWhiteSpace(outputElement.GetString()))
            throw new AnalyzerJobException("worker_invalid_result", "AnimeSR-X worker did not return an output path.");
        var stagingRoot = Path.GetFullPath(Path.Combine(_options.DataRoot, "artifact-staging"));
        var outputPath = Path.GetFullPath(outputElement.GetString()!);
        if (!outputPath.StartsWith(stagingRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(outputPath))
            throw new AnalyzerJobException("worker_invalid_result", "AnimeSR-X output escaped the server staging directory.");
        try
        {
            var binary = await _artifacts.PutFileAsync(
                outputPath, "video/mp4", ".mp4", cancellationToken).ConfigureAwait(false);
            var metadata = JsonSerializer.SerializeToUtf8Bytes(new
            {
                kind = "anime-upscale",
                inputAssetId = document.AssetIds.Single(),
                outputArtifactId = binary.Id,
                modelId = "AnimeSR-X_first_short_run_best-experimental",
                modelSha256 = AiServerOptions.AnimeSrModelSha256,
                durationTicks = root.TryGetProperty("durationTicks", out var duration) ? duration.GetInt64() : 0,
                width = root.TryGetProperty("width", out var width) ? width.GetInt32() : 0,
                height = root.TryGetProperty("height", out var height) ? height.GetInt32() : 0,
                frames = root.TryGetProperty("frames", out var frames) ? frames.GetInt32() : 0,
                fps = root.TryGetProperty("fps", out var fps) ? fps.GetString() : null
            });
            return result with { ResultJson = metadata, ArtifactIds = [binary.Id] };
        }
        finally
        {
            try { File.Delete(outputPath); } catch { }
        }
    }

    private static string ActiveRequestKey(string fingerprint, string? ownerId)
        => fingerprint + ":" + (ownerId ?? "");

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
        string RuntimeFingerprint = "",
        long Version = 0,
        string? OwnerId = null,
        string[]? RequestIds = null,
        SharedRequestBinding[]? SharedRequestBindings = null);

    private sealed record SharedRequestBinding(string OwnerId, string RequestId);
}

public sealed record JobStorageHealth(
    bool RecoveryCompleted,
    bool RecoveryScanPending,
    int PendingRecoveryReads,
    int PendingCommits,
    int JobsWithStorageErrors)
{
    public bool IsHealthy => RecoveryCompleted && !RecoveryScanPending &&
        PendingRecoveryReads == 0 && PendingCommits == 0 && JobsWithStorageErrors == 0;
}

public sealed record ServerResourceMetrics(
    int Queued,
    int Active,
    int History,
    long StorageBytes,
    long PartialBytes,
    long CleanupCount);

public sealed class AnalyzerJobException(string errorCode, string message) : InvalidOperationException(message)
{
    public string ErrorCode { get; } = errorCode;
}
