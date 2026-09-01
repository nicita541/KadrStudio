using System.Text.Json;
using System.Text.Json.Nodes;

namespace KadrStudio.AiServer.Infrastructure;

public static class JsonRequestReader
{
    public static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<JsonObject> ReadObjectAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        JsonNode? node;
        try
        {
            node = await JsonNode.ParseAsync(
                request.Body,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new BadHttpRequestException("Request body must contain valid JSON.", exception);
        }

        return node as JsonObject
               ?? throw new BadHttpRequestException("Request body must be a JSON object.");
    }
}
