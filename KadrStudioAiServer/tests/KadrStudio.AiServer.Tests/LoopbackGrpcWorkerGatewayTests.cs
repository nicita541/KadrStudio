using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using KadrStudio.AiServer.Configuration;
using KadrStudio.AiServer.Workers;

namespace KadrStudio.AiServer.Tests;

public sealed class LoopbackGrpcWorkerGatewayTests
{
    [Fact]
    public async Task Concurrent_cold_start_publishes_one_runtime_per_analyzer()
    {
        var launcher = new ControlledRuntimeLauncher { PauseStarts = true };
        await using var gateway = CreateGateway(launcher);
        var calls = Enumerable.Range(0, 12)
            .Select(index => gateway.ExecuteAsync(Job("audio-events", index), null, CancellationToken.None))
            .ToArray();
        await launcher.WaitForAnyStartAsync("audio-events");

        try
        {
            await Task.Delay(100);
            Assert.Equal(1, launcher.StartCount("audio-events"));
        }
        finally
        {
            launcher.ReleaseStarts();
            await Task.WhenAll(calls);
        }
    }

    [Fact]
    public async Task Count_tokens_waits_for_vision_accelerator_lease_and_evicts_vision()
    {
        var launcher = new ControlledRuntimeLauncher();
        launcher.BlockRunJob("video-understanding");
        await using var gateway = CreateGateway(launcher);
        var vision = gateway.ExecuteAsync(Job("video-understanding", 1), null, CancellationToken.None);
        await launcher.WaitForRequestAsync("video-understanding");

        var tokens = gateway.CountTokensAsync("planner", "hello", CancellationToken.None);
        try
        {
            await Task.Delay(100);
            Assert.Equal(0, launcher.StartCount("director"));
        }
        finally
        {
            launcher.ReleaseRunJob("video-understanding");
        }

        await vision;
        Assert.Equal(5, await tokens);
        Assert.True(launcher.Latest("video-understanding").DisposeCount > 0);
    }

    [Fact]
    public async Task Cpu_embedding_can_run_while_vision_holds_the_accelerator()
    {
        var launcher = new ControlledRuntimeLauncher();
        launcher.BlockRunJob("video-understanding");
        await using var gateway = CreateGateway(launcher);
        var vision = gateway.ExecuteAsync(Job("video-understanding", 1), null, CancellationToken.None);
        await launcher.WaitForRequestAsync("video-understanding");

        var embedding = gateway.ExecuteAsync(Job("embedding", 2), null, CancellationToken.None);
        try
        {
            await launcher.WaitForAnyStartAsync("embedding");
            Assert.Equal(1, launcher.StartCount("embedding"));
            await embedding;
        }
        finally
        {
            launcher.ReleaseRunJob("video-understanding");
        }

        await vision;
    }

    [Fact]
    public async Task Exited_worker_is_disposed_and_restarted()
    {
        var launcher = new ControlledRuntimeLauncher();
        await using var gateway = CreateGateway(launcher);
        await gateway.ExecuteAsync(Job("audio-events", 1), null, CancellationToken.None);
        var crashed = launcher.Latest("audio-events");
        crashed.HasExited = true;

        await gateway.ExecuteAsync(Job("audio-events", 2), null, CancellationToken.None);

        Assert.Equal(2, launcher.StartCount("audio-events"));
        Assert.Equal(1, crashed.DisposeCount);
    }

    private static LoopbackGrpcWorkerGateway CreateGateway(ControlledRuntimeLauncher launcher)
        => new(new AiServerOptions
        {
            DataRoot = Path.Combine(Path.GetTempPath(), "KadrStudio", "gateway-tests"),
            WorkersRoot = Path.Combine(Path.GetTempPath(), "KadrStudio", "gateway-workers")
        }, launcher.StartAsync);

    private static WorkerJob Job(string analyzer, int index)
        => new(Guid.NewGuid(), analyzer, "test-v1", [],
            JsonSerializer.SerializeToElement(new { index }));

    private sealed class ControlledRuntimeLauncher
    {
        private readonly ConcurrentDictionary<string, int> _starts = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ConcurrentBag<FakeRuntime>> _runtimes = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _runGates = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _requests = new(StringComparer.OrdinalIgnoreCase);
        private readonly TaskCompletionSource _startGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool PauseStarts { get; init; }

        public async Task<IWorkerRuntime> StartAsync(string analyzer, CancellationToken cancellationToken)
        {
            _starts.AddOrUpdate(analyzer, 1, (_, value) => value + 1);
            if (PauseStarts) await _startGate.Task.WaitAsync(cancellationToken);
            var runtime = new FakeRuntime(
                analyzer,
                _runGates.GetOrAdd(analyzer, _ => CompletedSource()),
                _requests.GetOrAdd(analyzer, _ => NewSource()));
            _runtimes.GetOrAdd(analyzer, _ => []).Add(runtime);
            return runtime;
        }

        public int StartCount(string analyzer) => _starts.GetValueOrDefault(analyzer);
        public FakeRuntime Latest(string analyzer) => _runtimes[analyzer].Last();
        public void ReleaseStarts() => _startGate.TrySetResult();
        public void BlockRunJob(string analyzer) => _runGates[analyzer] = NewSource();
        public void ReleaseRunJob(string analyzer) => _runGates[analyzer].TrySetResult();
        public Task WaitForRequestAsync(string analyzer)
            => _requests.GetOrAdd(analyzer, _ => NewSource()).Task.WaitAsync(TimeSpan.FromSeconds(5));

        public async Task WaitForAnyStartAsync(string analyzer)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (StartCount(analyzer) == 0)
                await Task.Delay(10, timeout.Token);
        }

        private static TaskCompletionSource NewSource()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static TaskCompletionSource CompletedSource()
        {
            var source = NewSource();
            source.SetResult();
            return source;
        }
    }

    private sealed class FakeRuntime : IWorkerRuntime
    {
        private readonly HttpClient _client;
        private int _disposeCount;

        public FakeRuntime(string analyzer, TaskCompletionSource runGate, TaskCompletionSource requestStarted)
        {
            _client = new HttpClient(new GrpcHandler(runGate, requestStarted))
            {
                BaseAddress = new Uri("http://127.0.0.1/")
            };
        }

        public bool HasExited { get; set; }
        public int ExitCode => HasExited ? 1 : 0;
        public HttpClient Client => _client;
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Dispose() => Interlocked.Increment(ref _disposeCount);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class GrpcHandler(
        TaskCompletionSource runGate,
        TaskCompletionSource requestStarted) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var isTokenize = request.RequestUri!.AbsolutePath.EndsWith("/CountTokens", StringComparison.Ordinal);
            if (!isTokenize)
            {
                requestStarted.TrySetResult();
                await runGate.Task.WaitAsync(cancellationToken);
            }
            var payload = isTokenize
                ? new byte[] { 0x08, 0x05 }
                : new byte[] { 0x08, 0x01, 0x22, 0x02, 0x7b, 0x7d };
            var frame = new byte[payload.Length + 5];
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1, 4), payload.Length);
            payload.CopyTo(frame, 5);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Version = HttpVersion.Version20,
                Content = new ByteArrayContent(frame)
            };
        }
    }
}
