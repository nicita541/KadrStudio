using System.Text.Json;

namespace KadrStudio.AiServer.Api;

public sealed record StructuredInferenceRequest(
    JsonElement Schema,
    string SystemPrompt,
    string UserPrompt,
    string[]? Images = null,
    double Temperature = 0,
    int ContextTokens = 8192,
    int MaxTokens = 2048,
    string? Model = null,
    bool Think = false,
    int? ReasoningTokens = null);

public sealed record InferenceResponse(
    string Content,
    string? DoneReason,
    int EvalCount,
    int ReasoningEvalCount,
    int AttemptCount);

public sealed record InferenceErrorResponse(
    string ErrorCode,
    string Error,
    string? DoneReason,
    int EvalCount,
    int AttemptCount);
