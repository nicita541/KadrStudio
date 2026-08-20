using System.Text.Json;
using System.Text.Json.Nodes;
using KadrStudio.AiServer.Api;
using KadrStudio.AiServer.Configuration;

namespace KadrStudio.AiServer.Inference;

public sealed class StructuredInferencePipeline
{
    private const int DefaultReasoningTokens = 1280;
    private const int MinReasoningTokens = 256;
    private const int MaxReasoningTokens = 4096;
    private const int SafetyMarginTokens = 512;
    private const int MaxPlannerContextTokens = 32768;
    private const int MaxVisionContextTokens = 8192;

    private readonly IInferenceChatRuntime _runtime;

    public StructuredInferencePipeline(IInferenceChatRuntime runtime)
    {
        _runtime = runtime;
    }

    public async Task<StructuredInferenceResult> RunAsync(
        StructuredInferenceRequest payload,
        AiServerModelRoute model,
        CancellationToken cancellationToken)
    {
        var answerTokens = Math.Clamp(payload.MaxTokens, 32, 8192);
        var reasoningTokens = payload.Think && !model.RequiresVision
            ? Math.Clamp(
                payload.ReasoningTokens ?? DefaultReasoningTokens,
                MinReasoningTokens,
                MaxReasoningTokens)
            : 0;
        var schemaText = payload.Schema.GetRawText();
        var schemaInstruction = BuildSchemaInstruction(schemaText);
        var promptTokens = EstimateTokens(
            payload.SystemPrompt.Length +
            payload.UserPrompt.Length +
            schemaInstruction.Length);
        var maximumContextTokens = model.RequiresVision
            ? MaxVisionContextTokens
            : MaxPlannerContextTokens;
        var requiredContextTokens = checked(
            promptTokens + answerTokens + reasoningTokens + SafetyMarginTokens);
        var contextTokens = SelectContextWindow(
            Math.Max(payload.ContextTokens, requiredContextTokens),
            maximumContextTokens);
        var promptBudget = contextTokens - answerTokens - reasoningTokens - SafetyMarginTokens;
        if (promptTokens > promptBudget)
        {
            return StructuredInferenceResult.Failure(
                "invalid_context_budget",
                $"Prompt requires approximately {promptTokens} tokens, but only {promptBudget} are available after reserved reasoning and answer budgets.",
                null,
                0,
                0);
        }

        // Ollama's num_predict is a single limit shared by hidden thinking and
        // visible content. Giving a thinking model only reasoningTokens makes it
        // stop exactly when it should start writing the JSON answer. Reserve both
        // parts in the context calculation above and pass their sum to Ollama.
        var firstPredictTokens = payload.Think && !model.RequiresVision
            ? checked(reasoningTokens + answerTokens)
            : answerTokens;
        var firstRequest = BuildModelRequest(
            payload,
            schemaInstruction,
            contextTokens,
            firstPredictTokens,
            payload.Think && !model.RequiresVision);

        var firstResponse = await _runtime.ChatAsync(model, firstRequest, cancellationToken)
            .ConfigureAwait(false);
        var first = ReadAttempt(firstResponse);
        if (StructuredOutputValidator.TryValidate(
                first.Content,
                payload.Schema,
                out var normalized,
                out _))
        {
            return StructuredInferenceResult.Success(
                normalized,
                first.DoneReason,
                first.EvalCount,
                payload.Think ? first.EvalCount : 0,
                1);
        }

        var repairRequest = BuildRepairRequest(
            payload,
            schemaInstruction,
            contextTokens,
            answerTokens);
        var repairResponse = await _runtime.ChatAsync(model, repairRequest, cancellationToken)
            .ConfigureAwait(false);
        var repair = ReadAttempt(repairResponse);

        if (StructuredOutputValidator.TryValidate(
                repair.Content,
                payload.Schema,
                out normalized,
                out var repairErrors))
        {
            return StructuredInferenceResult.Success(
                normalized,
                repair.DoneReason,
                repair.EvalCount,
                payload.Think ? first.EvalCount : 0,
                2);
        }

        if (StructuredOutputValidator.TryCloseOpenContainers(
                repair.Content,
                out var structurallyCompleted) &&
            StructuredOutputValidator.TryValidate(
                structurallyCompleted,
                payload.Schema,
                out normalized,
                out _))
        {
            return StructuredInferenceResult.Success(
                normalized,
                repair.DoneReason,
                repair.EvalCount,
                payload.Think ? first.EvalCount : 0,
                2);
        }

        var errorCode = ClassifyFailure(payload.Think, first, repair);
        var safeError = repairErrors.Count == 0
            ? "The model did not return a valid structured response."
            : $"The model output failed JSON Schema validation: {string.Join(" ", repairErrors.Take(3))}";
        return StructuredInferenceResult.Failure(
            errorCode,
            safeError,
            repair.DoneReason ?? first.DoneReason,
            repair.EvalCount,
            2);
    }

