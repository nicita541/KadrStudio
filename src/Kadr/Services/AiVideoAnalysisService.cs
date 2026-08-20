using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using KadrStudio.Application.Automation;
using KadrStudio.Models;
using CoreMediaClip = KadrStudio.Core.Domain.MediaClip;
using CoreProjectState = KadrStudio.Core.Domain.ProjectState;
using CoreAnalysisEvidence = KadrStudio.Core.Domain.AnalysisEvidence;
using CoreAnalysisSegment = KadrStudio.Core.Domain.AnalysisSegment;
using CoreGameEditingProfile = KadrStudio.Core.Domain.GameEditingProfile;
using CoreMontageEvidenceKind = KadrStudio.Core.Domain.MontageEvidenceKind;
using CoreTimeRange = KadrStudio.Core.Domain.TimeRange;
using CoreTimelineTime = KadrStudio.Core.Domain.TimelineTime;
using CoreMontagePlan = KadrStudio.Core.Domain.MontagePlan;
using CoreMontagePlanItem = KadrStudio.Core.Domain.MontagePlanItem;
using CoreMontageRole = KadrStudio.Core.Domain.MontageRole;
using CoreSourceAnnotationKind = KadrStudio.Core.Domain.SourceAnnotationKind;
using CoreTransitionKind = KadrStudio.Core.Domain.TransitionKind;

namespace KadrStudio.Services;

public sealed class AiVideoAnalysisService : IDisposable
{
    public const string DefaultServerModelAlias = "kadr-vision:latest";
    public const string DefaultPlannerModelAlias = "kadr-planner:latest";
    private readonly FfmpegLocator _ffmpegLocator;
    private readonly ProcessRunner _processRunner;
    private readonly HttpClient _httpClient;
    private readonly AiServerClientOptions _serverOptions;
    private readonly ConcurrentDictionary<string, byte> _verifiedModels = new(StringComparer.OrdinalIgnoreCase);

