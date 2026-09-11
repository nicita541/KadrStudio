using System.Globalization;

namespace KadrStudio.AiServer.Configuration;

public enum AiServerAccessMode
{
    Local,
    Remote
}

public sealed record AiServerOptions
{
    public const string DefaultVisionBackendModel = "Qwen/Qwen3-VL-8B-Instruct-GGUF";
    public const string DefaultPlannerPublicModelAlias = "kadr-planner:latest";
    public const string DefaultPlannerBackendModel = "Qwen/Qwen3-30B-A3B-Instruct-2507";
    public const string DefaultListenUrls = "http://127.0.0.1:5080";
    public const string AnimeSrModelSha256 = "5302034b463cded6497eb2c53e7e6be22d3e4f16bed190d7cfff4415ff81c548";

    public string VisionBackendModel { get; init; } = DefaultVisionBackendModel;
    public string PlannerBackendModel { get; init; } = DefaultPlannerBackendModel;
    public string PlannerPublicModelAlias { get; init; } = DefaultPlannerPublicModelAlias;
    public string ProductionModelsRoot { get; init; } = Path.Combine(AppContext.BaseDirectory, "LocalData", "AiServer", "models");
    public string DataRoot { get; init; } = Path.Combine(AppContext.BaseDirectory, "LocalData", "AiServer", "v2");
    public string WorkersRoot { get; init; } = Path.Combine(AppContext.BaseDirectory, "workers");
    public string? AsrModelPath { get; init; }
    public string? EmbeddingModelPath { get; init; }
    public string? DiarizationModelPath { get; init; }
    public string AsrModelRevision { get; init; } = "faster-whisper-large-v3";
    public string EmbeddingModelRevision { get; init; } = "multilingual-minilm-l12-int8";
    public string DiarizationModelRevision { get; init; } = "pyannote-speaker-diarization-3.1";
    public string? ApiKey { get; init; }
    public AiServerAccessMode AccessMode { get; init; } = AiServerAccessMode.Local;
    public bool AllowInsecureRemoteHttp { get; init; }
    public long MaxRequestBodyBytes { get; init; }
    public long MaxAssetBytes { get; init; } = 16L * 1024 * 1024 * 1024;
    public int MaxImageCount { get; init; }
    public int MaxPromptCharacters { get; init; }
    public int MaxPlannerContextTokens { get; init; } = 32_768;
    public int MaxQueuedJobs { get; init; } = 128;
    public int MaxActiveJobs { get; init; } = 2;
    public TimeSpan JobRetention { get; init; } = TimeSpan.FromDays(7);
    public int MaxJobHistory { get; init; } = 2_000;
    public TimeSpan PartialUploadTtl { get; init; } = TimeSpan.FromHours(24);
    public long MaxDataRootBytes { get; init; } = 256L * 1024 * 1024 * 1024;
    public string ListenUrls { get; init; } = DefaultListenUrls;
    public string AnimeSrModelPath => Path.Combine(
        ProductionModelsRoot, "animesr-x-experimental", "AnimeSR-X_first_short_run_best.pth");

