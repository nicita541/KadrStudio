using System.Text.Json;
using KadrStudio.AiServer.Api;
using KadrStudio.AiServer.Storage;

namespace KadrStudio.AiServer.Workers;

public sealed record WorkerJob(
    Guid Id,
    string Analyzer,
    string AnalyzerVersion,
    IReadOnlyList<StoredAsset> Assets,
    JsonElement Parameters);

public sealed record WorkerJobResult(
    bool IsSuccess,
    byte[] ResultJson,
    string[] ArtifactIds,
    string? ErrorCode = null,
    string? Error = null);

public interface IWorkerGateway
{
    Task<WorkerJobResult> ExecuteAsync(
        WorkerJob job,
        IProgress<double>? progress,
        CancellationToken cancellationToken);

    Task<int> CountTokensAsync(
        string model,
        string text,
        CancellationToken cancellationToken);

    Task ReleaseAcceleratorAsync(CancellationToken cancellationToken);
}

public sealed record WorkerManifest(
    string Analyzer,
    string ProtocolVersion,
    string Executable,
    string[] Arguments,
    int Port);

public static class WorkerAnalyzers
{
    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "video-understanding",
        "asr-align",
        "diarization",
        "audio-events",
        "embedding",
        "anime-upscale",
        "director",
        "critic"
    };
}