    private static JsonObject BuildModelRequest(
        StructuredInferenceRequest payload,
        string schemaInstruction,
        int contextTokens,
        int predictTokens,
        bool useThinking)
    {
        var userMessage = BuildUserMessage(payload.UserPrompt, payload.Images ?? []);
        return new JsonObject
        {
            ["stream"] = false,
            ["think"] = useThinking ? JsonValue.Create("low") : JsonValue.Create(false),
            ["format"] = JsonNode.Parse(payload.Schema.GetRawText()),
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = payload.SystemPrompt + "\n\n" + schemaInstruction
                },
                userMessage
            },
            ["options"] = BuildOptions(payload.Temperature, contextTokens, predictTokens)
        };
    }

    private static JsonObject BuildRepairRequest(
        StructuredInferenceRequest payload,
        string schemaInstruction,
        int contextTokens,
        int answerTokens)
    {
        // Retry from the pinned inputs, not from a growing JSON wrapper containing
        // hidden reasoning and a damaged previous answer. The latter made the retry
        // larger than the original turn and caused one-token length failures.
        var userMessage = BuildUserMessage(payload.UserPrompt, payload.Images ?? []);

        return new JsonObject
        {
            ["stream"] = false,
            ["think"] = false,
            ["format"] = JsonNode.Parse(payload.Schema.GetRawText()),
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "system",
                    ["content"] =
                        "You are a deterministic JSON finalizer. Re-evaluate the pinned request directly. " +
                        "Return exactly one JSON value and no prose, markdown, or hidden reasoning.\n\n" +
                        payload.SystemPrompt + "\n\n" +
                        schemaInstruction
                },
                userMessage
            },
            ["options"] = BuildOptions(payload.Temperature, contextTokens, answerTokens)
        };
    }

    private static JsonObject BuildUserMessage(string content, IReadOnlyList<string> images)
    {
        var message = new JsonObject
        {
            ["role"] = "user",
            ["content"] = content
        };
        if (images.Count > 0)
        {
            message["images"] = new JsonArray(
                images.Select(image => (JsonNode?)JsonValue.Create(image)).ToArray());
        }

        return message;
    }

    private static JsonObject BuildOptions(double temperature, int contextTokens, int predictTokens)
        => new()
        {
            ["temperature"] = Math.Clamp(temperature, 0, 2),
            ["num_ctx"] = contextTokens,
            ["num_predict"] = predictTokens
        };

    private static string BuildSchemaInstruction(string schemaText)
        => "Your response MUST validate against the following JSON Schema. " +
           "Do not add properties that the schema does not allow. Return JSON only.\n" +
           schemaText;

    private static ModelAttempt ReadAttempt(JsonObject response)
    {
        var content = response["message"]?["content"]?.GetValue<string>() ?? string.Empty;
        var thinking = response["message"]?["thinking"]?.GetValue<string>() ?? string.Empty;
        var doneReason = response["done_reason"]?.GetValue<string>();
        var evalCount = response["eval_count"] is JsonValue value &&
                        value.TryGetValue<int>(out var parsed)
            ? parsed
            : 0;
        return new ModelAttempt(content, thinking, doneReason, evalCount);
    }

    private static int SelectContextWindow(int requiredTokens, int maximumTokens)
    {
        foreach (var window in new[] { 2048, 4096, 8192, 16384, 32768 })
        {
            if (window >= requiredTokens && window <= maximumTokens)
            {
                return window;
            }
        }

        return maximumTokens;
    }

    private static int EstimateTokens(int characterCount)
        => Math.Max(1, (int)Math.Ceiling(characterCount / 3d));

    private static string ClassifyFailure(
        bool thinkingEnabled,
        ModelAttempt first,
        ModelAttempt repair)
    {
        if (!string.IsNullOrWhiteSpace(repair.Content))
        {
            return "schema_validation_failed";
        }

        if (thinkingEnabled &&
            string.IsNullOrWhiteSpace(first.Content) &&
            string.Equals(first.DoneReason, "length", StringComparison.OrdinalIgnoreCase))
        {
            return "reasoning_budget_exhausted";
        }

        return "invalid_model_output";
    }

    private sealed record ModelAttempt(
        string Content,
        string Thinking,
        string? DoneReason,
        int EvalCount);
}

public sealed record StructuredInferenceResult(
    bool IsSuccess,
    string? Content,
    string? ErrorCode,
    string? Error,
    string? DoneReason,
    int EvalCount,
    int ReasoningEvalCount,
    int AttemptCount)
{
    public static StructuredInferenceResult Success(
        string content,
        string? doneReason,
        int evalCount,
        int reasoningEvalCount,
        int attemptCount)
        => new(true, content, null, null, doneReason, evalCount, reasoningEvalCount, attemptCount);

    public static StructuredInferenceResult Failure(
        string errorCode,
        string error,
        string? doneReason,
        int evalCount,
        int attemptCount)
        => new(false, null, errorCode, error, doneReason, evalCount, 0, attemptCount);
}
