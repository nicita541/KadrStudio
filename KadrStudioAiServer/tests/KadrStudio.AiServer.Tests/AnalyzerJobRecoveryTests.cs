using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using KadrStudio.AiServer.Api;
using KadrStudio.AiServer.Configuration;
using KadrStudio.AiServer.Inference;
using KadrStudio.AiServer.Jobs;
using KadrStudio.AiServer.Storage;
using KadrStudio.AiServer.Workers;

namespace KadrStudio.AiServer.Tests;

public sealed class AnalyzerJobRecoveryTests
{
    [Fact]
    public async Task Valid_legacy_record_without_fingerprints_or_version_still_recovers()
    {
        await using var harness = new Harness();
        var id = harness.Seed(AnalyzerJobState.Queued);
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(harness.PathFor(id)))!.AsObject();
        document.Remove("requestFingerprint");
        document.Remove("runtimeFingerprint");
        document.Remove("version");
        File.WriteAllText(harness.PathFor(id), document.ToJsonString());
        await harness.Service.StartAsync(CancellationToken.None);
        await harness.WaitForAsync(() => harness.Service.Find(id)?.State == AnalyzerJobState.Succeeded);
        Assert.Equal(1, harness.Worker.Calls[id]);
    }

    [Theory]
    [InlineData("parameters")]
    [InlineData("assetIds")]
    [InlineData("artifactIds")]
    [InlineData("analyzer")]
    [InlineData("state")]
    [InlineData("id")]
    public async Task Structurally_invalid_json_record_does_not_stop_healthy_recovery(string field)
    {
        await using var harness = new Harness();
        var invalid = harness.Seed(AnalyzerJobState.Queued);
        var healthy = harness.Seed(AnalyzerJobState.Queued);
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(harness.PathFor(invalid)))!.AsObject();
        document["requestFingerprint"] = "";
        if (field == "parameters") document.Remove(field);
        else if (field == "state") document[field] = 999;
        else if (field == "id") document[field] = Guid.NewGuid().ToString();
        else document[field] = null;
        var damaged = document.ToJsonString();
        File.WriteAllText(harness.PathFor(invalid), damaged);

        await harness.RecoverAsync();
        Assert.Null(harness.Service.Find(invalid));
        await harness.Service.StartAsync(CancellationToken.None);
        await harness.WaitForAsync(() => harness.Service.Find(healthy)?.State == AnalyzerJobState.Succeeded);
        Assert.Equal(damaged, File.ReadAllText(harness.PathFor(invalid)));
        Assert.Single(harness.Worker.Calls);
        Assert.Equal(1, harness.Worker.Calls[healthy]);
    }

    [Fact]
    public async Task Unavailable_jobs_directory_does_not_fault_startup_and_is_retried()
    {
        await using var harness = new Harness();
        Assert.False(harness.Service.GetStorageHealth().IsHealthy);
        var existing = await harness.Service.CreateAsync(new AnalyzerJobRequest("embedding", "test", [],
            JsonSerializer.SerializeToElement(new { query = "already queued" }), false), CancellationToken.None);
        var recovered = harness.Seed(AnalyzerJobState.Running);
        harness.BlockJobsDirectory();
        try
        {
            await harness.Service.StartAsync(CancellationToken.None);
            // A consumer storage error proves startup reached queue processing despite failed enumeration.
            await harness.WaitForAsync(() => harness.Service.Find(existing.Id)?.StorageError is not null);
            Assert.Null(harness.Service.Find(recovered));
            Assert.Empty(harness.Worker.Calls);
            Assert.True(harness.Service.GetStorageHealth().RecoveryScanPending);
            Assert.False(harness.Service.GetStorageHealth().IsHealthy);
        }
        finally { harness.RestoreJobsDirectory(); }

        await harness.WaitForAsync(() => harness.Service.Find(recovered)?.State == AnalyzerJobState.Succeeded);
        await harness.WaitForAsync(() => harness.Service.Find(existing.Id)?.State == AnalyzerJobState.Succeeded);
        Assert.Equal(1, harness.Worker.Calls[recovered]);
        Assert.Equal(1, harness.Worker.Calls[existing.Id]);
        Assert.Equal(AnalyzerJobState.Succeeded, harness.ReadState(recovered));
        await harness.WaitForAsync(() => harness.Service.GetStorageHealth().IsHealthy);
    }

    [Fact]
    public async Task Unreadable_record_is_isolated_and_retried_after_storage_recovers()
    {
        await using var harness = new Harness();
        var blocked = harness.Seed(AnalyzerJobState.Queued);
        var healthy = harness.Seed(AnalyzerJobState.Queued);
        using (var file = new FileStream(harness.PathFor(blocked), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await harness.RecoverAsync();
            await harness.Service.StartAsync(CancellationToken.None);
            await harness.WaitForAsync(() => harness.Service.Find(healthy)?.State == AnalyzerJobState.Succeeded);
            Assert.Null(harness.Service.Find(blocked));
            Assert.Equal(1, harness.Service.GetStorageHealth().PendingRecoveryReads);
            Assert.False(harness.Service.GetStorageHealth().IsHealthy);
        }
        await harness.WaitForAsync(() => harness.Service.Find(blocked)?.State == AnalyzerJobState.Succeeded);
        Assert.Equal(1, harness.Worker.Calls[blocked]);
        await harness.WaitForAsync(() => harness.Service.GetStorageHealth().IsHealthy);
    }

    [Fact]
    public async Task Running_recovery_is_not_published_before_save_and_eventually_runs_once()
    {
        await using var harness = new Harness();
        var id = harness.Seed(AnalyzerJobState.Running);
        using (var file = new FileStream(harness.PathFor(id), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await harness.RecoverAsync();
            Assert.Equal(AnalyzerJobState.Running, harness.Service.Find(id)!.State);
            Assert.NotNull(harness.Service.Find(id)!.StorageError);
            Assert.Equal(0, harness.Service.GetMetrics().Queued);
            Assert.Empty(harness.Worker.Calls);
        }
        await harness.Service.StartAsync(CancellationToken.None);
        await harness.WaitForAsync(() => harness.Service.Find(id)?.State == AnalyzerJobState.Succeeded);
        Assert.Equal(1, harness.Worker.Calls[id]);
    }

    [Fact]
    public async Task Overflow_failure_is_published_only_after_durable_commit_without_running_inference()
    {
        await using var harness = new Harness(maxQueued: 1);
        await harness.Service.CreateAsync(new AnalyzerJobRequest("embedding", "test", [],
            JsonSerializer.SerializeToElement(new { query = "occupy queue" }), false), CancellationToken.None);
        var id = harness.Seed(AnalyzerJobState.Queued);
        using (var file = new FileStream(harness.PathFor(id), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await harness.RecoverAsync();
            Assert.Equal(AnalyzerJobState.Queued, harness.Service.Find(id)!.State);
            Assert.NotNull(harness.Service.Find(id)!.StorageError);
            Assert.Equal(AnalyzerJobState.Queued, harness.ReadState(id));
        }
        await harness.Service.StartAsync(CancellationToken.None);
        await harness.WaitForAsync(() => harness.Service.Find(id)?.State == AnalyzerJobState.Failed);
        Assert.Equal("queue_full", harness.Service.Find(id)!.ErrorCode);
        Assert.Equal(AnalyzerJobState.Failed, harness.ReadState(id));
        Assert.False(harness.Worker.Calls.ContainsKey(id));
    }

    [Theory]
    [InlineData(AnalyzerJobState.Queued)]
    [InlineData(AnalyzerJobState.Running)]
    public async Task Failed_cancel_commit_is_retried_without_starting_inference(AnalyzerJobState initialState)
    {
        await using var harness = new Harness();
        var id = harness.Seed(initialState);
        using (var file = new FileStream(harness.PathFor(id), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await harness.RecoverAsync();
            var error = await Record.ExceptionAsync(() => harness.Service.CancelAsync(id, CancellationToken.None));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Equal(initialState, harness.ReadState(id));
            Assert.Equal(initialState, harness.Service.Find(id)!.State);
            Assert.NotNull(harness.Service.Find(id)!.StorageError);
            Assert.Equal(1, harness.Service.GetStorageHealth().PendingCommits);
            Assert.False(harness.Service.GetStorageHealth().IsHealthy);
        }
        await harness.Service.StartAsync(CancellationToken.None);
        await harness.WaitForAsync(() => harness.Service.Find(id)?.State == AnalyzerJobState.Cancelled);
        Assert.Equal(AnalyzerJobState.Cancelled, harness.ReadState(id));
        Assert.False(harness.Worker.Calls.ContainsKey(id));
        Assert.Null(harness.Service.Find(id)!.StorageError);
        await harness.WaitForAsync(() => harness.Service.GetStorageHealth().IsHealthy);
    }

    [Fact]
    public async Task Failed_cancel_commit_takes_precedence_over_late_worker_failure()
    {
        await using var harness = new Harness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Worker.Handler = async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(token.IsCancellationRequested);
            throw new InvalidOperationException("late worker failure");
        };
        var job = await harness.Service.CreateAsync(new AnalyzerJobRequest("embedding", "test", [],
            JsonSerializer.SerializeToElement(new { query = "cancel running worker" }), false), CancellationToken.None);
        await harness.Service.StartAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            using (var file = new FileStream(harness.PathFor(job.Id), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var error = await Record.ExceptionAsync(() => harness.Service.CancelAsync(job.Id, CancellationToken.None));
                Assert.True(error is IOException or UnauthorizedAccessException);
                Assert.Equal(AnalyzerJobState.Running, harness.ReadState(job.Id));
                Assert.Equal(AnalyzerJobState.Running, harness.Service.Find(job.Id)!.State);
                release.TrySetResult();
                // Keep publication blocked until the worker has completed its terminal transition.
                await harness.WaitForAsync(() => !harness.HasWorker(job.Id));
            }
        }
        finally { release.TrySetResult(); }
        await harness.WaitForAsync(() => harness.Service.Find(job.Id)?.State == AnalyzerJobState.Cancelled);
        Assert.Equal(AnalyzerJobState.Cancelled, harness.ReadState(job.Id));
        Assert.Equal("cancelled", harness.Service.Find(job.Id)!.ErrorCode);
        Assert.Equal(1, harness.Worker.Calls[job.Id]);
    }

    [Fact]
    public async Task Cleanup_preserves_locked_job_and_retries_without_blocking_other_jobs()
    {
        await using var harness = new Harness();
        var blocked = harness.Seed(AnalyzerJobState.Succeeded, DateTimeOffset.UtcNow.AddDays(-10));
        var removable = harness.Seed(AnalyzerJobState.Cancelled, DateTimeOffset.UtcNow.AddDays(-11));
        var active = harness.Seed(AnalyzerJobState.Queued);
        await harness.RecoverAsync();
        using (var file = new FileStream(harness.PathFor(blocked), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await harness.Service.RunCleanupAsync(CancellationToken.None);
            Assert.Equal(AnalyzerJobState.Succeeded, harness.Service.Find(blocked)!.State);
            Assert.NotNull(harness.Service.Find(blocked)!.StorageError);
            Assert.True(File.Exists(harness.PathFor(blocked)));
            Assert.Null(harness.Service.Find(removable));
            Assert.False(File.Exists(harness.PathFor(removable)));
            Assert.NotNull(harness.Service.Find(active));
        }
        await harness.Service.RunCleanupAsync(CancellationToken.None);
        Assert.Null(harness.Service.Find(blocked));
        Assert.False(File.Exists(harness.PathFor(blocked)));
        await harness.RecoverAsync();
        Assert.Null(harness.Service.Find(blocked));
        Assert.Null(harness.Service.Find(removable));
    }

    [Fact]
    public async Task Cleanup_waits_for_existing_job_operations_before_removing_history()
    {
        await using var harness = new Harness();
        var id = harness.Seed(AnalyzerJobState.Succeeded, DateTimeOffset.UtcNow.AddDays(-10));
        await harness.RecoverAsync();
        var gate = harness.GateFor(id);
        await gate.WaitAsync();
        Task cleanup;
        Task<AnalyzerJobResponse?> cancel;
        try
        {
            cancel = harness.Service.CancelAsync(id, CancellationToken.None);
            cleanup = harness.Service.RunCleanupAsync(CancellationToken.None);
            Assert.False(cleanup.IsCompleted);
            Assert.NotNull(harness.Service.Find(id));
        }
        finally { gate.Release(); }
        await Task.WhenAll(cancel, cleanup).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(harness.Service.Find(id));
        Assert.False(File.Exists(harness.PathFor(id)));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "KadrStudio", "recovery-tests", Guid.NewGuid().ToString("N"));
        public AnalyzerJobService Service { get; }
        public Worker Worker { get; } = new();

        public Harness(int maxQueued = 32)
        {
            Directory.CreateDirectory(_root);
            var model = Path.Combine(_root, "models", "embedding", "model.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(model)!);
            File.WriteAllText(model, "model");
            var options = new AiServerOptions
            {
                DataRoot = _root, EmbeddingModelPath = model,
                ProductionModelsRoot = Path.Combine(_root, "models"),
                WorkersRoot = Path.Combine(_root, "workers"), MaxQueuedJobs = maxQueued,
                MaxActiveJobs = 1, MaxDataRootBytes = 1024 * 1024 * 1024
            };
            Service = new AnalyzerJobService(options, new ContentAddressedAssetStore(_root, 1024 * 1024),
                new ContentAddressedArtifactStore(_root), Worker, new ModelCapabilityGate(options));
        }

        public string PathFor(Guid id) => Path.Combine(_root, "jobs", id.ToString("N") + ".json");

        public SemaphoreSlim GateFor(Guid id) => ((ConcurrentDictionary<Guid, SemaphoreSlim>)
            typeof(AnalyzerJobService).GetField("_stateGates", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Service)!)[id];

        public bool HasWorker(Guid id) => ((ConcurrentDictionary<Guid, CancellationTokenSource>)
            typeof(AnalyzerJobService).GetField("_cancellations", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Service)!).ContainsKey(id);

        public void BlockJobsDirectory()
        {
            Directory.Move(Path.Combine(_root, "jobs"), Path.Combine(_root, "jobs-unavailable"));
            File.WriteAllText(Path.Combine(_root, "jobs"), "temporary directory blocker");
        }

        public void RestoreJobsDirectory()
        {
            File.Delete(Path.Combine(_root, "jobs"));
            Directory.Move(Path.Combine(_root, "jobs-unavailable"), Path.Combine(_root, "jobs"));
        }

        public Guid Seed(AnalyzerJobState state, DateTimeOffset? updatedAt = null)
        {
            var id = Guid.NewGuid();
            File.WriteAllText(PathFor(id), JsonSerializer.Serialize(new
            {
                id, analyzer = "embedding", analyzerVersion = "test", assetIds = Array.Empty<string>(),
                parameters = new { query = id.ToString() }, state, progress = 0.5, message = "original",
                artifactIds = Array.Empty<string>(), createdAt = DateTimeOffset.UtcNow,
                updatedAt = updatedAt ?? DateTimeOffset.UtcNow, requireProduction = false,
                requestFingerprint = id.ToString(), version = 7
            }));
            return id;
        }

        public AnalyzerJobState ReadState(Guid id)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(PathFor(id)));
            return json.RootElement.GetProperty("state").Deserialize<AnalyzerJobState>();
        }

        public Task RecoverAsync() => (Task)typeof(AnalyzerJobService)
            .GetMethod("RecoverAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Service, [CancellationToken.None])!;

        public async Task WaitForAsync(Func<bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!predicate())
            {
                if (Service.ExecuteTask?.IsFaulted == true) await Service.ExecuteTask;
                await Task.Delay(10, timeout.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Service.StopAsync(CancellationToken.None);
            Service.Dispose();
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }
    }

    private sealed class Worker : IWorkerGateway
    {
        public ConcurrentDictionary<Guid, int> Calls { get; } = new();
        public Func<CancellationToken, Task<WorkerJobResult>>? Handler { get; set; }
        public Task<WorkerJobResult> ExecuteAsync(WorkerJob job, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            Calls.AddOrUpdate(job.Id, 1, (_, count) => count + 1);
            if (Handler is not null) return Handler(cancellationToken);
            return Task.FromResult(new WorkerJobResult(true, "{}"u8.ToArray(), []));
        }
        public Task<int> CountTokensAsync(string model, string text, CancellationToken cancellationToken) => Task.FromResult(1);
        public Task ReleaseAcceleratorAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
