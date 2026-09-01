namespace KadrStudio.Application.Automation.Agent.Diagnostics;

public sealed record AgentDebugLogEntry(
    DateTimeOffset Timestamp,
    string Area,
    string EventName,
    Guid? TaskId = null,
    string? Phase = null,
    int? Turn = null,
    string? Message = null,
    string? Details = null,
    string? Exception = null);
