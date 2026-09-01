using System.Globalization;

namespace KadrStudio.AiServer.Configuration;

public sealed record AiServerOptions
{
    public const string DefaultVisionBackendModel = "Qwen/Qwen3-VL-8B-Instruct-GGUF";
    public const string DefaultPlannerPublicModelAlias = "kadr-planner:latest";
    public const string DefaultPlannerBackendModel = "Qwen/Qwen3-30B-A3B-Instruct-2507";
    public const string DefaultListenUrls = "http://127.0.0.1:5080";

    public string VisionBackendModel { get; init; } = DefaultVisionBackendModel;
    public string PlannerBackendModel { get; init; } = DefaultPlannerBackendModel;
    public string PlannerPublicModelAlias { get; init; } = DefaultPlannerPublicModelAlias;
    public string ProductionModelsRoot { get; init; } = Path.Combine(AppContext.BaseDirectory, "LocalData", "AiServer", "models");
    public string DataRoot { get; init; } = Path.Combine(AppContext.BaseDirectory, "LocalData", "AiServer", "v2");
    public string WorkersRoot { get; init; } = Path.Combine(AppContext.BaseDirectory, "workers");
    public string? ApiKey { get; init; }
    public long MaxRequestBodyBytes { get; init; }
    public long MaxAssetBytes { get; init; } = 16L * 1024 * 1024 * 1024;
    public int MaxImageCount { get; init; }
    public int MaxPromptCharacters { get; init; }
    public int MaxPlannerContextTokens { get; init; } = 32_768;
    public string ListenUrls { get; init; } = DefaultListenUrls;

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
        return new AiServerOptions
        {
            VisionBackendModel = ReadNonEmpty("KADR_AI_VISION_MODEL") ?? DefaultVisionBackendModel,
            PlannerBackendModel = ReadNonEmpty("KADR_AI_PLANNER_MODEL") ?? DefaultPlannerBackendModel,
            PlannerPublicModelAlias = ReadNonEmpty("KADR_AI_PLANNER_PUBLIC_MODEL") ?? DefaultPlannerPublicModelAlias,
            ProductionModelsRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(productionModelsRoot)),
            DataRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dataRoot)),
            WorkersRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(workersRoot)),
            ApiKey = ReadNonEmpty("KADR_AI_API_KEY"),
            MaxRequestBodyBytes = checked(maxRequestMegabytes * 1024L * 1024L),
            MaxAssetBytes = checked(maxAssetMegabytes * 1024L * 1024L),
            MaxImageCount = (int)ReadLong("KADR_AI_MAX_IMAGES", 16, 1, 64),
            MaxPromptCharacters = (int)ReadLong("KADR_AI_MAX_PROMPT_CHARS", 2_000_000, 8_192, 16_000_000),
            MaxPlannerContextTokens = (int)ReadLong(
                "KADR_PLANNER_CONTEXT_TOKENS", 32_768, 2_048, 262_144),
            ListenUrls = ReadNonEmpty("KADR_AI_URLS") ?? DefaultListenUrls
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
}

public sealed record WorkerModelRoute(string PublicAlias, string BackendModel, string Role);
