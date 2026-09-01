namespace KadrStudio.Application.Automation.Agent;

/// <summary>The persisted stages of the only Kadr AI Editor workflow.</summary>
public enum AgentTaskPhase
{
    Indexing = 12,
    Directing = 13,
    RoughCut = 14,
    BoundaryRefining = 15,
    ReviewingDraft = 16,
    Accepted = 17,
    Discarded = 18,
    Retrieving = 20,
    Compiling = 21,
    Verifying = 22,
    Failed = 23,
    Cancelled = 24
}