    public static AiServerOptions FromEnvironment()
    {
        var aiRoot = ReadNonEmpty("KADR_AI_DATA_ROOT");
        var dataRoot = ReadNonEmpty("KADR_AI_RUNTIME_DATA_ROOT") ?? (aiRoot is null
            ? Path.Combine(AppContext.BaseDirectory, "LocalData", "AiServer", "v2")
            : Path.Combine(aiRoot, "data"));
        var workersRoot = ReadNonEmpty("KADR_AI_WORKERS_ROOT") ?? (aiRoot is null
            ? Path.Combine(AppContext.BaseDirectory, "workers")
            : Path.Combine(aiRoot, "workers"));
        var productionModelsRoot = ReadNonEmpty("KADR_AI_PRODUCTION_MODELS_ROOT") ?? (aiRoot is null
            ? Path.Combine(AppContext.BaseDirectory, "LocalData", "AiServer", "models")
            : Path.Combine(aiRoot, "models"));
        var maxRequestMegabytes = ReadLong("KADR_AI_MAX_REQUEST_MB", 96, 8, 1024);
        var maxAssetMegabytes = ReadLong("KADR_AI_MAX_ASSET_MB", 16_384, 64, 1_048_576);
        var apiKey = ReadNonEmpty("KADR_AI_API_KEY");
        var listenUrls = ReadNonEmpty("KADR_AI_URLS") ?? DefaultListenUrls;
        var configuredMode = ReadNonEmpty("KADR_AI_MODE");
        var accessMode = configuredMode?.ToLowerInvariant() switch
        {
            null => apiKey is not null || ParseListenUris(listenUrls).Any(uri => !IsLoopbackHost(uri.Host))
                ? AiServerAccessMode.Remote
                : AiServerAccessMode.Local,
            "local" => AiServerAccessMode.Local,
            "remote" => AiServerAccessMode.Remote,
            _ => throw new InvalidOperationException("KADR_AI_MODE must be 'local' or 'remote'.")
        };
        return new AiServerOptions
        {
            VisionBackendModel = ReadNonEmpty("KADR_AI_VISION_MODEL") ?? DefaultVisionBackendModel,
            PlannerBackendModel = ReadNonEmpty("KADR_AI_PLANNER_MODEL") ?? DefaultPlannerBackendModel,
            PlannerPublicModelAlias = ReadNonEmpty("KADR_AI_PLANNER_PUBLIC_MODEL") ?? DefaultPlannerPublicModelAlias,
            ProductionModelsRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(productionModelsRoot)),
            DataRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dataRoot)),
            WorkersRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(workersRoot)),
            AsrModelPath = ReadExpandedPath("KADR_ASR_MODEL"),
            EmbeddingModelPath = ReadExpandedPath("KADR_EMBEDDING_MODEL"),
            DiarizationModelPath = ReadExpandedPath("KADR_DIARIZATION_MODEL"),
            AsrModelRevision = ReadNonEmpty("KADR_ASR_MODEL_REVISION") ?? "faster-whisper-large-v3",
            EmbeddingModelRevision = ReadNonEmpty("KADR_EMBEDDING_MODEL_REVISION") ?? "multilingual-minilm-l12-int8",
            DiarizationModelRevision = ReadNonEmpty("KADR_DIARIZATION_MODEL_REVISION") ?? "pyannote-speaker-diarization-3.1",
            ApiKey = apiKey,
            AccessMode = accessMode,
            AllowInsecureRemoteHttp = string.Equals(
                ReadNonEmpty("KADR_AI_ALLOW_INSECURE_REMOTE_HTTP"), "1", StringComparison.Ordinal),
            MaxRequestBodyBytes = checked(maxRequestMegabytes * 1024L * 1024L),
            MaxAssetBytes = checked(maxAssetMegabytes * 1024L * 1024L),
            MaxImageCount = (int)ReadLong("KADR_AI_MAX_IMAGES", 16, 1, 64),
            MaxPromptCharacters = (int)ReadLong("KADR_AI_MAX_PROMPT_CHARS", 2_000_000, 8_192, 16_000_000),
            MaxPlannerContextTokens = (int)ReadLong(
                "KADR_PLANNER_CONTEXT_TOKENS", 32_768, 2_048, 262_144),
            MaxQueuedJobs = (int)ReadLong("KADR_AI_MAX_QUEUED_JOBS", 128, 1, 100_000),
            MaxActiveJobs = (int)ReadLong("KADR_AI_MAX_ACTIVE_JOBS", 2, 1, 64),
            JobRetention = TimeSpan.FromHours(ReadLong("KADR_AI_JOB_RETENTION_HOURS", 168, 1, 8760)),
            MaxJobHistory = (int)ReadLong("KADR_AI_MAX_JOB_HISTORY", 2_000, 1, 1_000_000),
            PartialUploadTtl = TimeSpan.FromMinutes(ReadLong("KADR_AI_PARTIAL_UPLOAD_TTL_MINUTES", 1440, 1, 525_600)),
            MaxDataRootBytes = checked(ReadLong("KADR_AI_MAX_DATA_ROOT_GB", 256, 1, 1_048_576) * 1024L * 1024L * 1024L),
            ListenUrls = listenUrls
        };
    }

    public WorkerModelRoute ResolvePlannerModel(string? requestedModel)
    {
        var requested = string.IsNullOrWhiteSpace(requestedModel)
            ? PlannerPublicModelAlias
            : requestedModel.Trim();
        if (!requested.Equals(PlannerPublicModelAlias, StringComparison.OrdinalIgnoreCase) &&
            !requested.Equals(PlannerBackendModel, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unknown structured-reasoning model '{requested}'.");
        return new WorkerModelRoute(PlannerPublicModelAlias, PlannerBackendModel, "planner");
    }

    public void ValidateForStartup(string? effectiveListenUrls = null)
    {
        var uris = ParseListenUris(effectiveListenUrls ?? ListenUrls);
        if (AccessMode == AiServerAccessMode.Remote)
        {
            if (string.IsNullOrWhiteSpace(ApiKey))
                throw new InvalidOperationException("Remote AI Server mode requires an API key.");
            if (!AllowInsecureRemoteHttp && uris.Any(uri => uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    "Remote AI Server mode requires HTTPS. Set KADR_AI_ALLOW_INSECURE_REMOTE_HTTP=1 only for explicit development use.");
            return;
        }
        if (uris.Any(uri => !IsLoopbackHost(uri.Host)))
            throw new InvalidOperationException("Local AI Server mode may listen only on loopback addresses.");
    }

    private static Uri[] ParseListenUris(string value)
    {
        var values = (value ?? string.Empty).Split(
            ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length == 0)
            throw new InvalidOperationException("At least one KADR_AI_URLS endpoint is required.");
        var uris = new List<Uri>(values.Length);
        foreach (var item in values)
        {
            if (!Uri.TryCreate(item, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
                throw new InvalidOperationException($"Invalid AI Server listen URL '{item}'.");
            uris.Add(uri);
        }
        return uris.ToArray();
    }

    private static bool IsLoopbackHost(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
           System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address);

    private static long ReadLong(string name, long defaultValue, long min, long max)
    {
        var raw = ReadNonEmpty(name);
        if (raw is null) return defaultValue;
        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ||
            parsed < min || parsed > max)
            throw new InvalidOperationException($"{name} must be an integer in range {min}..{max}.");
        return parsed;
    }

    private static string? ReadNonEmpty(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? ReadExpandedPath(string name)
    {
        var value = ReadNonEmpty(name);
        return value is null ? null : Path.GetFullPath(Environment.ExpandEnvironmentVariables(value));
    }
}

public sealed record WorkerModelRoute(string PublicAlias, string BackendModel, string Role);