    public AiVideoAnalysisService(
        FfmpegLocator ffmpegLocator,
        ProcessRunner processRunner,
        AiServerClientOptions? serverOptions = null,
        HttpMessageHandler? messageHandler = null)
    {
        _ffmpegLocator = ffmpegLocator;
        _processRunner = processRunner;
        _serverOptions = serverOptions ?? AiServerClientOptions.FromEnvironment();
        var endpoint = _serverOptions.Endpoint ?? AiServerClientOptions.DefaultServerEndpoint;
        var useProxy = !endpoint.IsLoopback;
        _httpClient = new HttpClient(messageHandler ?? new HttpClientHandler { UseProxy = useProxy })
        {
            BaseAddress = endpoint,
            Timeout = TimeSpan.FromHours(2)
        };
        if (!string.IsNullOrWhiteSpace(_serverOptions.ApiKey))
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _serverOptions.ApiKey);
    }

    public Uri Endpoint => _httpClient.BaseAddress!;
    public string PreferredModel => _serverOptions.PreferredModel ?? DefaultServerModelAlias;

    public async Task<IReadOnlyList<AiModelInfo>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureServerAsync(cancellationToken);
        using var response = await _httpClient.GetAsync("v1/models", cancellationToken);
        await EnsureSuccessAsync(response, "Не удалось получить список моделей ИИ-сервера", cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("models", out var modelsElement))
        {
            return Array.Empty<AiModelInfo>();
        }

        var models = new List<AiModelInfo>();
        foreach (var modelElement in modelsElement.EnumerateArray())
        {
            var name = modelElement.TryGetProperty("id", out var nameElement)
                ? nameElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var capabilities = modelElement.TryGetProperty("capabilities", out var capabilitiesElement)
                ? capabilitiesElement.EnumerateArray()
                    .Select(item => item.GetString())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Cast<string>()
                    .ToArray()
                : [];
            models.Add(new AiModelInfo(
                name, 0, capabilities.Contains("vision", StringComparer.OrdinalIgnoreCase), true));
        }

        return models
            .OrderByDescending(model => model.Name.Equals(PreferredModel, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(model => model.SupportsVision)
            .ToList();
    }

    public async Task VerifyModelAsync(string model, CancellationToken cancellationToken = default)
    {
        if (_verifiedModels.ContainsKey(model)) return;
        await EnsureServerAsync(cancellationToken).ConfigureAwait(false);
        using var schema = JsonDocument.Parse("""
            {"type":"object","properties":{"status":{"type":"string","enum":["ok"]}},"required":["status"],"additionalProperties":false}
            """);
        var inference = await RunStructuredInferenceAsync(
            schema.RootElement,
            "Верни только JSON по заданной схеме.",
            "Проверка готовности ИИ для монтажа видео.",
            images: null,
            temperature: 0,
            contextTokens: 2048,
            maxTokens: 64,
            cancellationToken,
            model: model,
            think: false).ConfigureAwait(false);
        var raw = inference.Content;
        using var result = JsonDocument.Parse(ExtractJson(raw));
        if (!GetString(result.RootElement, "status").Equals("ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"ИИ-модель {model} не прошла проверку готовности.");
        _verifiedModels.TryAdd(model, 0);
    }

    internal async Task<string> RunAgentStructuredTurnAsync(
        JsonElement schema,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default,
        bool think = true,
        int maxTokens = 4096,
        int? reasoningTokens = 1280)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            throw new ArgumentException(
                "Agent response schema must be a JSON object.",
                nameof(schema));
        if (string.IsNullOrWhiteSpace(systemPrompt))
            throw new ArgumentException(
                "Agent system prompt cannot be empty.",
                nameof(systemPrompt));
        if (string.IsNullOrWhiteSpace(userPrompt))
            throw new ArgumentException(
                "Agent turn payload cannot be empty.",
                nameof(userPrompt));

        await EnsureServerAsync(cancellationToken).ConfigureAwait(false);

        var inference = await RunStructuredInferenceAsync(
            schema,
            systemPrompt,
            userPrompt,
            images: null,
            temperature: 0,
            contextTokens: 16384,
            maxTokens,
            cancellationToken,
            model: _serverOptions.PlannerModelAlias ?? DefaultPlannerModelAlias,
            think,
            reasoningTokens: think ? reasoningTokens : null).ConfigureAwait(false);
        var raw = inference.Content;
        var doneReason = inference.DoneReason;
        var evalCount = inference.EvalCount;

        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException(
                $"ИИ-сервер вернул пустой шаг AI-агента. done_reason={doneReason ?? "unknown"}, eval_count={evalCount}.");
        if (string.Equals(doneReason, "length", StringComparison.OrdinalIgnoreCase) ||
            !raw.TrimEnd().EndsWith("}", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"ИИ-сервер оборвал JSON шага AI-агента. done_reason={doneReason ?? "unknown"}, eval_count={evalCount}.");

        return ExtractJson(raw);
    }
    public async Task<AiAnalysisEnhancement> EnhanceAsync(
        MediaAsset asset,
        VideoAnalysisResult baseline,
        string query,
        string model,
        IProgress<VideoAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var inspection = await InspectRangeAsync(
            asset,
            baseline,
            query,
            model,
            progress,
            cancellationToken).ConfigureAwait(false);
        var observations = inspection.Observations.Select(item => new DetectedVideoRange(
            MarkerKind.Note,
            item.Start,
            item.End - item.Start,
            string.IsNullOrWhiteSpace(item.Title) ? "Наблюдение ИИ" : item.Title,
            item.Description,
            item.Confidence)).ToArray();
        return new AiAnalysisEnhancement(
            inspection.Summary,
            observations,
            model,
            inspection.UsedVision);
    }

    public async Task<AiRangeInspection> InspectRangeAsync(
        MediaAsset asset,
        VideoAnalysisResult baseline,
        string query,
        string model,
        IProgress<VideoAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("A vision model is required.", nameof(model));

        await EnsureServerAsync(cancellationToken).ConfigureAwait(false);
        var capabilities = await GetCapabilitiesAsync(model, cancellationToken).ConfigureAwait(false);
        if (!capabilities.Contains("vision", StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Модель {model} не поддерживает анализ изображений.");

        var duration = Math.Max(0.1, baseline.SourceEnd - baseline.SourceStart);
        var sheetCount = Math.Clamp((int)Math.Ceiling(duration / 180d), 1, 4);
        var window = duration / sheetCount;
        var specs = new List<ContactSheetSpec>(sheetCount);
        for (var index = 0; index < sheetCount; index++)
        {
            var start = baseline.SourceStart + index * window;
            var end = index == sheetCount - 1
                ? baseline.SourceEnd
                : Math.Min(baseline.SourceEnd, start + window);
            specs.Add(new ContactSheetSpec(
                start,
                end,
                duration >= 3 ? 16 : 4,
                $"часть {index + 1}/{sheetCount}"));
        }

        var paths = new List<string>();
        var images = new List<string>();
        try
        {
            for (var index = 0; index < specs.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new VideoAnalysisProgress(
                    90 + 5d * index / Math.Max(1, specs.Count),
                    $"Agent vision: диапазон {index + 1}/{specs.Count}"));
                var spec = specs[index];
                var path = await CreateContactSheetAsync(
                    asset.Path,
                    spec.Start,
                    spec.End,
                    spec.FrameCount,
                    cancellationToken).ConfigureAwait(false);
                paths.Add(path);
                images.Add(Convert.ToBase64String(
                    await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)));
            }

            using var schema = JsonDocument.Parse(
                """
                {
                  "type": "object",
                  "properties": {
                    "summary": { "type": "string" },
                    "observations": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "properties": {
                          "start": { "type": "number" },
                          "end": { "type": "number" },
                          "title": { "type": "string" },
                          "description": { "type": "string" },
                          "confidence": { "type": "number" },
                          "tags": {
                            "type": "array",
                            "items": { "type": "string" }
                          }
                        },
                        "required": [
                          "start",
                          "end",
                          "title",
                          "description",
                          "confidence",
                          "tags"
                        ],
                        "additionalProperties": false
                      }
                    }
                  },
                  "required": ["summary", "observations"],
                  "additionalProperties": false
                }
                """);

            var sheetDescription = string.Join(
                Environment.NewLine,
                specs.Select((spec, index) =>
                {
                    var step = Math.Max(0.001, (spec.End - spec.Start) / spec.FrameCount);
                    return
                        $"- image {index + 1}: {Format(spec.Start)}–{Format(spec.End)} sec, " +
                        $"{spec.FrameCount} frames left-to-right/top-to-bottom; " +
                        $"approx frame N time = {Format(spec.Start)} + (N-0.5)*{Format(step)} sec";
                }));

            var technicalRanges = string.Join(
                Environment.NewLine,
                baseline.Ranges
                    .Where(item => item.Kind is MarkerKind.Scene or MarkerKind.BlackFrame or
                        MarkerKind.Silence or MarkerKind.Freeze)
                    .OrderBy(item => item.SourceStart)
                    .Take(80)
                    .Select(item =>
                        $"- {item.Kind}: {Format(item.SourceStart)}–" +
                        $"{Format(item.SourceStart + item.Duration)}, confidence {Format(item.Confidence)}"));

            var messages = new object[]
            {
                new
                {
                    role = "system",
                    content =
                        "/no_think\n" +
                        "Ты визуальный исследователь материала для монтажного AI-агента. " +
                        "Верни только JSON строго по JSON Schema. " +
                        "Отвечай на конкретный вопрос агента, используя только видимое на приложенных кадрах " +
                        "и технические факты. Не решай, что удалять или как монтировать: твоя задача — наблюдения. " +
                        "Не выдумывай события между редкими кадрами. start/end — абсолютные секунды исходника " +
                        "и должны оставаться внутри анализируемого диапазона. " +
                        "Создавай observations только для фактов, полезных для вопроса; максимум 24."
                },
                new
                {
                    role = "user",
                    content =
                        $"Файл: {asset.Name}\n" +
                        $"Вопрос агента: {(string.IsNullOrWhiteSpace(query) ? "Опиши значимые визуальные факты диапазона." : query.Trim())}\n" +
                        $"Диапазон: {Format(baseline.SourceStart)}–{Format(baseline.SourceEnd)} сек.\n" +
                        $"Техническая сводка: {baseline.Summary}\n" +
                        $"Технические события:\n{technicalRanges}\n" +
                        $"Контактные листы:\n{sheetDescription}",
                    images = images.ToArray()
                }
            };

            progress?.Report(new VideoAnalysisProgress(96, $"Agent vision: смысловая проверка ({model})"));
            var responseJson = await RunLegacyEnvelopeAsync(
                schema.RootElement, messages, 0, 16384, 4096, cancellationToken).ConfigureAwait(false);
            using var envelope = JsonDocument.Parse(responseJson);
            var raw = envelope.RootElement
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;

            var doneReason = envelope.RootElement.TryGetProperty("done_reason", out var doneReasonElement)
                ? doneReasonElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(raw))
                throw new InvalidOperationException(
                    $"ИИ вернул пустой анализ диапазона. done_reason={doneReason ?? "unknown"}.");
            if (string.Equals(doneReason, "length", StringComparison.OrdinalIgnoreCase) ||
                !raw.TrimEnd().EndsWith("}", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"ИИ оборвал JSON анализа диапазона. done_reason={doneReason ?? "unknown"}.");

            using var document = JsonDocument.Parse(ExtractJson(raw));
            var summary = GetString(document.RootElement, "summary");
            var observations = new List<AiRangeObservation>();

            if (document.RootElement.TryGetProperty("observations", out var items) &&
                items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray().Take(24))
                {
                    if (!TryGetNumber(item, "start", out var start) ||
                        !TryGetNumber(item, "end", out var end))
                        continue;

                    start = Math.Clamp(start, baseline.SourceStart, baseline.SourceEnd);
                    end = Math.Clamp(end, baseline.SourceStart, baseline.SourceEnd);
                    if (end <= start + 0.05)
                        continue;

                    var confidence = TryGetNumber(item, "confidence", out var parsedConfidence)
                        ? Math.Clamp(parsedConfidence, 0, 1)
                        : 0.5;
                    var tags = item.TryGetProperty("tags", out var tagsElement) &&
                               tagsElement.ValueKind == JsonValueKind.Array
                        ? tagsElement.EnumerateArray()
                            .Where(tag => tag.ValueKind == JsonValueKind.String)
                            .Select(tag => tag.GetString()?.Trim())
                            .Where(tag => !string.IsNullOrWhiteSpace(tag))
                            .Select(tag => tag!)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Take(12)
                            .ToArray()
                        : Array.Empty<string>();

                    observations.Add(new AiRangeObservation(
                        start,
                        end,
                        GetString(item, "title"),
                        GetString(item, "description"),
                        confidence,
                        tags));
                }
            }

            progress?.Report(new VideoAnalysisProgress(100, "Agent vision: диапазон исследован"));
            return new AiRangeInspection(
                summary,
                observations
                    .OrderBy(item => item.Start)
                    .ThenByDescending(item => item.Confidence)
                    .ToArray(),
                model,
                UsedVision: true);
        }
        finally
        {
            foreach (var path in paths)
                TryDelete(path);
        }
    }

    public async Task<ImmutableArray<CoreAnalysisSegment>> AnalyzeMaterialAsync(
        MediaAsset asset,
        VideoAnalysisResult baseline,
        CoreGameEditingProfile profile,
        string model,
        IProgress<VideoAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _ = profile; // Compatibility DTO; it must not steer the shared AI prompt.
        var inspection = await InspectRangeAsync(
            asset,
            baseline,
            "Опиши только наблюдаемые события, действия, речь, эмоции и технические изменения без жанровых ярлыков и монтажных решений.",
            model,
            progress,
            cancellationToken).ConfigureAwait(false);
        return inspection.Observations.Select(item =>
        {
            var tags = (item.Tags.Count > 0 ? item.Tags : ["observed"])
                .ToImmutableDictionary(tag => tag, _ => item.Confidence, StringComparer.OrdinalIgnoreCase);
            return new CoreAnalysisSegment(
                Guid.NewGuid(),
                asset.Id,
                new CoreTimeRange(
                    CoreTimelineTime.FromSeconds(item.Start),
                    CoreTimelineTime.FromSeconds(item.End - item.Start)),
                0,
                0,
                0,
                string.Empty,
                tags,
                item.Confidence,
                [new CoreAnalysisEvidence(
                    CoreMontageEvidenceKind.Vision,
                    string.IsNullOrWhiteSpace(item.Description) ? item.Title : item.Description,
                    string.Join(",", item.Tags))]);
        }).OrderBy(item => item.SourceRange.Start).ToImmutableArray();
    }

    public async Task<EditCommandPlan> PlanEditsAsync(
        CoreProjectState project,
        string prompt,
        string model,
        CoreMediaClip? selectedClip,
        CancellationToken cancellationToken = default)
    {
        if (EditingCommandPlanner.TryCreateDeterministic(project, prompt, selectedClip, out var deterministic))
        {
            return deterministic;
        }

        await EnsureServerAsync(cancellationToken);
        var context = new StringBuilder();
        context.AppendLine($"Длительность проекта: {Format(project.Duration.TotalSeconds)} секунд.");
        if (selectedClip is not null)
        {
            project.Sources.TryGetValue(selectedClip.SourceId, out var asset);
            var track = project.FindTrack(selectedClip.TrackId);
            context.AppendLine(
                $"Выбранный клип: {asset?.Name}, дорожка {track?.Name}, " +
                $"{Format(selectedClip.Start.TotalSeconds)}–{Format(selectedClip.End.TotalSeconds)} сек.");
        }
        context.AppendLine("Доступные технические и нейтральные метки:");
        foreach (var marker in project.Markers
                     .Where(marker => marker.Kind is
                         KadrStudio.Core.Domain.MarkerKind.Scene or
                         KadrStudio.Core.Domain.MarkerKind.BlackFrame or
                         KadrStudio.Core.Domain.MarkerKind.Silence or
                         KadrStudio.Core.Domain.MarkerKind.Freeze or
                         KadrStudio.Core.Domain.MarkerKind.Note)
                     .OrderBy(marker => marker.Start)
                     .Take(60))
        {
            context.AppendLine(
                $"- {marker.Kind}: {Format(marker.Start.TotalSeconds)}–{Format(marker.End.TotalSeconds)}; {marker.Title}");
        }
        context.AppendLine("Клипы:");
        foreach (var clip in project.MediaClips
                     .OrderBy(clip => project.FindTrack(clip.TrackId)?.Kind)
                     .ThenBy(clip => project.FindTrack(clip.TrackId)?.Index)
                     .ThenBy(clip => clip.Start)
                     .Take(100))
        {
            var track = project.FindTrack(clip.TrackId);
            project.Sources.TryGetValue(clip.SourceId, out var source);
            context.AppendLine(
                $"- {track?.Name}: {Format(clip.Start.TotalSeconds)}–{Format(clip.End.TotalSeconds)}; {source?.Name}");
        }

        var messages = new object[]
        {
                    new
                    {
                        role = "system",
                        content =
                            "Ты планировщик монтажа. Не выполняй действия, только верни JSON без Markdown: " +
                            "{\"summary\":\"что будет сделано по-русски\",\"commands\":[{\"type\":\"delete_range|split_at|delete_selected\",\"start\":0.0,\"end\":1.0,\"reason\":\"причина\"}]}. " +
                            "Все времена — секунды таймлайна. Технические метки являются только измерениями, а не готовым монтажным решением. Не придумывай команды, которых не просили."
                    },
                    new
                    {
                        role = "user",
                        content = $"Запрос: {prompt}\n\n{context}"
                    }
        };
        var responseJson = await RunLegacyEnvelopeAsync(
            "json", messages, 0.05, 8192, 1024, cancellationToken).ConfigureAwait(false);
        using var envelope = JsonDocument.Parse(responseJson);
        var rawContent = envelope.RootElement.GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
        using var result = JsonDocument.Parse(ExtractJson(rawContent));
        var summary = GetString(result.RootElement, "summary");
        var commands = new List<EditCommand>();
        if (result.RootElement.TryGetProperty("commands", out var commandsElement) && commandsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var commandElement in commandsElement.EnumerateArray())
            {
                var typeText = GetString(commandElement, "type").ToLowerInvariant();
                var type = typeText switch
                {
                    "delete_range" => EditCommandType.DeleteRange,
                    "split_at" => EditCommandType.SplitAt,
                    "delete_selected" => EditCommandType.DeleteSelected,
                    _ => (EditCommandType)(-1)
                };
                if (!Enum.IsDefined(type))
                {
                    continue;
                }
                TryGetNumber(commandElement, "start", out var start);
                TryGetNumber(commandElement, "end", out var end);
                start = Math.Clamp(start, 0, project.Duration.TotalSeconds);
                end = Math.Clamp(end, 0, project.Duration.TotalSeconds);
                if (type == EditCommandType.DeleteRange && end <= start + 0.05)
                {
                    continue;
                }
                if (type == EditCommandType.SplitAt && start <= 0)
                {
                    continue;
                }
                if (type == EditCommandType.DeleteSelected && selectedClip is null)
                {
                    continue;
                }
                commands.Add(new EditCommand(type, start, end, GetString(commandElement, "reason")));
            }
        }
        if (commands.Count == 0)
        {
            throw new InvalidOperationException("Не удалось преобразовать запрос в безопасные команды монтажа.");
        }
        return new EditCommandPlan(string.IsNullOrWhiteSpace(summary) ? "План монтажа подготовлен." : summary, commands);
    }

    public async Task<CoreMontagePlan> PlanMontageAsync(
        MontagePlanningContext context,
        CancellationToken cancellationToken = default)
    {
        var baselineProvider = new EvidenceMontagePlanningProvider();
        var baseline = context.PreviousPlan is null
            ? await baselineProvider.CreatePlanAsync(context, cancellationToken).ConfigureAwait(false)
            : await baselineProvider.RevisePlanAsync(context, cancellationToken).ConfigureAwait(false);
        var model = context.Manifests.Values
            .Select(item => item.Model)
            .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item) && !item.Equals("technical+whisper", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(model)) return baseline;

        await EnsureServerAsync(cancellationToken);
        var permittedItems = baseline.Items.ToDictionary(item => item.Id);
        var segments = context.Manifests.Values
            .SelectMany(item => item.Segments)
            .Where(item => permittedItems.ContainsKey(item.Id) && !item.Evidence.IsDefaultOrEmpty)
            .Select(item => item with { SourceRange = permittedItems[item.Id].SourceRange })
            .OrderByDescending(ScoreForPrompt)
            .ThenBy(item => item.SourceId)
            .ThenBy(item => item.SourceRange.Start)
            .Take(140)
            .ToDictionary(item => item.Id);
        foreach (var item in permittedItems.Values.Where(item => !segments.ContainsKey(item.Id)))
            segments[item.Id] = new CoreAnalysisSegment(
                item.Id, item.SourceId, item.SourceRange, 0, 0, 0, string.Empty,
                ImmutableDictionary<string, double>.Empty.Add("plan-candidate", item.Confidence),
                item.Confidence,
                item.Evidence);
        foreach (var required in context.Request.Constraints.Where(item => item.Kind == CoreSourceAnnotationKind.Required))
        {
            if (segments.Values.Any(item => item.SourceId == required.SourceId &&
                                            item.SourceRange.Start <= required.SourceRange.Start &&
                                            item.SourceRange.End >= required.SourceRange.End))
                continue;
            segments[required.Id] = new CoreAnalysisSegment(
                required.Id, required.SourceId, required.SourceRange, 0, 0, 0, string.Empty,
                ImmutableDictionary<string, double>.Empty.Add("required", 1), 1,
                [new CoreAnalysisEvidence(CoreMontageEvidenceKind.UserAnnotation, required.Note, required.Id.ToString("N"))]);
        }

        var contextText = new StringBuilder();
        contextText.AppendLine($"Формат: {context.Request.TargetFormat}; цель {Format(context.Request.TargetDuration.TotalSeconds)} сек.");
        contextText.AppendLine(
            $"Технические пределы фрагмента: {Format(context.Request.Profile.MinimumSegmentSeconds)}–" +
            $"{Format(context.Request.Profile.MaximumSegmentSeconds)} сек.");
        contextText.AppendLine($"Запрос пользователя: {context.Request.Brief}");
        if (!string.IsNullOrWhiteSpace(context.RevisionRequest))
            contextText.AppendLine($"Корректировка: {context.RevisionRequest}");
        contextText.AppendLine("Кандидаты (можно использовать только эти segment_id):");
        foreach (var segment in segments.Values)
        {
            context.Project.Sources.TryGetValue(segment.SourceId, out var source);
            contextText.Append("- ").Append(segment.Id.ToString("N"))
                .Append(" | ").Append(source?.Name)
                .Append(" | ").Append(Format(segment.SourceRange.Start.TotalSeconds)).Append('–')
                .Append(Format(segment.SourceRange.End.TotalSeconds)).Append(" | tags=")
                .Append(string.Join(',', segment.Tags.OrderByDescending(item => item.Value).Select(item => $"{item.Key}:{Format(item.Value)}")))
                .Append(" | transcript=").Append(segment.Transcript)
                .Append(" | evidence=")
                .AppendLine(string.Join("; ", segment.Evidence.Select(item => item.Summary)));
        }
        contextText.AppendLine("Обязательные диапазоны:");
        foreach (var required in context.Request.Constraints.Where(item => item.Kind == CoreSourceAnnotationKind.Required))
            contextText.AppendLine($"- {required.Id:N}: {required.Note}");
        contextText.AppendLine("Заметки пользователя:");
        foreach (var note in context.Request.Constraints.Where(item => item.Kind == CoreSourceAnnotationKind.Note))
            contextText.AppendLine($"- {note.SourceId:N} {Format(note.SourceRange.Start.TotalSeconds)}–{Format(note.SourceRange.End.TotalSeconds)}: {note.Note}");
        if (context.PreviousPlan is not null)
        {
            contextText.AppendLine("Заблокированные элементы предыдущего плана нельзя менять:");
            foreach (var item in context.PreviousPlan.Items.Where(item => item.IsLocked))
                contextText.AppendLine($"- {item.Id:N}");
        }

        using var montageSchema = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "summary": {
                  "type": "string"
                },
                "items": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "segment_id": {
                        "type": "string"
                      },
                      "role": {
                        "type": "string",
                        "enum": [
                          "hook",
                          "setup",
                          "development",
                          "payoff",
                          "close"
                        ]
                      },
                      "transition_after": {
                        "type": "string",
                        "enum": [
                          "none",
                          "cross_dissolve",
                          "dip_to_black"
                        ]
                      },
                      "volume": {
                        "type": "number"
                      },
                      "subtitles": {
                        "type": "boolean"
                      }
                    },
                    "required": [
                      "segment_id",
                      "role",
                      "transition_after",
                      "volume",
                      "subtitles"
                    ],
                    "additionalProperties": false
                  }
                }
              },
              "required": [
                "summary",
                "items"
              ],
              "additionalProperties": false
            }
            """);

        var messages = new object[]
        {
                    new
                    {
                        role = "system",
                        content =
                            "Ты универсальный режиссёр монтажа. " +
                            "Верни только JSON строго по переданной JSON Schema, без Markdown и без текста вокруг JSON. " +
                            "Используй только переданные segment_id и каждый не более одного раза. " +
                            "Порядок items — итоговый порядок монтажа. " +
                            "Обязательные элементы включай всегда. " +
                            "Не используй все кандидаты без необходимости: выбери материал под целевую длительность. " +
                            "Нейтральные роли: hook, setup, development, payoff, close."
                    },
                    new
                    {
                        role = "user",
                        content = contextText.ToString()
                    }
        };
        var responseJson = await RunLegacyEnvelopeAsync(
            montageSchema.RootElement, messages, 0, 32768, 8192, cancellationToken).ConfigureAwait(false);
        using var envelope = JsonDocument.Parse(responseJson);

        var rawContent =
            envelope.RootElement
                .GetProperty("message")
                .GetProperty("content")
                .GetString()
            ?? string.Empty;

        var doneReason =
            envelope.RootElement.TryGetProperty("done_reason", out var doneReasonElement)
                ? doneReasonElement.GetString()
                : null;

        var evalCount =
            envelope.RootElement.TryGetProperty("eval_count", out var evalCountElement) &&
            evalCountElement.TryGetInt32(out var parsedEvalCount)
                ? parsedEvalCount
                : 0;

        if (string.IsNullOrWhiteSpace(rawContent))
        {
            throw new InvalidOperationException(
                $"ИИ вернул пустой монтажный план. done_reason={doneReason ?? "unknown"}, eval_count={evalCount}.");
        }

        if (string.Equals(doneReason, "length", StringComparison.OrdinalIgnoreCase) ||
            !rawContent.TrimEnd().EndsWith("}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"ИИ оборвал JSON до завершения. done_reason={doneReason ?? "unknown"}, eval_count={evalCount}.");
        }

        using var result = JsonDocument.Parse(ExtractJson(rawContent));
        var summary = GetString(result.RootElement, "summary");
        var items = new List<CoreMontagePlanItem>();
        if (result.RootElement.TryGetProperty("items", out var itemElements) && itemElements.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in itemElements.EnumerateArray())
            {
                var idText = GetString(element, "segment_id").Replace("-", string.Empty, StringComparison.Ordinal);
                if (!Guid.TryParseExact(idText, "N", out var segmentId) || !segments.TryGetValue(segmentId, out var segment) ||
                    items.Any(item => item.Id == segmentId))
                    continue;
                var role = ParseMontageRole(GetString(element, "role"));
                var transition = ParseMontageTransition(GetString(element, "transition_after"));
                var volume = TryGetNumber(element, "volume", out var parsedVolume) ? Math.Clamp(parsedVolume, 0, 2) : 1;
                var includeSubtitles = !element.TryGetProperty("subtitles", out var subtitlesElement) ||
                                       subtitlesElement.ValueKind != JsonValueKind.False;
                var required = context.Request.Constraints.FirstOrDefault(item =>
                    item.Kind == CoreSourceAnnotationKind.Required && item.SourceId == segment.SourceId &&
                    segment.SourceRange.Start <= item.SourceRange.Start && segment.SourceRange.End >= item.SourceRange.End);
                var range = required?.SourceRange ?? segment.SourceRange;
                items.Add(new CoreMontagePlanItem(
                    segment.Id, segment.SourceId, range, role, items.Count,
                    string.IsNullOrWhiteSpace(GetString(element, "reason")) ? "Выбрано ИИ по данным анализа" : GetString(element, "reason"),
                    segment.Confidence, segment.Evidence, required is not null, TransitionAfter: transition,
                    Volume: volume, IncludeSubtitles: includeSubtitles));
            }
        }

        foreach (var requiredItem in baseline.Items.Where(item =>
                     context.Request.Constraints.Any(constraint =>
                         constraint.Kind == CoreSourceAnnotationKind.Required && constraint.Id == item.Id)))
            if (items.All(item => item.Id != requiredItem.Id)) items.Add(requiredItem);

        var protectedLocked = context.PreviousPlan?.Items.Where(item => item.IsLocked).ToArray() ?? [];
        if (protectedLocked.Length > 0)
        {
            var lockedIds = protectedLocked.Select(item => item.Id).ToHashSet();
            items.RemoveAll(item => lockedIds.Contains(item.Id));
            items.AddRange(protectedLocked);
        }
        if (items.Count == 0)
            return baseline with
            {
                Summary = string.IsNullOrWhiteSpace(summary) ? baseline.Summary : summary,
                Warnings = baseline.Warnings.Add(
                    "ИИ не предложил безопасных изменений к подтверждённым диапазонам; сохранён базовый план."),
                UpdatedAt = DateTimeOffset.UtcNow,
                Dependencies = baseline.Dependencies with { Model = model }
            };
        var lockedOrders = protectedLocked.Select(item => item.Order).ToHashSet();
        var nextOrder = 0;
        var normalized = items
            .Where(item => !item.IsLocked || protectedLocked.All(locked => locked.Id != item.Id))
            .Select(item =>
            {
                while (lockedOrders.Contains(nextOrder)) nextOrder++;
                return item with { Order = nextOrder++ };
            })
            .Concat(protectedLocked)
            .OrderBy(item => item.Order)
            .ToImmutableArray();
        return baseline with
        {
            Summary = string.IsNullOrWhiteSpace(summary) ? baseline.Summary : summary,
            Items = normalized,
            UpdatedAt = DateTimeOffset.UtcNow,
            Dependencies = baseline.Dependencies with { Model = model }
        };
    }

    public async Task EnsureServerAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var response = await _httpClient.GetAsync("health/live", timeout.Token)
                .ConfigureAwait(false);
            await EnsureSuccessAsync(
                response,
                $"Kadr AI Server {Endpoint} недоступен",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException(
                $"Kadr AI Server {Endpoint} недоступен. Запустите отдельный сервер и проверьте адрес/API-ключ.",
                exception);
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private async Task<IReadOnlyList<string>> GetCapabilitiesAsync(string model, CancellationToken cancellationToken)
    {
        var selected = (await GetModelsAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(item => item.Name.Equals(model, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
            throw new InvalidOperationException($"ИИ-модель {model} не опубликована сервером {Endpoint}.");
        return selected.SupportsVision
            ? ["structured-output", "vision"]
            : ["structured-output"];
    }

    private async Task<string> CreateContactSheetAsync(
        string sourcePath,
        double sourceStart,
        double sourceEnd,
        int frameCount,
        CancellationToken cancellationToken)
    {
        _ffmpegLocator.EnsureAvailable();
        var temporaryDirectory = Path.Combine(KadrLocalDataPaths.TempRoot, "analysis");
        Directory.CreateDirectory(temporaryDirectory);
        var outputPath = Path.Combine(temporaryDirectory, $"contact-{Guid.NewGuid():N}.jpg");
        var duration = Math.Max(0.25, sourceEnd - sourceStart);
        var columns = Math.Max(2, (int)Math.Ceiling(Math.Sqrt(frameCount)));
        var rows = (int)Math.Ceiling(frameCount / (double)columns);
        var framesPerSecond = (frameCount + 0.75) / duration;
        var filter =
            $"fps={Format(framesPerSecond)}," +
            "scale=320:180:force_original_aspect_ratio=decrease," +
            "pad=320:180:(ow-iw)/2:(oh-ih)/2:black," +
            $"tile={columns}x{rows}:nb_frames={frameCount}:padding=4:margin=4";
        var result = await _processRunner.RunAsync(
            _ffmpegLocator.FfmpegPath,
            [
                "-hide_banner", "-y", "-ss", Format(sourceStart), "-t", Format(duration), "-i", sourcePath,
                "-vf", filter, "-frames:v", "1", "-q:v", "3", outputPath
            ],
            cancellationToken: cancellationToken);
        if (result.ExitCode != 0 || !File.Exists(outputPath))
        {
            throw new InvalidOperationException(
                $"Не удалось подготовить кадры для локального ИИ.\n{FfmpegOutput.LastMeaningfulLine(result.StandardError)}");
        }

        return outputPath;
    }

    private static double ScoreForPrompt(CoreAnalysisSegment segment)
    {
        var measuredEvidence = segment.Tags.Values.DefaultIfEmpty(0).Max();
        return measuredEvidence + segment.MotionScore * 0.2 + segment.LoudnessScore * 0.1 +
               segment.SpeechScore * 0.15 + segment.Confidence * 0.1;
    }

    private static CoreMontageRole ParseMontageRole(string value) => value.Trim().ToLowerInvariant() switch
    {
        "hook" => CoreMontageRole.Hook,
        "setup" => CoreMontageRole.Setup,
        "payoff" => CoreMontageRole.Payoff,
        "close" => CoreMontageRole.Ending,
        _ => CoreMontageRole.Development
    };

    private static CoreTransitionKind? ParseMontageTransition(string value) => value.Trim().ToLowerInvariant() switch
    {
        "cross_dissolve" or "crossdissolve" => CoreTransitionKind.CrossDissolve,
        "dip_to_black" or "diptoblack" => CoreTransitionKind.DipToBlack,
        _ => null
    };

    private static string ExtractJson(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            var preview = content.Length > 240 ? content[..240] + "…" : content;
            throw new InvalidOperationException(
                $"ИИ-сервер вернул результат не в формате JSON. Ответ: {preview}");
        }
        return content[start..(end + 1)];
    }

    private static string GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool TryGetNumber(JsonElement element, string name, out double value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number)
        {
            return property.TryGetDouble(out value);
        }

        return property.ValueKind == JsonValueKind.String &&
               double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private async Task<StructuredInferenceResult> RunStructuredInferenceAsync(
        JsonElement schema,
        string systemPrompt,
        string userPrompt,
        IReadOnlyList<string>? images,
        double temperature,
        int contextTokens,
        int maxTokens,
        CancellationToken cancellationToken,
        string? model = null,
        bool think = false,
        int? reasoningTokens = null)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            "v1/inference/structured",
            new
            {
                schema,
                systemPrompt,
                userPrompt,
                images,
                temperature,
                contextTokens,
                maxTokens,
                model,
                think,
                reasoningTokens
            },
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(
            response,
            "Kadr AI Server не выполнил структурированный inference",
            cancellationToken).ConfigureAwait(false);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;
        var content = GetString(root, "content");
        var doneReason = root.TryGetProperty("doneReason", out var reasonElement)
            ? reasonElement.GetString()
            : null;
        var evalCount = root.TryGetProperty("evalCount", out var countElement) &&
                        countElement.TryGetInt32(out var parsedCount)
            ? parsedCount
            : 0;
        var reasoningEvalCount = root.TryGetProperty("reasoningEvalCount", out var reasoningCountElement) &&
                                 reasoningCountElement.TryGetInt32(out var parsedReasoningCount)
            ? parsedReasoningCount
            : 0;
        var attemptCount = root.TryGetProperty("attemptCount", out var attemptCountElement) &&
                           attemptCountElement.TryGetInt32(out var parsedAttemptCount)
            ? parsedAttemptCount
            : 1;
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException(
                $"ИИ вернул пустой структурированный ответ. done_reason={doneReason ?? "unknown"}, eval_count={evalCount}.");
        }

        return new StructuredInferenceResult(
            content,
            doneReason,
            evalCount,
            reasoningEvalCount,
            attemptCount);
    }

    private async Task<string> RunLegacyEnvelopeAsync(
        object format,
        object[] messages,
        double temperature,
        int contextTokens,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        var messageElements = JsonSerializer.SerializeToElement(messages);
        string systemPrompt = string.Empty;
        string userPrompt = string.Empty;
        string[]? images = null;
        foreach (var message in messageElements.EnumerateArray())
        {
            var role = GetString(message, "role");
            if (role.Equals("system", StringComparison.OrdinalIgnoreCase))
                systemPrompt = GetString(message, "content");
            else if (role.Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                userPrompt = GetString(message, "content");
                if (message.TryGetProperty("images", out var imagesElement) &&
                    imagesElement.ValueKind == JsonValueKind.Array)
                {
                    images = imagesElement.EnumerateArray()
                        .Select(item => item.GetString())
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Cast<string>()
                        .ToArray();
                }
            }
        }

        JsonElement schema;
        var serializedFormat = JsonSerializer.SerializeToElement(format);
        if (serializedFormat.ValueKind == JsonValueKind.Object)
        {
            schema = serializedFormat;
        }
        else
        {
            schema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                additionalProperties = true
            });
        }

        var inference = await RunStructuredInferenceAsync(
            schema,
            systemPrompt,
            userPrompt,
            images,
            temperature,
            Math.Min(contextTokens, 8192),
            maxTokens,
            cancellationToken,
            model: _serverOptions.PreferredModel ?? DefaultServerModelAlias,
            think: false).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            message = new { content = inference.Content },
            done_reason = inference.DoneReason,
            eval_count = inference.EvalCount
        });
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string message,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var details = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(details);
            var root = document.RootElement;
            var errorCode = GetString(root, "errorCode");
            if (!string.IsNullOrWhiteSpace(errorCode))
            {
                var friendly = errorCode switch
                {
                    "reasoning_budget_exhausted" =>
                        "Модель исчерпала бюджет размышления, а финализатор не смог получить ответ.",
                    "schema_validation_failed" =>
                        "Модель вернула повреждённый JSON даже после автоматической финализации.",
                    "invalid_model_output" =>
                        "Модель дважды вернула пустой или неподдерживаемый ответ.",
                    "invalid_context_budget" =>
                        "Запрос не помещается в контекст вместе с зарезервированным местом для ответа.",
                    _ => "ИИ-сервер не смог сформировать структурированный ответ."
                };
                var doneReason = GetString(root, "doneReason");
                var evalCount = root.TryGetProperty("evalCount", out var evalElement) &&
                                evalElement.TryGetInt32(out var parsedEval)
                    ? parsedEval
                    : 0;
                var attemptCount = root.TryGetProperty("attemptCount", out var attemptElement) &&
                                   attemptElement.TryGetInt32(out var parsedAttempts)
                    ? parsedAttempts
                    : 0;
                throw new InvalidOperationException(
                    $"{message}: {friendly} Код: {errorCode}; done: {doneReason}; " +
                    $"tokens: {evalCount}; attempts: {attemptCount}. Можно безопасно повторить задачу.");
            }
        }
        catch (JsonException)
        {
            // Fall back to the raw non-structured server error below.
        }
        throw new InvalidOperationException($"{message}: HTTP {(int)response.StatusCode}. {details}");
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static void TryDelete(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Временный кадр будет удалён системой позже.
        }
    }

    private sealed record ContactSheetSpec(double Start, double End, int FrameCount, string Label);
    private sealed record StructuredInferenceResult(
        string Content,
        string? DoneReason,
        int EvalCount,
        int ReasoningEvalCount,
        int AttemptCount);
}

public sealed record AiServerClientOptions(
    Uri? Endpoint = null,
    string? ApiKey = null,
    string? PreferredModel = null,
    string? PlannerModelAlias = null)
{
    public static readonly Uri DefaultServerEndpoint = new("http://127.0.0.1:5080/");

    public static AiServerClientOptions FromEnvironment()
    {
        var endpointValue = Environment.GetEnvironmentVariable("KADR_STUDIO_AI_ENDPOINT");
        var endpoint = DefaultServerEndpoint;
        if (!string.IsNullOrWhiteSpace(endpointValue))
        {
            if (!Uri.TryCreate(endpointValue.TrimEnd('/') + "/", UriKind.Absolute, out var parsedEndpoint) ||
                parsedEndpoint is null ||
                parsedEndpoint.Scheme is not ("http" or "https"))
                throw new InvalidOperationException(
                    "KADR_STUDIO_AI_ENDPOINT должен быть абсолютным HTTP(S)-адресом.");
            endpoint = parsedEndpoint;
        }

        var apiKey = Environment.GetEnvironmentVariable("KADR_STUDIO_AI_API_KEY");
        var preferred = Environment.GetEnvironmentVariable("KADR_STUDIO_AI_MODEL_ALIAS");
        if (string.IsNullOrWhiteSpace(preferred))
            preferred = AiVideoAnalysisService.DefaultServerModelAlias;

        var planner = Environment.GetEnvironmentVariable("KADR_STUDIO_AI_PLANNER_MODEL_ALIAS");
        if (string.IsNullOrWhiteSpace(planner))
            planner = AiVideoAnalysisService.DefaultPlannerModelAlias;

        return new AiServerClientOptions(
            endpoint,
            apiKey?.Trim(),
            preferred.Trim(),
            planner.Trim());
    }
}
public sealed record AiModelInfo(string Name, long SizeBytes, bool SupportsVision, bool ServerManaged = true)
{
    public string DisplayName => $"{Name} · {(SupportsVision ? "видит кадры" : "текст")} · " +
                                 "управляется сервером";
}

public sealed record AiAnalysisEnhancement(
    string Summary,
    IReadOnlyList<DetectedVideoRange> Ranges,
    string Model,
    bool UsedVision);


public sealed record AiRangeObservation(
    double Start,
    double End,
    string Title,
    string Description,
    double Confidence,
    IReadOnlyList<string> Tags);

public sealed record AiRangeInspection(
    string Summary,
    IReadOnlyList<AiRangeObservation> Observations,
    string Model,
    bool UsedVision);
