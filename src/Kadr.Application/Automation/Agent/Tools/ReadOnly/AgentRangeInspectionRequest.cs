namespace KadrStudio.Application.Automation.Agent.Tools.ReadOnly;

public enum AgentRangeTargetKind
{
    Media,
    Sequence
}

public enum AgentRangeInspectionDetail
{
    Summary,
    Frames,
    Audio,
    Transcript,
    All
}

public sealed record AgentRangeInspectionRequest(
    AgentRangeTargetKind TargetKind,
    Guid TargetId,
    double StartSeconds,
    double EndSeconds,
    AgentRangeInspectionDetail Detail);
