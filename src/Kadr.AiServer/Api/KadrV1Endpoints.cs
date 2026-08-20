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
                    models = new[]
                    {
                        new
                        {
                            id = options.PublicModelAlias,
                            capabilities = new[] { "structured-output", "vision" },
                            managed = true
                        }
                    }
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
            OllamaRuntime runtime,
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

            var imageNodes = images
                .Select(image => (JsonNode?)JsonValue.Create(image))
                .ToArray();
            var userMessage = new JsonObject
            {
                ["role"] = "user",
                ["content"] = payload.UserPrompt
            };
            if (imageNodes.Length > 0)
            {
                userMessage["images"] = new JsonArray(imageNodes);
            }

            var modelRequest = new JsonObject
            {
                ["stream"] = false,
                ["think"] = false,
                ["format"] = JsonNode.Parse(payload.Schema.GetRawText()),
                ["messages"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "system",
                        ["content"] = payload.SystemPrompt
                    },
                    userMessage
                },
                ["options"] = new JsonObject
                {
                    ["temperature"] = Math.Clamp(payload.Temperature, 0, 2),
                    ["num_ctx"] = Math.Clamp(payload.ContextTokens, 2048, 65536),
                    ["num_predict"] = Math.Clamp(payload.MaxTokens, 32, 8192)
                }
            };

            return await RunInferenceAsync(runtime, modelRequest, cancellationToken)
                .ConfigureAwait(false);
        });
    }

    private static async Task<IResult> RunInferenceAsync(
        OllamaRuntime runtime,
        JsonObject request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await runtime.ChatAsync(request, cancellationToken).ConfigureAwait(false);
            var content = response["message"]?["content"]?.GetValue<string>() ?? string.Empty;
            var doneReason = response["done_reason"]?.GetValue<string>();
            var evalCount = response["eval_count"] is JsonValue evalValue &&
                            evalValue.TryGetValue<int>(out var parsedEvalCount)
                ? parsedEvalCount
                : 0;

            if (string.IsNullOrWhiteSpace(content))
            {
                return Results.Json(
                    new { error = "AI model returned an empty response." },
                    statusCode: StatusCodes.Status502BadGateway);
            }

            return Results.Json(new InferenceResponse(content, doneReason, evalCount));
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
