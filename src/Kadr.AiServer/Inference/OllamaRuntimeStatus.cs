namespace KadrStudio.AiServer.Inference;

public enum OllamaRuntimeState
{
    Starting,
    Ready,
    Failed
}

public sealed record OllamaRuntimeStatus(
    OllamaRuntimeState State,
    string Message,
    DateTimeOffset UpdatedAt);
