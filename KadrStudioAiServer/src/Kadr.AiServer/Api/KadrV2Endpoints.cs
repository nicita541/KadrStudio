using System.Globalization;
using System.Text.Json;
using KadrStudio.AiServer.Infrastructure;
using KadrStudio.AiServer.Inference;
using KadrStudio.AiServer.Jobs;
using KadrStudio.AiServer.Storage;
using KadrStudio.AiServer.Workers;

namespace KadrStudio.AiServer.Api;

public static class KadrV2Endpoints
{
    public static void MapKadrV2Endpoints(this WebApplication app)
    {
        app.MapPost("/v2/assets", async (
            HttpRequest request,
            ContentAddressedAssetStore assets,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var hash = RequiredHeader(request, "X-Kadr-Content-Sha256");
                var total = ParseLongHeader(request, "X-Kadr-Total-Length");
                var offset = ParseLongHeader(request, "X-Kadr-Offset");
                var mediaType = request.ContentType ?? "application/octet-stream";
                var asset = await assets.AppendAsync(
                    hash, total, offset, mediaType, request.Body, cancellationToken).ConfigureAwait(false);
                return Results.Json(
                    new AssetUploadResponse(
                        asset.Id, asset.Length, total, asset.IsComplete, asset.MediaType),
                    statusCode: asset.IsComplete ? StatusCodes.Status201Created : StatusCodes.Status202Accepted);
            }
            catch (AssetUploadException exception)
            {
                return Error(exception.ErrorCode, exception.Message, StatusCodes.Status400BadRequest);
            }
            catch (BadHttpRequestException exception)
            {
                return Error("invalid_upload", exception.Message, StatusCodes.Status400BadRequest);
            }
        });

        app.MapGet("/v2/assets/{id}", (
            string id,
            long totalBytes,
            ContentAddressedAssetStore assets) =>
        {
            try
            {
                var asset = assets.FindUpload(id);
                return asset is null
                    ? Error("asset_not_found", "Analysis asset upload was not found.", StatusCodes.Status404NotFound)
                    : Results.Json(new AssetUploadResponse(
                        asset.Id, asset.Length, totalBytes, asset.IsComplete, asset.MediaType));
            }
            catch (AssetUploadException exception)
            {
                return Error(exception.ErrorCode, exception.Message, StatusCodes.Status400BadRequest);
            }
        });

        app.MapPost("/v2/jobs", async (
            HttpRequest request,
            AnalyzerJobService jobs,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var payload = await JsonSerializer.DeserializeAsync<AnalyzerJobRequest>(
                    request.Body, JsonRequestReader.WebJsonOptions, cancellationToken).ConfigureAwait(false)
                    ?? throw new AnalyzerJobException("request_required", "Job request body is required.");
                var job = await jobs.CreateAsync(payload, cancellationToken).ConfigureAwait(false);
                return Results.Json(job, statusCode: StatusCodes.Status202Accepted);
            }
            catch (JsonException exception)
            {
                return Error("invalid_json", exception.Message, StatusCodes.Status400BadRequest);
            }
            catch (AnalyzerJobException exception)
            {
                return Error(exception.ErrorCode, exception.Message, StatusCodes.Status400BadRequest);
            }
        });

        app.MapGet("/v2/jobs/{id:guid}", (Guid id, AnalyzerJobService jobs) =>
            jobs.Find(id) is { } job
                ? Results.Json(job)
                : Error("job_not_found", "Analyzer job was not found.", StatusCodes.Status404NotFound));

        app.MapDelete("/v2/jobs/{id:guid}", async (
            Guid id,
            AnalyzerJobService jobs,
            CancellationToken cancellationToken) =>
        {
            var job = await jobs.CancelAsync(id, cancellationToken).ConfigureAwait(false);
            return job is null
                ? Error("job_not_found", "Analyzer job was not found.", StatusCodes.Status404NotFound)
                : Results.Json(job);
        });

        app.MapPost("/v2/accelerator/release", async (
            IWorkerGateway workers,
            CancellationToken cancellationToken) =>
        {
            await workers.ReleaseAcceleratorAsync(cancellationToken).ConfigureAwait(false);
            return Results.Json(new { released = true });
        });

        app.MapGet("/v2/jobs/{id:guid}/events", async (
            Guid id,
            AnalyzerJobService jobs,
            HttpResponse response,
            CancellationToken cancellationToken) =>
        {
            response.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-cache";
            DateTimeOffset? last = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                var job = jobs.Find(id);
                if (job is null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                if (last != job.UpdatedAt)
                {
                    await response.WriteAsync("event: job\n", cancellationToken).ConfigureAwait(false);
                    await response.WriteAsync(
                        "data: " + JsonSerializer.Serialize(job, JsonRequestReader.WebJsonOptions) + "\n\n",
                        cancellationToken).ConfigureAwait(false);
                    await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                    last = job.UpdatedAt;
                }
                if (job.State is AnalyzerJobState.Succeeded or AnalyzerJobState.Failed or AnalyzerJobState.Cancelled)
                    return;
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        });

        app.MapGet("/v2/artifacts/{id}", (string id, ContentAddressedArtifactStore artifacts) =>
            artifacts.Find(id) is { } artifact
                ? Results.File(
                    artifact.Path,
                    artifact.MediaType,
                    fileDownloadName: artifact.MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                        ? artifact.Id + artifact.Extension
                        : null,
                    enableRangeProcessing: true)
                : Error("artifact_not_found", "Artifact was not found.", StatusCodes.Status404NotFound));

        app.MapPost("/v2/reason/structured", async (
            HttpRequest request,
            RoleStructuredReasoningService reasoning,
            CancellationToken cancellationToken) =>
        {
            RoleStructuredReasoningRequest? payload;
            try
            {
                payload = await JsonSerializer.DeserializeAsync<RoleStructuredReasoningRequest>(
                    request.Body, JsonRequestReader.WebJsonOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException exception)
            {
                return Error("invalid_json", exception.Message, StatusCodes.Status400BadRequest);
            }
            if (payload is null)
                return Error("request_required", "Reasoning request body is required.", StatusCodes.Status400BadRequest);
            try
            {
                var result = await reasoning.RunAsync(payload, cancellationToken).ConfigureAwait(false);
                return result.IsSuccess
                    ? Results.Json(new RoleStructuredReasoningResponse(
                        result.Content!, payload.Role, result.InputTokens,
                        result.InputBudgetTokens, result.OutputBudgetTokens,
                        result.ReserveTokens, result.AttemptCount))
                    : Error(
                        result.ErrorCode!, result.Error!,
                        result.ErrorCode is "invalid_request" or "invalid_context_budget" or "invalid_model"
                            ? StatusCodes.Status400BadRequest
                            : StatusCodes.Status503ServiceUnavailable,
                        new
                        {
                            result.InputTokens,
                            result.InputBudgetTokens,
                            result.OutputBudgetTokens,
                            result.ReserveTokens
                        });
            }
            catch (Exception exception) when (exception is FileNotFoundException or WorkerUnavailableException)
            {
                return Error("backend_unavailable", exception.Message, StatusCodes.Status503ServiceUnavailable);
            }
        });
    }

    private static string RequiredHeader(HttpRequest request, string name)
        => request.Headers.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.ToString()
            : throw new BadHttpRequestException($"Header '{name}' is required.");

    private static long ParseLongHeader(HttpRequest request, string name)
        => long.TryParse(RequiredHeader(request, name), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new BadHttpRequestException($"Header '{name}' must be an integer.");

    private static IResult Error(string code, string message, int status, object? details = null)
        => Results.Json(new { errorCode = code, error = message, details }, statusCode: status);
}
