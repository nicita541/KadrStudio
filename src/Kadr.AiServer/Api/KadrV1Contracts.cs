using System.Text.Json;

namespace KadrStudio.AiServer.Api;

public sealed record StructuredInferenceRequest(
    JsonElement Schema,
    string SystemPrompt,
    string UserPrompt,
    string[]? Images = null,
    double Temperature = 0,
    int ContextTokens = 24576,
    int MaxTokens = 2048);

public sealed record InferenceResponse(
    string Content,
    string? DoneReason,
    int EvalCount);
