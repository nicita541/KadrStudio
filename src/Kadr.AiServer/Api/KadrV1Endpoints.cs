using System.Text.Json;
using System.Text.Json.Nodes;
using KadrStudio.AiServer.Configuration;
using KadrStudio.AiServer.Infrastructure;
using KadrStudio.AiServer.Inference;

namespace KadrStudio.AiServer.Api;

public static class KadrV1Endpoints
{
    public static void MapKadrV1Endpoints(this WebApplication app)
    {
        app.MapGet("/v1/models", async (
            AiServerOptions options,
            OllamaRuntime runtime,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
                return Results.Json(new
                {
                    models = options.ConfiguredModels.Select(model => new
                    {
                        id = model.PublicAlias,
                        role = model.Role,
                        capabilities = model.RequiresVision
                            ? new[] { "structured-output", "vision" }
                            : new[] { "structured-output", "planning", "thinking" },
                        managed = true
                    }).ToArray()
                });
            }
            catch (Exception exception) when (exception is AiBackendException or FileNotFoundException)
            {
                return BackendUnavailable(exception);
            }
        });

        app.MapPost("/v1/inference/structured", async (
            HttpRequest request,
            AiServerOptions options,
            StructuredInferencePipeline pipeline,
            CancellationToken cancellationToken) =>
        {
            StructuredInferenceRequest? payload;
            try
            {
                payload = await JsonSerializer.DeserializeAsync<StructuredInferenceRequest>(
                    request.Body,
                    JsonRequestReader.WebJsonOptions,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException exception)
            {
                return BadRequest($"Invalid structured inference JSON: {exception.Message}");
            }

            if (payload is null)
            {
                return BadRequest("Structured inference request body is required.");
            }

            if (payload.Schema.ValueKind != JsonValueKind.Object)
            {
                return BadRequest("schema must be a JSON object.");
            }

            AiServerModelRoute model;
            try
            {
                model = options.ResolveModel(payload.Model);
            }
            catch (InvalidOperationException exception)
            {
                return BadRequest(exception.Message);
            }

            if (!ValidatePrompt(payload.SystemPrompt, options, out var promptError) ||
                !ValidatePrompt(payload.UserPrompt, options, out promptError))
            {
                return BadRequest(promptError!);
            }

            var images = payload.Images ?? [];
            if (images.Length > options.MaxImageCount)
            {
                return BadRequest(
                    $"Too many images. Maximum is {options.MaxImageCount} per request.");
            }

            if (images.Any(string.IsNullOrWhiteSpace))
            {
                return BadRequest("Image payloads cannot be empty.");
            }

            if (images.Length > 0 && !model.RequiresVision)
            {
                return BadRequest(
                    $"Model '{model.PublicAlias}' does not accept images. Use '{options.PublicModelAlias}'.");
            }

            return await RunInferenceAsync(pipeline, payload, model, cancellationToken)
                .ConfigureAwait(false);
        });
    }

    private static async Task<IResult> RunInferenceAsync(
        StructuredInferencePipeline pipeline,
        StructuredInferenceRequest payload,
        AiServerModelRoute model,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await pipeline.RunAsync(payload, model, cancellationToken)
                .ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                return Results.Json(
                    new InferenceErrorResponse(
                        result.ErrorCode!,
                        result.Error!,
                        result.DoneReason,
                        result.EvalCount,
                        result.AttemptCount),
                    statusCode: result.ErrorCode == "invalid_context_budget"
                        ? StatusCodes.Status400BadRequest
                        : StatusCodes.Status502BadGateway);
            }

            return Results.Json(new InferenceResponse(
                result.Content!,
                result.DoneReason,
                result.EvalCount,
                result.ReasoningEvalCount,
                result.AttemptCount));
        }
        catch (BadHttpRequestException exception)
        {
            return BadRequest(exception.Message);
        }
        catch (Exception exception) when (exception is AiBackendException or FileNotFoundException)
        {
            return BackendUnavailable(exception);
        }
    }

    private static bool ValidatePrompt(
        string? prompt,
        AiServerOptions options,
        out string? error)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            error = "Prompt cannot be empty.";
            return false;
        }

        if (prompt.Length > options.MaxPromptCharacters)
        {
            error = $"Prompt exceeds {options.MaxPromptCharacters} characters.";
            return false;
        }

        error = null;
        return true;
    }

    private static IResult BadRequest(string message)
        => Results.Json(
            new { error = message },
            statusCode: StatusCodes.Status400BadRequest);

    private static IResult BackendUnavailable(Exception exception)
        => Results.Json(
            new
            {
                error = "Kadr AI backend is unavailable.",
                details = exception.Message
            },
            statusCode: StatusCodes.Status503ServiceUnavailable);
}
