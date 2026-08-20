using System.Text.Json.Nodes;
using KadrStudio.AiServer.Configuration;

namespace KadrStudio.AiServer.Inference;

public interface IInferenceChatRuntime
{
    Task<JsonObject> ChatAsync(
        AiServerModelRoute model,
        JsonObject publicRequest,
        CancellationToken cancellationToken);
}
