namespace KadrStudio.Application.Automation.Agent.Tools;

public sealed class AgentToolInputException : ArgumentException
{
    public AgentToolInputException(string message)
        : base(message)
    {
    }
}

public sealed class AgentToolRejectedException : InvalidOperationException
{
    public AgentToolRejectedException(
        string errorCode,
        string message)
        : base(message)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            throw new ArgumentException(
                "Error code cannot be empty.",
                nameof(errorCode));
        }

        ErrorCode = errorCode.Trim();
    }

    public string ErrorCode { get; }
}
