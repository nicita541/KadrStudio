namespace KadrStudio.Application.Automation.Agent.Runtime;

public sealed record AgentExecutionLoopOptions(
    int MaxModelTurns = 40,
    int MaxObservationCount = 24,
    int MaxObservationContextCharacters = 64_000,
    int MaxConversationMessages = 48,
    int MaxConversationCharacters = 20_000,
    int MaxConsecutiveIdenticalToolCalls = 2,
    int MaxProgressCharacters = 600)
{
    public static AgentExecutionLoopOptions Default { get; } = new();

    public void Validate()
    {
        if (MaxModelTurns <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxModelTurns));
        if (MaxObservationCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxObservationCount));
        if (MaxObservationContextCharacters <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxObservationContextCharacters));
        if (MaxConversationMessages <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxConversationMessages));
        if (MaxConversationCharacters <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxConversationCharacters));
        if (MaxConsecutiveIdenticalToolCalls <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxConsecutiveIdenticalToolCalls));
        if (MaxProgressCharacters <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxProgressCharacters));
    }
}
