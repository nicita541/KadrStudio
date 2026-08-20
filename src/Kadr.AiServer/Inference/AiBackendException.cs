namespace KadrStudio.AiServer.Inference;

public sealed class AiBackendException : Exception
{
    public AiBackendException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
