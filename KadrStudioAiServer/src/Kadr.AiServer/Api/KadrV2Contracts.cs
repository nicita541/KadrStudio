using System.Text.Json;
using System.Text.Json.Serialization;

namespace KadrStudio.AiServer.Api;

[JsonConverter(typeof(JsonStringEnumConverter<AnalyzerJobState>))]
public enum AnalyzerJobState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

[JsonConverter(typeof(JsonStringEnumConverter<ReasoningRole>))]
public enum ReasoningRole
{
    Director,
    RoughCut,
    StoryContinuity,
    Rhythm,
    DialogueAudio,
    CompositionReframe,
    Captions,
    Critic
}

public sealed record AssetUploadResponse(
    string AssetId,
    long ReceivedBytes,
    long TotalBytes,
    bool IsComplete,
    string MediaType);

public sealed record AnalyzerJobRequest(
    string Analyzer,
    string AnalyzerVersion,
    string[] AssetIds,
    JsonElement Parameters,
    bool RequireProduction = true,
    string? OwnerId = null,
    string? RequestId = null);

public sealed record AnalyzerJobResponse(
    Guid Id,
    string Analyzer,
    string AnalyzerVersion,
    AnalyzerJobState State,
    double Progress,
    string Message,
    string[] ArtifactIds,
    string? ErrorCode,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public string? StorageError { get; init; }
    public string? OwnerId { get; init; }
    public int OwnershipProtocolVersion { get; init; } = 1;
}

public sealed record RoleStructuredReasoningRequest(
    ReasoningRole Role,
    JsonElement Schema,
    JsonElement Context,
    string Instruction,
    int ContextWindowTokens = 32768,
    int MaxTokens = 8192,
    string? Model = null,
    string Profile = "",
    bool RequireProduction = true);

public sealed record RoleStructuredReasoningResponse(
    string Content,
    ReasoningRole Role,
    int InputTokens,
    int InputBudgetTokens,
    int OutputBudgetTokens,
    int ReserveTokens,
    int AttemptCount);
