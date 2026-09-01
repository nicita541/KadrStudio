using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Immutable;
using System.Diagnostics;
using KadrStudio.Application.Automation.Editorial;

namespace KadrStudio.Services.Editorial;

public sealed class AiServerV2Client(
    AiServerConnection aiServer,
    IEditorialTelemetrySink? telemetry = null,
    bool? requireProduction = null)
{
    private readonly HttpClient _client = aiServer.AuthorizedClient;
    private readonly IEditorialTelemetrySink _telemetry = telemetry ?? NullEditorialTelemetrySink.Instance;
    private readonly bool _requireProduction = requireProduction ??
        !string.Equals(
            Environment.GetEnvironmentVariable("KADR_STUDIO_AI_ALLOW_DEV_MODELS"),
            "1",
            StringComparison.Ordinal);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<string> UploadAssetAsync(
        string path,
        string mediaType,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Analysis proxy was not found.", path);
        var uploadStarted = Stopwatch.GetTimestamp();
        var requestCount = 1L;
        var transferredBytes = 0L;
        var info = new FileInfo(path);
        string hash;
        var hashStarted = Stopwatch.GetTimestamp();
        await using (var hashInput = File.OpenRead(path))
            hash = Convert.ToHexString(await SHA256.HashDataAsync(hashInput, cancellationToken).ConfigureAwait(false))
                .ToLowerInvariant();
        RecordTransport("asset_hash_duration_ms", Stopwatch.GetElapsedTime(hashStarted).TotalMilliseconds,
            ("media_type", mediaType));
        const int chunkBytes = 8 * 1024 * 1024;
        await using var input = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            chunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[chunkBytes];
        long offset = 0;
        using (var statusResponse = await _client.GetAsync(
                   $"v2/assets/{hash}?totalBytes={info.Length}", cancellationToken).ConfigureAwait(false))
        {
            if (statusResponse.IsSuccessStatusCode)
            {
                var status = await statusResponse.Content.ReadFromJsonAsync<AssetUploadStatus>(
                    Json, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("AI server returned an empty upload status.");
                if (status.IsComplete)
                {
                    if (status.ReceivedBytes != info.Length)
                        throw new InvalidDataException("AI server asset length differs from the local content hash input.");
                    progress?.Report(1);
                    RecordUploadMetrics(info.Length, transferredBytes, requestCount, uploadStarted, cacheHit: true, mediaType);
                    return hash;
                }
                offset = status.ReceivedBytes;
                if (offset < 0 || offset > info.Length)
                    throw new InvalidDataException("AI server returned an invalid resumable upload offset.");
                input.Position = offset;
                progress?.Report(offset / (double)info.Length);
            }
            else if (statusResponse.StatusCode != HttpStatusCode.NotFound)
            {
                await EnsureSuccessAsync(statusResponse, cancellationToken).ConfigureAwait(false);
            }
        }
        while (offset < info.Length)
        {
            var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("Analysis proxy ended before its declared length.");
            using var content = new ByteArrayContent(buffer, 0, count);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
            content.Headers.Add("X-Kadr-Content-Sha256", hash);
            content.Headers.Add("X-Kadr-Total-Length", info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            content.Headers.Add("X-Kadr-Offset", offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var response = await _client.PostAsync("v2/assets", content, cancellationToken).ConfigureAwait(false);
            requestCount++;
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            offset += count;
            transferredBytes += count;
            progress?.Report(offset / (double)info.Length);
        }
        RecordUploadMetrics(info.Length, transferredBytes, requestCount, uploadStarted, cacheHit: false, mediaType);
        return hash;
    }

    public async Task<Guid> StartJobAsync(
        string analyzer,
        string analyzerVersion,
        IReadOnlyList<string> assetIds,
        object parameters,
        CancellationToken cancellationToken)
    {
        using var response = await _client.PostAsJsonAsync(
            "v2/jobs",
            new
            {
                analyzer,
                analyzerVersion,
                assetIds,
                parameters,
                requireProduction = _requireProduction
            },
            Json,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var job = await response.Content.ReadFromJsonAsync<AiServerJob>(Json, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("AI server returned an empty job response.");
        return job.Id;
    }

    public async Task<AiServerJob> WaitForJobAsync(
        Guid id,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var requests = 0L;
        while (true)
        {
            using var response = await _client.GetAsync($"v2/jobs/{id:D}", cancellationToken).ConfigureAwait(false);
            requests++;
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            var job = await response.Content.ReadFromJsonAsync<AiServerJob>(Json, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("AI server returned an empty job response.");
            progress?.Report(job.Progress);
            if (job.State.Equals("succeeded", StringComparison.OrdinalIgnoreCase))
            {
                RecordTransport("job_wait_duration_ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    ("analyzer", job.Analyzer));
                RecordTransport("job_poll_request_count", requests, ("analyzer", job.Analyzer));
                return job;
            }
            if (job.State.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
                job.State.Equals("cancelled", StringComparison.OrdinalIgnoreCase))
                throw new AiServerV2Exception(job.ErrorCode ?? "analyzer_failed", job.Error ?? job.Message);
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<JsonDocument> GetArtifactAsync(string id, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync($"v2/artifacts/{id}", cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task DownloadArtifactAsync(
        string id,
        string destinationPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        using var response = await _client.GetAsync(
            $"v2/artifacts/{id}", HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var total = response.Content.Headers.ContentLength;
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("Artifact destination has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[1024 * 1024];
            long received = 0;
            while (true)
            {
                var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                received += count;
                if (total is > 0) progress?.Report(Math.Clamp(received / (double)total.Value, 0, 1));
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destinationPath, overwrite: true);
            progress?.Report(1);
            RecordTransport("artifact_download_duration_ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            RecordTransport("artifact_download_bytes", received);
            RecordTransport("artifact_download_http_requests", 1);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    public async Task CancelJobAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await _client.DeleteAsync($"v2/jobs/{id:D}", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound)
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ReasonAsync(
        string role,
        JsonElement schema,
        JsonElement context,
        string instruction,
        CancellationToken cancellationToken)
    {
        using var response = await _client.PostAsJsonAsync(
            "v2/reason/structured",
            new
            {
                role,
                schema,
                context,
                instruction,
                contextWindowTokens = 32768,
                maxTokens = 8192,
                model = AiServerConnection.DefaultPlannerModelAlias,
                profile = ResolveProfile(context),
                requireProduction = _requireProduction
            },
            Json,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;
        var inputTokens = root.GetProperty("inputTokens").GetInt32();
        var inputBudget = root.GetProperty("inputBudgetTokens").GetInt32();
        try
        {
            _telemetry.Record(new EditorialTelemetryEvent(
                DateTimeOffset.UtcNow,
                Guid.Empty,
                "context_utilization",
                inputBudget == 0 ? 0 : inputTokens / (double)inputBudget,
                ImmutableDictionary<string, string>.Empty
                    .Add("role", role)
                    .Add("attempt_count", root.GetProperty("attemptCount").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }
        catch
        {
            // Local telemetry must never make structured reasoning fail.
        }
        return root.GetProperty("content").GetString()
               ?? throw new InvalidDataException("AI server returned empty structured reasoning content.");
    }

    private static string ResolveProfile(JsonElement context)
    {
        if (context.TryGetProperty("profile", out var profile))
        {
            if (profile.ValueKind == JsonValueKind.String) return profile.GetString() ?? string.Empty;
            if (profile.ValueKind == JsonValueKind.Object && profile.TryGetProperty("id", out var id))
                return id.GetString() ?? string.Empty;
        }
        if (context.TryGetProperty("brief", out var brief) && brief.ValueKind == JsonValueKind.Object &&
            brief.TryGetProperty("profile", out var nestedProfile))
        {
            if (nestedProfile.ValueKind == JsonValueKind.String) return nestedProfile.GetString() ?? string.Empty;
            if (nestedProfile.ValueKind == JsonValueKind.Object && nestedProfile.TryGetProperty("id", out var id))
                return id.GetString() ?? string.Empty;
        }
        return "profile-selection";
    }

    private void RecordUploadMetrics(
        long assetBytes,
        long transferredBytes,
        long requestCount,
        long started,
        bool cacheHit,
        string mediaType)
    {
        var dimensions = new[]
        {
            ("media_type", mediaType),
            ("cache_hit", cacheHit ? "true" : "false")
        };
        RecordTransport("asset_upload_duration_ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds, dimensions);
        RecordTransport("asset_logical_bytes", assetBytes, dimensions);
        RecordTransport("asset_transferred_bytes", transferredBytes, dimensions);
        RecordTransport("asset_upload_http_requests", requestCount, dimensions);
        RecordTransport("asset_duplicate_transferred_bytes", 0, dimensions);
    }

    private void RecordTransport(
        string metric,
        double value,
        params (string Key, string Value)[] dimensions)
    {
        try
        {
            _telemetry.Record(new EditorialTelemetryEvent(
                DateTimeOffset.UtcNow,
                Guid.Empty,
                metric,
                value,
                dimensions.ToImmutableDictionary(item => item.Key, item => item.Value)));
        }
        catch
        {
            // Performance telemetry must never fail an HTTP operation.
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        string code = "ai_server_v2_error";
        string message = $"HTTP {(int)response.StatusCode}";
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("errorCode", out var codeElement))
                code = codeElement.GetString() ?? code;
            if (document.RootElement.TryGetProperty("error", out var errorElement))
                message = errorElement.GetString() ?? message;
        }
        catch (JsonException)
        {
            message += ": " + body;
        }
        throw new AiServerV2Exception(code, message, response.StatusCode);
    }
}

public sealed record AiServerJob(
    Guid Id,
    string Analyzer,
    string AnalyzerVersion,
    string State,
    double Progress,
    string Message,
    string[] ArtifactIds,
    string? ErrorCode,
    string? Error);

internal sealed record AssetUploadStatus(
    string AssetId,
    long ReceivedBytes,
    long TotalBytes,
    bool IsComplete,
    string MediaType);

public sealed class AiServerV2Exception(
    string errorCode,
    string message,
    HttpStatusCode? statusCode = null) : InvalidOperationException(message)
{
    public string ErrorCode { get; } = errorCode;
    public HttpStatusCode? StatusCode { get; } = statusCode;
}
