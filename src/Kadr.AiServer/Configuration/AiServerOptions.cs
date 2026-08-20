using System.Globalization;

namespace KadrStudio.AiServer.Configuration;

public sealed record AiServerOptions
{
    public const string DefaultPublicModelAlias = "kadr-vision:latest";
    public const string DefaultBackendModel = "qwen3-vl:4b-instruct";
    public const string DefaultPlannerPublicModelAlias = "kadr-planner:latest";
    public const string DefaultPlannerBackendModel = "qwen3.5:9b";
    public const string DefaultListenUrls = "http://127.0.0.1:5080";

    public required Uri OllamaEndpoint { get; init; }
    public required string BackendModel { get; init; }
    public required string PublicModelAlias { get; init; }
    public string PlannerBackendModel { get; init; } = DefaultPlannerBackendModel;
    public string PlannerPublicModelAlias { get; init; } = DefaultPlannerPublicModelAlias;
    public required string ModelsRoot { get; init; }
    public string? ApiKey { get; init; }
    public string? OllamaExecutable { get; init; }
    public bool ManageOllama { get; init; }
    public bool AutoPull { get; init; }
    public TimeSpan StartupTimeout { get; init; }
    public TimeSpan RequestTimeout { get; init; }
    public long MaxRequestBodyBytes { get; init; }
    public int MaxImageCount { get; init; }
    public int MaxPromptCharacters { get; init; }
    public string ListenUrls { get; init; } = DefaultListenUrls;

    public static AiServerOptions FromEnvironment()
    {
        var endpoint = ParseHttpUri(
            Environment.GetEnvironmentVariable("KADR_AI_OLLAMA_ENDPOINT"),
            new Uri("http://127.0.0.1:11436/"),
            "KADR_AI_OLLAMA_ENDPOINT");

        var backendModel = ReadNonEmpty("KADR_AI_MODEL") ?? DefaultBackendModel;
        var publicAlias = ReadNonEmpty("KADR_AI_PUBLIC_MODEL") ?? DefaultPublicModelAlias;
        var plannerBackendModel =
            ReadNonEmpty("KADR_AI_PLANNER_MODEL") ?? DefaultPlannerBackendModel;
        var plannerPublicAlias =
            ReadNonEmpty("KADR_AI_PLANNER_PUBLIC_MODEL") ?? DefaultPlannerPublicModelAlias;
        var modelsRoot = ReadNonEmpty("KADR_AI_MODELS_ROOT") ?? ResolveDefaultModelsRoot();
        var apiKey = ReadNonEmpty("KADR_AI_API_KEY");
        var ollamaExecutable = ReadNonEmpty("KADR_AI_OLLAMA_EXE");
        var listenUrls = ReadNonEmpty("KADR_AI_URLS") ?? DefaultListenUrls;

        var startupSeconds = ReadDouble("KADR_AI_STARTUP_TIMEOUT_SECONDS", 45, 5, 600);
        var requestMinutes = ReadDouble("KADR_AI_REQUEST_TIMEOUT_MINUTES", 120, 1, 720);
        var maxRequestMegabytes = ReadLong("KADR_AI_MAX_REQUEST_MB", 96, 8, 1024);
        var maxImages = (int)ReadLong("KADR_AI_MAX_IMAGES", 16, 1, 64);
        var maxPromptCharacters = (int)ReadLong("KADR_AI_MAX_PROMPT_CHARS", 2_000_000, 8_192, 16_000_000);

        return new AiServerOptions
        {
            OllamaEndpoint = endpoint,
            BackendModel = backendModel,
            PublicModelAlias = publicAlias,
            PlannerBackendModel = plannerBackendModel,
            PlannerPublicModelAlias = plannerPublicAlias,
            ModelsRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(modelsRoot)),
            ApiKey = apiKey,
            OllamaExecutable = ollamaExecutable,
            ManageOllama = ReadBoolean("KADR_AI_MANAGE_OLLAMA", defaultValue: true),
            AutoPull = ReadBoolean("KADR_AI_AUTO_PULL", defaultValue: true),
            StartupTimeout = TimeSpan.FromSeconds(startupSeconds),
            RequestTimeout = TimeSpan.FromMinutes(requestMinutes),
            MaxRequestBodyBytes = checked(maxRequestMegabytes * 1024L * 1024L),
            MaxImageCount = maxImages,
            MaxPromptCharacters = maxPromptCharacters,
            ListenUrls = listenUrls
        };
    }

    public bool CanManageConfiguredBackend()
        => OllamaEndpoint.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
           (OllamaEndpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            System.Net.IPAddress.TryParse(OllamaEndpoint.Host, out var address) &&
           System.Net.IPAddress.IsLoopback(address));

    public AiServerModelRoute ResolveModel(string? publicAlias)
    {
        var alias = string.IsNullOrWhiteSpace(publicAlias)
            ? PublicModelAlias
            : publicAlias.Trim();
        if (alias.Equals(PlannerPublicModelAlias, StringComparison.OrdinalIgnoreCase))
        {
            return new AiServerModelRoute(
                PlannerPublicModelAlias,
                PlannerBackendModel,
                RequiresVision: false,
                Role: "planner");
        }

        if (alias.Equals(PublicModelAlias, StringComparison.OrdinalIgnoreCase))
        {
            return new AiServerModelRoute(
                PublicModelAlias,
                BackendModel,
                RequiresVision: true,
                Role: "vision");
        }

        throw new InvalidOperationException($"Unknown public AI model alias '{alias}'.");
    }

    public IReadOnlyList<AiServerModelRoute> ConfiguredModels =>
    [
        ResolveModel(PlannerPublicModelAlias),
        ResolveModel(PublicModelAlias)
    ];

    private static Uri ParseHttpUri(string? raw, Uri fallback, string environmentVariable)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (!Uri.TryCreate(raw.TrimEnd('/') + "/", UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                $"{environmentVariable} must be an absolute HTTP(S) address.");
        }

        return parsed;
    }

    private static bool ReadBoolean(string name, bool defaultValue)
    {
        var raw = ReadNonEmpty(name);
        if (raw is null)
        {
            return defaultValue;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => throw new InvalidOperationException(
                $"{name} must be one of: true/false, 1/0, yes/no, on/off.")
        };
    }

    private static double ReadDouble(string name, double defaultValue, double min, double max)
    {
        var raw = ReadNonEmpty(name);
        if (raw is null)
        {
            return defaultValue;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ||
            parsed < min || parsed > max)
        {
            throw new InvalidOperationException(
                $"{name} must be a number in range {min.ToString(CultureInfo.InvariantCulture)}..{max.ToString(CultureInfo.InvariantCulture)}.");
        }

        return parsed;
    }

    private static long ReadLong(string name, long defaultValue, long min, long max)
    {
        var raw = ReadNonEmpty(name);
        if (raw is null)
        {
            return defaultValue;
        }

        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ||
            parsed < min || parsed > max)
        {
            throw new InvalidOperationException($"{name} must be an integer in range {min}..{max}.");
        }

        return parsed;
    }

    private static string? ReadNonEmpty(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string ResolveDefaultModelsRoot()
    {
        return Path.Combine(
            AppContext.BaseDirectory,
            "LocalData",
            "AiServer",
            "ollama-models");
    }
}

public sealed record AiServerModelRoute(
    string PublicAlias,
    string BackendModel,
    bool RequiresVision,
    string Role);
