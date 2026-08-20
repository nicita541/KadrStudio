using System.Text.Json.Nodes;

namespace KadrStudio.AiServer.Inference;

public static class OllamaRequestRewriter
{
    public static JsonObject RewriteChatRequest(JsonObject request, string backendModel)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(backendModel);

        var copy = request.DeepClone() as JsonObject
                   ?? throw new InvalidOperationException("Chat request must be a JSON object.");

        if (copy["stream"] is JsonValue streamValue &&
            streamValue.TryGetValue<bool>(out var stream) &&
            stream)
        {
            throw new BadHttpRequestException(
                "Streaming Ollama responses are intentionally disabled by Kadr AI Server v1.");
        }

        copy["model"] = backendModel;
        copy["stream"] = false;
        return copy;
    }

    public static JsonObject MaskChatResponse(JsonObject response, string publicModelAlias)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicModelAlias);

        var copy = response.DeepClone() as JsonObject
                   ?? throw new InvalidOperationException("Chat response must be a JSON object.");
        copy["model"] = publicModelAlias;
        return copy;
    }
}
