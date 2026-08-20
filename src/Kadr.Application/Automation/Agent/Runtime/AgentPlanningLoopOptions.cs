namespace KadrStudio.Application.Automation.Agent.Runtime;

public sealed record AgentPlanningLoopOptions(
    int MaxModelTurns = 18,
    int MaxObservationCount = 12,
    int MaxObservationContextCharacters = 48_000,
    int MaxConversationMessages = 40,
    int MaxConversationCharacters = 16_000,
    int MaxConsecutiveIdenticalToolCalls = 2,
    int MaxProgressCharacters = 600)
{
    public static AgentPlanningLoopOptions Default { get; } = new();

    public void Validate()
    {
        if (MaxModelTurns <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(MaxModelTurns),
                "Max model turns must be positive.");

        if (MaxObservationCount <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(MaxObservationCount),
                "Max observation count must be positive.");

        if (MaxObservationContextCharacters <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(MaxObservationContextCharacters),
                "Observation context budget must be positive.");

        if (MaxConversationMessages <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(MaxConversationMessages),
                "Conversation message limit must be positive.");

        if (MaxConversationCharacters <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(MaxConversationCharacters),
                "Conversation context budget must be positive.");

        if (MaxConsecutiveIdenticalToolCalls <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(MaxConsecutiveIdenticalToolCalls),
                "Repeated tool-call limit must be positive.");

        if (MaxProgressCharacters <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(MaxProgressCharacters),
                "Progress text limit must be positive.");
    }
}
