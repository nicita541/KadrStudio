using System.Collections.Concurrent;
using System.Text.Json;
using KadrStudio.AiServer.Api;
using KadrStudio.AiServer.Configuration;
using KadrStudio.AiServer.Inference;
using KadrStudio.AiServer.Jobs;
using KadrStudio.AiServer.Storage;
using KadrStudio.AiServer.Workers;

namespace KadrStudio.AiServer.Tests;

public sealed class AnalyzerJobServiceTests
{
    private static AnalyzerJobRequest OwnedRequest(string owner, string request = "request-1")
        => JsonSerializer.Deserialize<AnalyzerJobRequest>(JsonSerializer.Serialize(new {
            analyzer = "embedding", analyzerVersion = "test-v1", assetIds = Array.Empty<string>(),
            parameters = new { documents = new[] { new { id = "doc", text = "text" } }, query = "query" },
            requireProduction = false, ownerId = owner, requestId = request
        }), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public async Task Active_jobs_are_isolated_by_owner_but_reused_inside_owner()
    {
        await using var harness = new JobHarness();
        var first = await harness.Service.CreateAsync(OwnedRequest("owner-a"), default);
        var same = await harness.Service.CreateAsync(OwnedRequest("owner-a", "request-2"), default);
        var other = await harness.Service.CreateAsync(OwnedRequest("owner-b"), default);
        Assert.Equal(first.Id, same.Id);
        Assert.NotEqual(first.Id, other.Id);
    }

    [Fact]
    public async Task Owned_active_job_rejects_legacy_unscoped_cancellation()
    {
        await using var harness = new JobHarness();
        var job = await harness.Service.CreateAsync(OwnedRequest("owner-a"), default);
        var error = await Assert.ThrowsAsync<AnalyzerJobException>(() => harness.Service.CancelAsync(job.Id, default));
        Assert.Equal("job_owner_mismatch", error.ErrorCode);
        Assert.Equal(AnalyzerJobState.Queued, harness.Service.Find(job.Id)!.State);
    }

    [Fact]
    public async Task Failed_terminal_publication_never_reports_success_and_retries_without_inference()
    {
        await using var harness = new JobHarness(WorkerCompletion.IgnoreCancellationUntilReleased);
        await harness.StartAsync();
        var created = await harness.CreateAsync("terminal-storage");
        await harness.Worker.WaitUntilStartedAsync(created.Id);
        using (var blocker = new FileStream(harness.JobPath(created.Id), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            harness.Worker.Release(created.Id);
            await harness.Worker.WaitUntilReturnedAsync(created.Id);
            await Task.Delay(150);
            Assert.NotEqual(AnalyzerJobState.Succeeded, harness.Service.Find(created.Id)!.State);
        }
        Assert.Equal(AnalyzerJobState.Succeeded, (await WaitForTerminalAsync(harness.Service, created.Id)).State);
        Assert.Equal(1, harness.Worker.StartedCount);
    }

    [Fact]
    public async Task Request_aliases_survive_cancel_and_reject_changed_inputs()
    {
        await using var harness = new JobHarness();
        var first = await harness.Service.CreateAsync(OwnedRequest("owner-a"), default);
        await harness.Service.CreateAsync(OwnedRequest("owner-a", "alias"), default);
        var cancelled = await harness.Service.CancelAsync(first.Id, default, "owner-a");
        Assert.Equal(AnalyzerJobState.Cancelled, cancelled!.State);
        var retried = await harness.Service.CreateAsync(OwnedRequest("owner-a", "alias"), default);
        Assert.Equal(first.Id, retried.Id);
        Assert.Equal(AnalyzerJobState.Cancelled, retried.State);
        using var persisted = JsonDocument.Parse(File.ReadAllText(harness.JobPath(first.Id)));
        Assert.Equal(2, persisted.RootElement.GetProperty("requestIds").GetArrayLength());
        var changed = OwnedRequest("owner-a", "alias") with { Parameters = JsonSerializer.SerializeToElement(new { query = "changed" }) };
        var error = await Assert.ThrowsAsync<AnalyzerJobException>(() => harness.Service.CreateAsync(changed, default));
        Assert.Equal("request_identity_conflict", error.ErrorCode);
    }

    [Fact]
    public async Task Owner_is_enforced_after_loading_durable_record_into_new_service()
    {
        await using var original = new JobHarness();
        var job = await original.Service.CreateAsync(OwnedRequest("owner-a"), default);
        await using var recovered = new JobHarness();
        File.Copy(original.JobPath(job.Id), recovered.JobPath(job.Id));
        await (Task)typeof(AnalyzerJobService).GetMethod("RecoverAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(recovered.Service, [CancellationToken.None])!;
        var error = await Assert.ThrowsAsync<AnalyzerJobException>(() => recovered.Service.CancelAsync(job.Id, default, "owner-b"));
        Assert.Equal("job_owner_mismatch", error.ErrorCode);
        Assert.Equal(AnalyzerJobState.Cancelled, (await recovered.Service.CancelAsync(job.Id, default, "owner-a"))!.State);
    }

    [Fact]
    public async Task Cancellation_while_reuse_waits_creates_fresh_job_for_new_request()
    {
        await using var harness = new JobHarness();
        var first = await harness.Service.CreateAsync(OwnedRequest("owner-a"), default);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var gates = (ConcurrentDictionary<Guid, SemaphoreSlim>)typeof(AnalyzerJobService)
            .GetField("_stateGates", flags)!.GetValue(harness.Service)!;
        var gate = gates[first.Id];
        await gate.WaitAsync();
        Task<AnalyzerJobResponse?> cancellation;
        Task<AnalyzerJobResponse> creation;
        try
        {
            cancellation = harness.Service.CancelAsync(first.Id, default, "owner-a");
            creation = harness.Service.CreateAsync(OwnedRequest("owner-a", "fresh"), default);
            // Observe both actual asynchronous gate waiters before releasing cancellation first.
            var head = typeof(SemaphoreSlim).GetField("m_asyncHead", flags)!;
            var tail = typeof(SemaphoreSlim).GetField("m_asyncTail", flags)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (head.GetValue(gate) is null || ReferenceEquals(head.GetValue(gate), tail.GetValue(gate)))
                await Task.Delay(10, timeout.Token);
        }
        finally { gate.Release(); }
        Assert.Equal(AnalyzerJobState.Cancelled, (await cancellation)!.State);
        var fresh = await creation;
        Assert.NotEqual(first.Id, fresh.Id);
        Assert.Equal(AnalyzerJobState.Queued, fresh.State);
        Assert.Equal(fresh.Id, (await harness.Service.CreateAsync(OwnedRequest("owner-a", "fresh"), default)).Id);
    }

    [Fact]
    public async Task Completed_reuse_limits_total_durable_request_bindings()
    {
        await using var harness = new JobHarness();
        await harness.StartAsync();
        var first = await harness.Service.CreateAsync(OwnedRequest("owner-a"), default);
        await WaitForTerminalAsync(harness.Service, first.Id);
        for (var i = 0; i < 127; i++)
            Assert.Equal(first.Id, (await harness.Service.CreateAsync(OwnedRequest("other-" + i), default)).Id);
        var error = await Assert.ThrowsAsync<AnalyzerJobException>(() => harness.Service.CreateAsync(OwnedRequest("overflow"), default));
        Assert.Equal("job_request_limit", error.ErrorCode);
        Assert.Equal(first.Id, (await harness.Service.CreateAsync(OwnedRequest("other-0"), default)).Id);
    }

    [Fact]
    public async Task Completed_reuse_binds_request_identity_across_reload()
    {
        await using var original = new JobHarness();
        await original.StartAsync();
        var first = await original.Service.CreateAsync(OwnedRequest("owner-a"), default);
        await WaitForTerminalAsync(original.Service, first.Id);
        await original.Service.CreateAsync(OwnedRequest("owner-b", "shared"), default);
        var changed = OwnedRequest("owner-b", "shared") with { Parameters = JsonSerializer.SerializeToElement(new { query = "changed" }) };
        var initialError = await Assert.ThrowsAsync<AnalyzerJobException>(() => original.Service.CreateAsync(changed, default));
        Assert.Equal("request_identity_conflict", initialError.ErrorCode);
        await original.StopAsync();
        await using var recovered = new JobHarness(root: original.Root);
        await (Task)typeof(AnalyzerJobService).GetMethod("RecoverAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(recovered.Service, [CancellationToken.None])!;
        Assert.Equal(first.Id, (await recovered.Service.CreateAsync(OwnedRequest("owner-b", "shared"), default)).Id);
        var error = await Assert.ThrowsAsync<AnalyzerJobException>(() => recovered.Service.CreateAsync(changed, default));
        Assert.Equal("request_identity_conflict", error.ErrorCode);
    }

    [Fact]
    public async Task Completed_immutable_result_is_reused_across_owners_without_new_inference()
    {
        await using var harness = new JobHarness();
        await harness.StartAsync();
        var first = await harness.Service.CreateAsync(OwnedRequest("owner-a"), default);
        Assert.Equal(AnalyzerJobState.Succeeded, (await WaitForTerminalAsync(harness.Service, first.Id)).State);
        var other = await harness.Service.CreateAsync(OwnedRequest("owner-b"), default);
        Assert.Equal(first.Id, other.Id);
        Assert.Equal(1, harness.Worker.StartedCount);
        Assert.Equal(AnalyzerJobState.Succeeded, (await harness.Service.CancelAsync(other.Id, default, "owner-b"))!.State);
    }

    [Fact]
    public async Task Failed_running_publication_does_not_kill_consumer_or_start_uncommitted_job()
    {
        await using var harness = new JobHarness(maxActiveJobs: 1);
        var blocked = await harness.CreateAsync("blocked-storage");
        var next = await harness.CreateAsync("next-storage");
        using (var blocker = new FileStream(harness.JobPath(blocked.Id), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await harness.StartAsync();
            await harness.Worker.WaitUntilStartedAsync(next.Id);
            Assert.Equal(AnalyzerJobState.Queued, harness.Service.Find(blocked.Id)!.State);
        }
        Assert.Equal(AnalyzerJobState.Succeeded, (await WaitForTerminalAsync(harness.Service, blocked.Id)).State);
    }

    [Fact]
    public async Task Cancel_queued_job_is_terminal_before_the_service_starts()
    {
        await using var harness = new JobHarness();
        var created = await harness.CreateAsync("queued");

        var cancelled = await harness.Service.CancelAsync(created.Id, CancellationToken.None);

        Assert.Equal(AnalyzerJobState.Cancelled, cancelled!.State);
        Assert.Equal(AnalyzerJobState.Cancelled, harness.Service.Find(created.Id)!.State);
        Assert.Equal(0, harness.Worker.StartedCount);
    }

    [Fact]
    public async Task Cancel_running_job_propagates_to_the_worker_token()
    {
        await using var harness = new JobHarness(WorkerCompletion.WaitForCancellation);
        await harness.StartAsync();
        var created = await harness.CreateAsync("running");
        await harness.Worker.WaitUntilStartedAsync(created.Id);

        var cancelled = await harness.Service.CancelAsync(created.Id, CancellationToken.None);

        await harness.Worker.WaitUntilCancelledAsync(created.Id);
        Assert.Equal(AnalyzerJobState.Cancelled, cancelled!.State);
        Assert.Equal(AnalyzerJobState.Cancelled, (await WaitForTerminalAsync(harness.Service, created.Id)).State);
    }

    [Fact]
    public async Task Worker_completion_after_cancel_cannot_resurrect_the_job()
    {
        await using var harness = new JobHarness(WorkerCompletion.IgnoreCancellationUntilReleased);
        await harness.StartAsync();
        var created = await harness.CreateAsync("late-completion");
        await harness.Worker.WaitUntilStartedAsync(created.Id);

        await harness.Service.CancelAsync(created.Id, CancellationToken.None);
        harness.Worker.Release(created.Id);

        await harness.Worker.WaitUntilReturnedAsync(created.Id);
        await Task.Delay(100);
        Assert.Equal(AnalyzerJobState.Cancelled, harness.Service.Find(created.Id)!.State);
    }

    [Fact]
    public async Task Cancel_at_completion_has_exactly_one_terminal_winner()
    {
        await using var harness = new JobHarness(WorkerCompletion.IgnoreCancellationUntilReleased);
        await harness.StartAsync();
        var created = await harness.CreateAsync("completion-race");
        await harness.Worker.WaitUntilStartedAsync(created.Id);

        var cancel = harness.Service.CancelAsync(created.Id, CancellationToken.None);
        harness.Worker.Release(created.Id);
        await cancel;
        await harness.Worker.WaitUntilReturnedAsync(created.Id);
        var terminal = await WaitForTerminalAsync(harness.Service, created.Id);
        await Task.Delay(100);

        Assert.Equal(AnalyzerJobState.Cancelled, terminal.State);
        Assert.Equal(AnalyzerJobState.Cancelled, harness.Service.Find(created.Id)!.State);
    }

    [Fact]
    public async Task Repeated_cancel_is_idempotent()
    {
        await using var harness = new JobHarness();
        var created = await harness.CreateAsync("repeat");

        var first = await harness.Service.CancelAsync(created.Id, CancellationToken.None);
        var second = await harness.Service.CancelAsync(created.Id, CancellationToken.None);

        Assert.Equal(AnalyzerJobState.Cancelled, first!.State);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Shutdown_while_cancelling_keeps_the_job_cancelled()
    {
        await using var harness = new JobHarness(WorkerCompletion.WaitForCancellation);
        await harness.StartAsync();
        var created = await harness.CreateAsync("shutdown");
        await harness.Worker.WaitUntilStartedAsync(created.Id);

        await harness.Service.CancelAsync(created.Id, CancellationToken.None);
        await harness.StopAsync();

        Assert.Equal(AnalyzerJobState.Cancelled, harness.Service.Find(created.Id)!.State);
    }

    [Fact]
    public async Task Replacing_embedding_model_does_not_reuse_old_successful_job()
    {
        await using var harness = new JobHarness();
        await harness.StartAsync();
        var first = await harness.CreateAsync("same-request");
        await WaitForTerminalAsync(harness.Service, first.Id);

        harness.ReplaceEmbeddingModel("model-b-is-different");
        var second = await harness.CreateAsync("same-request");
        await WaitForTerminalAsync(harness.Service, second.Id);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, harness.Worker.StartedCount);
    }

    [Fact]
    public async Task Queue_saturation_rejects_new_job_with_typed_error()
    {
        await using var harness = new JobHarness(
            WorkerCompletion.WaitForCancellation, maxQueuedJobs: 1, maxActiveJobs: 1);
        await harness.StartAsync();
        var active = await harness.CreateAsync("active");
        await harness.Worker.WaitUntilStartedAsync(active.Id);
        await harness.CreateAsync("queued");

        var error = await Assert.ThrowsAsync<AnalyzerJobException>(
            () => harness.CreateAsync("overflow"));

        Assert.Equal("queue_full", error.ErrorCode);
        Assert.Equal(1, harness.Service.GetMetrics().Queued);
        Assert.Equal(1, harness.Service.GetMetrics().Active);
    }

    [Fact]
    public async Task Cleanup_removes_expired_completed_jobs_and_caps_history()
    {
        await using var harness = new JobHarness(
            jobRetention: TimeSpan.Zero, maxJobHistory: 1);
        await harness.StartAsync();
        var completed = await harness.CreateAsync("expired");
        await WaitForTerminalAsync(harness.Service, completed.Id);

        await harness.Service.RunCleanupAsync(CancellationToken.None);

        Assert.Null(harness.Service.Find(completed.Id));
        Assert.True(harness.Service.GetMetrics().CleanupCount >= 1);
    }

    private static async Task<AnalyzerJobResponse> WaitForTerminalAsync(AnalyzerJobService service, Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var current = service.Find(id)!;
            if (current.State is AnalyzerJobState.Succeeded or AnalyzerJobState.Failed or AnalyzerJobState.Cancelled)
                return current;
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class JobHarness : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "KadrStudio", "job-state-tests", Guid.NewGuid().ToString("N"));
        private bool _started;
        private readonly string _embeddingModelPath;

        public JobHarness(
            WorkerCompletion completion = WorkerCompletion.SucceedImmediately,
            int maxQueuedJobs = 32,
            int maxActiveJobs = 2,
            TimeSpan? jobRetention = null,
            int maxJobHistory = 100,
            string? root = null)
        {
            _root = root ?? _root;
            Directory.CreateDirectory(_root);
            _embeddingModelPath = Path.Combine(_root, "models", "embedding", "model.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(_embeddingModelPath)!);
            if (!File.Exists(_embeddingModelPath)) File.WriteAllText(_embeddingModelPath, "model-a");
            var options = new AiServerOptions
            {
                DataRoot = _root,
                ProductionModelsRoot = Path.Combine(_root, "models"),
                WorkersRoot = Path.Combine(_root, "workers"),
                EmbeddingModelPath = _embeddingModelPath,
                MaxQueuedJobs = maxQueuedJobs,
                MaxActiveJobs = maxActiveJobs,
                JobRetention = jobRetention ?? TimeSpan.FromDays(7),
                MaxJobHistory = maxJobHistory,
                PartialUploadTtl = TimeSpan.FromHours(24),
                MaxDataRootBytes = 1024 * 1024 * 1024
            };
            Worker = new ControlledWorkerGateway(completion);
            Service = new AnalyzerJobService(
                options,
                new ContentAddressedAssetStore(_root, 1024 * 1024),
                new ContentAddressedArtifactStore(_root),
                Worker,
                new ModelCapabilityGate(options));
        }

        public AnalyzerJobService Service { get; }
        public string Root => _root;
        public ControlledWorkerGateway Worker { get; }
        public string JobPath(Guid id) => Path.Combine(_root, "jobs", id.ToString("N") + ".json");

        public async Task StartAsync()
        {
            await Service.StartAsync(CancellationToken.None);
            _started = true;
        }

        public async Task StopAsync()
        {
            if (!_started) return;
            await Service.StopAsync(CancellationToken.None);
            _started = false;
        }

        public Task<AnalyzerJobResponse> CreateAsync(string nonce)
            => Service.CreateAsync(new AnalyzerJobRequest(
                "embedding",
                "test-v1",
                [],
                JsonSerializer.SerializeToElement(new
                {
                    nonce,
                    documents = new[] { new { id = "doc", text = "text" } },
                    query = "query"
                }),
                RequireProduction: false), CancellationToken.None);

        public void ReplaceEmbeddingModel(string payload)
        {
            File.WriteAllText(_embeddingModelPath, payload);
            File.SetLastWriteTimeUtc(_embeddingModelPath, DateTime.UtcNow.AddSeconds(2));
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var id in Worker.RunningIds) Worker.Release(id);
            await StopAsync();
            Service.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }

    private enum WorkerCompletion
    {
        SucceedImmediately,
        WaitForCancellation,
        IgnoreCancellationUntilReleased
    }

    private sealed class ControlledWorkerGateway(WorkerCompletion completion) : IWorkerGateway
    {
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _started = new();
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _cancelled = new();
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _released = new();
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _returned = new();

        public int StartedCount => _started.Values.Count(item => item.Task.IsCompleted);
        public IEnumerable<Guid> RunningIds => _started.Keys;

        public async Task<WorkerJobResult> ExecuteAsync(
            WorkerJob job,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            Signal(_started, job.Id);
            try
            {
                if (completion == WorkerCompletion.WaitForCancellation)
                {
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        Signal(_cancelled, job.Id);
                        throw;
                    }
                }
                else if (completion == WorkerCompletion.IgnoreCancellationUntilReleased)
                {
                    await Source(_released, job.Id).Task;
                }
                return new WorkerJobResult(true, "{}"u8.ToArray(), []);
            }
            finally
            {
                Signal(_returned, job.Id);
            }
        }

        public Task<int> CountTokensAsync(string model, string text, CancellationToken cancellationToken)
            => Task.FromResult(1);

        public Task ReleaseAcceleratorAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task WaitUntilStartedAsync(Guid id) => Source(_started, id).Task.WaitAsync(TimeSpan.FromSeconds(5));
        public Task WaitUntilCancelledAsync(Guid id) => Source(_cancelled, id).Task.WaitAsync(TimeSpan.FromSeconds(5));
        public Task WaitUntilReturnedAsync(Guid id) => Source(_returned, id).Task.WaitAsync(TimeSpan.FromSeconds(5));
        public void Release(Guid id) => Signal(_released, id);

        private static TaskCompletionSource Source(
            ConcurrentDictionary<Guid, TaskCompletionSource> sources,
            Guid id) => sources.GetOrAdd(id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        private static void Signal(ConcurrentDictionary<Guid, TaskCompletionSource> sources, Guid id)
            => Source(sources, id).TrySetResult();
    }
}
