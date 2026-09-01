using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using KadrStudio.AiServer.Configuration;

namespace KadrStudio.AiServer.Workers;

/// <summary>
/// Minimal unary gRPC client for the versioned local worker protocol. It keeps
/// the trust gateway in C# without coupling workers to the desktop or file paths.
/// </summary>
public sealed class LoopbackGrpcWorkerGateway : IWorkerGateway, IDisposable, IAsyncDisposable
{
    private const string ProtocolVersion = "2";
    private readonly string _workersRoot;
    private readonly string _dataRoot;
    private readonly ConcurrentDictionary<string, WorkerRuntime> _workers = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _acceleratorLease = new(1, 1);

    private static readonly HashSet<string> AcceleratorAnalyzers = new(StringComparer.OrdinalIgnoreCase)
    {
        "video-understanding", "asr-align", "diarization", "embedding", "director", "critic"
    };

    public LoopbackGrpcWorkerGateway(AiServerOptions options)
    {
        _workersRoot = Path.GetFullPath(options.WorkersRoot);
        _dataRoot = Path.GetFullPath(options.DataRoot);
    }

    public async Task<WorkerJobResult> ExecuteAsync(
        WorkerJob job,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var usesAccelerator = UsesAccelerator(job.Analyzer);
        if (usesAccelerator)
            await _acceleratorLease.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (usesAccelerator)
                await EvictOtherAcceleratorWorkersAsync(job.Analyzer).ConfigureAwait(false);
            var runtime = await GetRuntimeAsync(job.Analyzer, cancellationToken).ConfigureAwait(false);
            progress?.Report(0.05);
            var message = ProtoWriter.Create()
                .String(1, job.Id.ToString("N"))
                .String(2, job.Analyzer)
                .String(3, job.AnalyzerVersion)
                .RepeatedString(4, job.Assets.Select(asset => asset.Id))
                .Bytes(5, JsonSerializer.SerializeToUtf8Bytes(job.Parameters))
                .String(6, ProtocolVersion)
                .ToArray();
            var reply = await CallAsync(runtime, "RunJob", message, cancellationToken).ConfigureAwait(false);
            progress?.Report(1);
            var reader = new ProtoReader(reply);
            var success = reader.Bool(1);
            return new WorkerJobResult(
                success,
                reader.Bytes(4) ?? "{}"u8.ToArray(),
                reader.Strings(5).ToArray(),
                reader.String(2),
                reader.String(3));
        }
        finally
        {
            if (usesAccelerator) _acceleratorLease.Release();
        }
    }

    public static bool UsesAccelerator(string analyzer) => AcceleratorAnalyzers.Contains(analyzer);

    private async Task EvictOtherAcceleratorWorkersAsync(string requestedAnalyzer)
    {
        foreach (var pair in _workers.ToArray())
        {
            if (pair.Key.Equals(requestedAnalyzer, StringComparison.OrdinalIgnoreCase) ||
                !UsesAccelerator(pair.Key))
                continue;
            if (_workers.TryRemove(pair.Key, out var runtime))
                await runtime.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<int> CountTokensAsync(
        string model,
        string text,
        CancellationToken cancellationToken)
    {
        var runtime = await GetRuntimeAsync("director", cancellationToken).ConfigureAwait(false);
        var message = ProtoWriter.Create()
            .String(1, model)
            .String(2, text)
            .String(3, ProtocolVersion)
            .ToArray();
        var reply = await CallAsync(runtime, "CountTokens", message, cancellationToken).ConfigureAwait(false);
        var count = new ProtoReader(reply).Int32(1);
        if (count <= 0) throw new WorkerUnavailableException("Director worker returned an invalid tokenizer count.");
        return count;
    }

    public async Task ReleaseAcceleratorAsync(CancellationToken cancellationToken)
    {
        await _acceleratorLease.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var pair in _workers.ToArray())
            {
                if (!UsesAccelerator(pair.Key)) continue;
                if (_workers.TryRemove(pair.Key, out var runtime))
                    await runtime.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _acceleratorLease.Release();
        }
    }

    private async Task<WorkerRuntime> GetRuntimeAsync(string analyzer, CancellationToken cancellationToken)
    {
        if (!WorkerAnalyzers.Allowed.Contains(analyzer))
            throw new WorkerUnavailableException($"Analyzer '{analyzer}' is not allowed.");
        if (_workers.TryGetValue(analyzer, out var existing) && !existing.Process.HasExited)
            return existing;

        var directory = Path.GetFullPath(Path.Combine(_workersRoot, analyzer));
        if (!directory.StartsWith(_workersRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new WorkerUnavailableException("Worker path escaped the configured root.");
        var manifestPath = Path.Combine(directory, "worker-manifest.json");
        if (!File.Exists(manifestPath))
            throw new WorkerUnavailableException($"Worker manifest for '{analyzer}' was not found.");
        var manifest = JsonSerializer.Deserialize<WorkerManifest>(
            await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new WorkerUnavailableException($"Worker manifest for '{analyzer}' is invalid.");
        if (!manifest.Analyzer.Equals(analyzer, StringComparison.OrdinalIgnoreCase) ||
            manifest.ProtocolVersion != ProtocolVersion ||
            manifest.Port is < 1024 or > 65535)
            throw new WorkerUnavailableException($"Worker manifest for '{analyzer}' is incompatible with protocol v2.");
        var executable = Path.GetFullPath(Path.Combine(_workersRoot, manifest.Executable));
        if (!executable.StartsWith(_workersRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(executable))
            throw new WorkerUnavailableException($"Worker executable for '{analyzer}' is unavailable.");
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = _workersRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        // Worker manifests keep a port for protocol compatibility, but Windows can
        // reserve whole dynamic-port ranges after Hyper-V/WSL starts. Binding the
        // manifest port would then terminate every worker before gRPC is available.
        // Pick a currently available loopback port for each supervised process.
        var runtimePort = GetAvailableLoopbackPort();
        foreach (var argument in manifest.Arguments ?? []) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--grpc-port");
        start.ArgumentList.Add(runtimePort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--protocol-version");
        start.ArgumentList.Add(ProtocolVersion);
        start.ArgumentList.Add("--data-root");
        start.ArgumentList.Add(_dataRoot);
        var process = Process.Start(start)
            ?? throw new WorkerUnavailableException($"Worker '{analyzer}' could not be started.");
        var handler = new SocketsHttpHandler { UseProxy = false };
        var client = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{runtimePort}/"),
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        var runtime = new WorkerRuntime(process, client);
        if (_workers.TryGetValue(analyzer, out var previous))
            await previous.DisposeAsync().ConfigureAwait(false);
        _workers[analyzer] = runtime;
        return runtime;
    }

    private static int GetAvailableLoopbackPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<byte[]> CallAsync(
        WorkerRuntime runtime,
        string method,
        byte[] protobuf,
        CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (runtime.Process.HasExited)
                throw new WorkerUnavailableException($"Worker exited with code {runtime.Process.ExitCode}.");
            try
            {
                var framed = new byte[protobuf.Length + 5];
                BinaryPrimitives.WriteInt32BigEndian(framed.AsSpan(1, 4), protobuf.Length);
                protobuf.CopyTo(framed, 5);
                using var request = new HttpRequestMessage(
                    HttpMethod.Post, $"kadr.worker.v2.Worker/{method}")
                {
                    Content = new ByteArrayContent(framed),
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact
                };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
                request.Headers.TryAddWithoutValidation("TE", "trailers");
                using var response = await runtime.Client.SendAsync(
                    request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
                var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"Worker gRPC returned HTTP {(int)response.StatusCode}.");
                if (payload.Length < 5 || payload[0] != 0)
                    throw new WorkerUnavailableException("Worker returned an invalid gRPC frame.");
                var length = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(1, 4));
                if (length < 0 || length != payload.Length - 5)
                    throw new WorkerUnavailableException("Worker returned a truncated gRPC frame.");
                return payload[5..];
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                last = exception;
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
        throw new WorkerUnavailableException("Worker did not open its loopback gRPC endpoint.", last);
    }

    public void Dispose()
    {
        foreach (var runtime in _workers.Values) runtime.Dispose();
        _workers.Clear();
        _acceleratorLease.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var runtime in _workers.Values)
            await runtime.DisposeAsync().ConfigureAwait(false);
        _workers.Clear();
        _acceleratorLease.Dispose();
    }

    private sealed class WorkerRuntime(Process process, HttpClient client) : IDisposable, IAsyncDisposable
    {
        public Process Process { get; } = process;
        public HttpClient Client { get; } = client;

        public void Dispose()
        {
            try
            {
                if (!Process.HasExited)
                    Process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The OS may already have reaped the supervised worker.
            }
            Client.Dispose();
            Process.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!Process.HasExited)
                {
                    // Ask the worker to unload its owned llama-server first.
                    // Force-killing Python alone can orphan the native child on
                    // Windows, leaving Vision and Planner resident together.
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await LoopbackGrpcWorkerGateway.CallAsync(
                        this, "Shutdown", [], timeout.Token).ConfigureAwait(false);
                    if (!Process.HasExited)
                        Process.Kill(entireProcessTree: true);
                    await Process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                try
                {
                    if (!Process.HasExited) Process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // The OS may already have reaped the supervised worker.
                }
            }
            finally
            {
                Client.Dispose();
                Process.Dispose();
            }
        }
    }
}

public sealed class WorkerUnavailableException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);

internal sealed class ProtoWriter
{
    private readonly MemoryStream _stream = new();
    public static ProtoWriter Create() => new();
    public ProtoWriter String(int field, string? value) => Bytes(field, Encoding.UTF8.GetBytes(value ?? string.Empty));
    public ProtoWriter RepeatedString(int field, IEnumerable<string> values)
    {
        foreach (var value in values) String(field, value);
        return this;
    }
    public ProtoWriter Bytes(int field, byte[] value)
    {
        Varint((ulong)((field << 3) | 2));
        Varint((ulong)value.Length);
        _stream.Write(value);
        return this;
    }
    private void Varint(ulong value)
    {
        while (value >= 0x80) { _stream.WriteByte((byte)(value | 0x80)); value >>= 7; }
        _stream.WriteByte((byte)value);
    }
    public byte[] ToArray() => _stream.ToArray();
}

internal sealed class ProtoReader(byte[] payload)
{
    private readonly Dictionary<int, List<byte[]>> _fields = Parse(payload);
    public string? String(int field) => _fields.TryGetValue(field, out var values) ? Encoding.UTF8.GetString(values[^1]) : null;
    public IEnumerable<string> Strings(int field) => _fields.TryGetValue(field, out var values) ? values.Select(Encoding.UTF8.GetString) : [];
    public byte[]? Bytes(int field) => _fields.TryGetValue(field, out var values) ? values[^1] : null;
    public bool Bool(int field) => Int32(field) != 0;
    public int Int32(int field)
    {
        if (!_fields.TryGetValue(field, out var values)) return 0;
        var offset = 0;
        return checked((int)ReadVarint(values[^1], ref offset));
    }

    private static Dictionary<int, List<byte[]>> Parse(byte[] data)
    {
        var result = new Dictionary<int, List<byte[]>>();
        var offset = 0;
        while (offset < data.Length)
        {
            var tag = ReadVarint(data, ref offset);
            var field = (int)(tag >> 3);
            var wire = (int)(tag & 7);
            byte[] value;
            if (wire == 2)
            {
                var length = checked((int)ReadVarint(data, ref offset));
                if (length < 0 || offset + length > data.Length) throw new WorkerUnavailableException("Invalid protobuf length.");
                value = data.AsSpan(offset, length).ToArray();
                offset += length;
            }
            else if (wire == 0)
            {
                var start = offset;
                ReadVarint(data, ref offset);
                value = data.AsSpan(start, offset - start).ToArray();
            }
            else throw new WorkerUnavailableException("Unsupported protobuf wire type.");
            if (!result.TryGetValue(field, out var values)) result[field] = values = [];
            values.Add(value);
        }
        return result;
    }

    private static ulong ReadVarint(byte[] data, ref int offset)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64 && offset < data.Length; shift += 7)
        {
            var current = data[offset++];
            value |= (ulong)(current & 0x7f) << shift;
            if ((current & 0x80) == 0) return value;
        }
        throw new WorkerUnavailableException("Invalid protobuf varint.");
    }
}
