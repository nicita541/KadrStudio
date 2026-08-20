using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private static readonly Regex EpisodeTitlePattern = new(
        @"(?i)\b(?:сер(?:ия|ии)|эпизод|episode|next|следующ\w*)\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
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
            maxTokens: 32,
            cancellationToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken = default)
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
            contextTokens: 24576,
            maxTokens: 2048,
            cancellationToken).ConfigureAwait(false);
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
        await EnsureServerAsync(cancellationToken);
        var capabilities = await GetCapabilitiesAsync(model, cancellationToken);
        var supportsVision = capabilities.Contains("vision", StringComparer.OrdinalIgnoreCase);
        var contactSheetImages = new List<string>();
        var contactSheetPaths = new List<string>();
        var sheetSpecs = BuildContactSheetSpecs(baseline);

        try
        {
            if (supportsVision)
            {
                progress?.Report(new VideoAnalysisProgress(90, "Шаг 4/5: крупные кадры вокруг предполагаемых границ"));
                foreach (var sheet in sheetSpecs)
                {
                    var path = await CreateContactSheetAsync(
                        asset.Path,
                        sheet.Start,
                        sheet.End,
                        sheet.FrameCount,
                        cancellationToken);
                    contactSheetPaths.Add(path);
                    contactSheetImages.Add(Convert.ToBase64String(await File.ReadAllBytesAsync(path, cancellationToken)));
                }
            }

            progress?.Report(new VideoAnalysisProgress(96, $"Шаг 5/5: смысловая проверка серверным ИИ ({model})"));
            var messages = new object[]
            {
                new
                {
                    role = "system",
                    content = BuildSystemPrompt()
                },
                new
                {
                    role = "user",
                    content = BuildUserPrompt(asset, baseline, query, sheetSpecs, supportsVision),
                    images = contactSheetImages.ToArray()
                }
            };
            var responseJson = await RunLegacyEnvelopeAsync(
                "json", messages, 0.1, 16384, 4096, cancellationToken).ConfigureAwait(false);
            var enhancement = ParseEnhancement(responseJson, baseline, model, supportsVision);
            if (supportsVision && enhancement.Ranges.Any(range => range.Kind == MarkerKind.Ending))
            {
                progress?.Report(new VideoAnalysisProgress(98, "Шаг 5/6: отдельно проверяем эндинг, сцену после титров и превью"));
                enhancement = await RefineFinalStructureAsync(
                    asset, baseline, enhancement, model, cancellationToken);
            }
            progress?.Report(new VideoAnalysisProgress(100, "Многошаговый анализ завершён"));
            return enhancement;
        }
        finally
        {
            foreach (var path in contactSheetPaths)
            {
                TryDelete(path);
            }
        }
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
        await EnsureServerAsync(cancellationToken);
        var capabilities = await GetCapabilitiesAsync(model, cancellationToken);
        if (!capabilities.Contains("vision", StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Модель {model} не поддерживает анализ изображений.");

        var specs = BuildGameplayContactSheetSpecs(baseline);
        var paths = new List<string>();
        var images = new List<string>();
        try
        {
            for (var index = 0; index < specs.Count; index++)
            {
                progress?.Report(new VideoAnalysisProgress(
                    86 + 8d * index / Math.Max(1, specs.Count),
                    $"Игровой vision-анализ: обзор {index + 1}/{specs.Count}"));
                var spec = specs[index];
                var path = await CreateContactSheetAsync(
                    asset.Path, spec.Start, spec.End, spec.FrameCount, cancellationToken);
                paths.Add(path);
                images.Add(Convert.ToBase64String(await File.ReadAllBytesAsync(path, cancellationToken)));
            }

            var sheetDescription = string.Join(Environment.NewLine, specs.Select((spec, index) =>
                $"- изображение {index + 1}: {Format(spec.Start)}–{Format(spec.End)} сек., кадры слева направо и сверху вниз"));
            var tags = string.Join(", ", profile.EventTags);
            var messages = new object[]
            {
                new
                {
                    role = "system",
                    content =
                        "Ты универсальный анализатор видеоматериала. Верни только JSON без Markdown: " +
                        "{\"segments\":[{\"start\":0.0,\"end\":3.0,\"title\":\"событие\",\"description\":\"что видно\"," +
                        "\"confidence\":0.8,\"tags\":{\"tag\":0.9}}]}. " +
                        "start/end — абсолютные секунды исходника. Используй только события, которые видны на кадрах; " +
                        "не выдумывай содержание между редкими кадрами. Один сегмент должен описывать одно событие."
                },
                new
                {
                    role = "user",
                    content =
                        $"Профиль материала: {profile.DisplayName} ({profile.ContentFamily}).\n" +
                        $"Искомые теги: {tags}.\nПравила монтажа: {profile.PlanningGuidance}\n" +
                        $"Диапазон: {Format(baseline.SourceStart)}–{Format(baseline.SourceEnd)} сек.\n" +
                        $"Контактные листы:\n{sheetDescription}\n" +
                        "Отмечай только уверенно распознанные события, действия, речь, эмоции и изменения; не выдумывай содержание.",
                    images = images.ToArray()
                }
            };
            var responseJson = await RunLegacyEnvelopeAsync(
                "json", messages, 0.08, 16384, 4096, cancellationToken).ConfigureAwait(false);
            var parsed = ParseGameplaySegments(responseJson, asset.Id, baseline, profile);
            return await RefineGameplayBoundariesAsync(
                asset, parsed, baseline, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var path in paths) TryDelete(path);
        }
    }

    private async Task<ImmutableArray<CoreAnalysisSegment>> RefineGameplayBoundariesAsync(
        MediaAsset asset,
        ImmutableArray<CoreAnalysisSegment> segments,
        VideoAnalysisResult baseline,
        IProgress<VideoAnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (segments.IsDefaultOrEmpty) return [];
        var selected = segments
            .OrderByDescending(item => item.Confidence)
            .ThenByDescending(item => item.MotionScore + item.LoudnessScore)
            .Take(16)
            .Select(item => item.Id)
            .ToHashSet();
        var verifier = new VideoAnalysisService(_ffmpegLocator, _processRunner);
        var fps = asset.FrameRate > 0 ? asset.FrameRate : 30;
        var output = ImmutableArray.CreateBuilder<CoreAnalysisSegment>(segments.Length);
        var completed = 0;
        foreach (var segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!selected.Contains(segment.Id))
            {
                output.Add(segment);
                continue;
            }

            progress?.Report(new VideoAnalysisProgress(
                95 + 5d * completed / Math.Max(1, selected.Count),
                $"Уточнение границ фрагментов FFmpeg: {completed + 1}/{selected.Count}"));
            var start = await verifier.VerifyBoundaryAsync(
                asset.Path, segment.SourceRange.Start.TotalSeconds,
                baseline.SourceStart, baseline.SourceEnd, fps, cancellationToken).ConfigureAwait(false);
            var end = await verifier.VerifyBoundaryAsync(
                asset.Path, segment.SourceRange.End.TotalSeconds,
                baseline.SourceStart, baseline.SourceEnd, fps, cancellationToken).ConfigureAwait(false);
            completed++;
            if (end.VerifiedTime <= start.VerifiedTime + 0.15)
            {
                output.Add(segment);
                continue;
            }

            var evidence = segment.Evidence.Add(new CoreAnalysisEvidence(
                CoreMontageEvidenceKind.Technical,
                "Границы уточнены плотным FFmpeg-проходом около выбранного события.",
                $"start:{start.FrameCandidateCount};end:{end.FrameCandidateCount}"));
            output.Add(segment with
            {
                SourceRange = new CoreTimeRange(
                    CoreTimelineTime.FromSeconds(start.VerifiedTime),
                    CoreTimelineTime.FromSeconds(end.VerifiedTime - start.VerifiedTime)),
                Evidence = evidence
            });
        }
        progress?.Report(new VideoAnalysisProgress(100, "Игровые события и их границы уточнены"));
        return output.ToImmutable();
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
        context.AppendLine("Смысловые метки:");
        foreach (var marker in project.Markers
                     .Where(marker => marker.Kind is
                         KadrStudio.Core.Domain.MarkerKind.Opening or
                         KadrStudio.Core.Domain.MarkerKind.Ending or
                         KadrStudio.Core.Domain.MarkerKind.PostCredits or
                         KadrStudio.Core.Domain.MarkerKind.Preview or
                         KadrStudio.Core.Domain.MarkerKind.Recap or
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
                            "Все времена — секунды таймлайна. Используй точные границы смысловых меток. Не придумывай команды, которых не просили."
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
            .OrderByDescending(item => ScoreForPrompt(item, context.Request.Profile))
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
        contextText.AppendLine($"Профиль: {context.Request.Profile.DisplayName}. {context.Request.Profile.PlanningGuidance}");
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
                          "ending"
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
                            "Роли: hook, setup, development, payoff, ending."
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
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "KadrStudio", "analysis");
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

    private async Task<AiAnalysisEnhancement> RefineFinalStructureAsync(
        MediaAsset asset,
        VideoAnalysisResult baseline,
        AiAnalysisEnhancement firstPass,
        string model,
        CancellationToken cancellationToken)
    {
        var ending = firstPass.Ranges.First(range => range.Kind == MarkerKind.Ending);
        // Захватываем заметный участок до грубой границы: первый обзор может увидеть
        // титры не с первого кадра и запоздать на несколько десятков секунд.
        var start = Math.Max(baseline.SourceStart, ending.SourceStart - 45);
        var end = baseline.SourceEnd;
        if (end - start < 8)
        {
            return firstPass;
        }

        var frameCount = 36;
        var sheetPath = await CreateContactSheetAsync(asset.Path, start, end, frameCount, cancellationToken);
        try
        {
            var step = (end - start) / frameCount;
            var prompt = $$"""
                          Диапазон финала: {{Format(start)}}–{{Format(end)}} секунд. На изображении {{frameCount}} кадров,
                          слева направо и сверху вниз, приблизительное время кадра N: {{Format(start)}} + (N-0.5)*{{Format(step)}} секунд.
                          Первый проход уже нашёл предполагаемый ending {{Format(ending.SourceStart)}}–{{Format(ending.SourceStart + ending.Duration)}}.
                          Он может ошибаться и в начале, и в конце. Первые кадры диапазона могут ещё быть обычной серией.
                          Классифицируй КАЖДЫЙ кадр отдельно исключительно по тому, что реально видно на нём, не по его месту в видео.
                          ending = видны японские/латинские имена, производственные титры ИЛИ кадр находится между соседними кадрами одной музыкальной титровой последовательности;
                          postcredits = устойчивая сюжетная сцена БЕЗ имён/производственных титров, начавшаяся только после последнего титрового кадра;
                          preview = анонс/нарезка следующей серии;
                          episode = обычная серия до эндинга или пустой финальный кадр.
                          Верни ровно 36 меток в порядке кадров и только JSON:
                          {"labels":["episode","ending","postcredits","preview",...],"summary":"кратко что изменилось между блоками"}.
                          Если поверх сюжетного кадра остаётся хотя бы один столбец имён — это ending. Нельзя вернуть все 36 кадров как postcredits:
                          сначала должна быть непрерывная серия ending, и лишь после последнего кадра с именами может начаться postcredits.
                          """;
            var messages = new object[]
            {
                new { role = "system", content = "/no_think\nТы покадрово разделяешь финальные блоки anime. Ответ только JSON без Markdown." },
                new
                {
                    role = "user",
                    content = prompt,
                    images = new[] { Convert.ToBase64String(await File.ReadAllBytesAsync(sheetPath, cancellationToken)) }
                }
            };
            var responseJson = await RunLegacyEnvelopeAsync(
                "json", messages, 0, 16384, 2048, cancellationToken).ConfigureAwait(false);
            var finalPass = ParseFrameClassifications(responseJson, start, end, frameCount, model, ending);
            if (!finalPass.Ranges.Any(range => range.Kind == MarkerKind.PostCredits))
            {
                var embeddedTextRanges = await InferFinalRangesFromEmbeddedTextAsync(
                    asset, baseline, ending, cancellationToken);
                if (embeddedTextRanges.Count > 1)
                {
                    finalPass = finalPass with
                    {
                        Summary = "Финал разделён по независимому сигналу встроенных надписей; границы дополнительно проверяются по кадрам.",
                        Ranges = embeddedTextRanges
                    };
                }
            }
            var finalKinds = new HashSet<MarkerKind> { MarkerKind.Ending, MarkerKind.PostCredits, MarkerKind.Preview };
            var finalRanges = finalPass.Ranges.Where(range => finalKinds.Contains(range.Kind)).ToList();
            if (finalRanges.Count == 0)
            {
                return firstPass;
            }

            var merged = firstPass.Ranges
                .Where(range => !finalKinds.Contains(range.Kind))
                .Concat(finalRanges)
                .OrderBy(range => range.SourceStart)
                .ToList();
            return firstPass with
            {
                Summary = finalPass.Summary,
                Ranges = NormalizeSemanticRelationships(merged)
            };
        }
        finally
        {
            TryDelete(sheetPath);
        }
    }

    public async Task<IReadOnlyList<DetectedVideoRange>> InferFinalRangesFromEmbeddedTextAsync(
        MediaAsset asset,
        VideoAnalysisResult baseline,
        DetectedVideoRange fallbackEnding,
        CancellationToken cancellationToken = default)
    {
        var scanStart = Math.Max(baseline.SourceStart, fallbackEnding.SourceStart - 10);
        var scanDuration = baseline.SourceEnd - scanStart;
        var subtitleService = new AutoSubtitleService(_ffmpegLocator, _processRunner);
        var relativeCues = await subtitleService.ExtractEmbeddedTextAsync(
            asset, scanStart, scanDuration, preferSigns: true, cancellationToken);
        if (relativeCues.Count == 0)
        {
            return [fallbackEnding];
        }

        var cues = relativeCues
            .Select(cue => new SubtitleCue(cue.Start + scanStart, cue.End + scanStart, cue.Text))
            .Where(cue => cue.Start >= fallbackEnding.SourceStart + 35)
            .OrderBy(cue => cue.Start)
            .ToList();
        var episodeTitle = cues.FirstOrDefault(cue => EpisodeTitlePattern.IsMatch(cue.Text));
        var previewStart = episodeTitle is null
            ? baseline.SourceEnd
            : Math.Max(fallbackEnding.SourceStart + 45, episodeTitle.Start - 20);
        var postCreditsCue = cues
            .Where(cue => cue.Start < previewStart - 3 && !EpisodeTitlePattern.IsMatch(cue.Text))
            .FirstOrDefault();
        if (postCreditsCue is null)
        {
            return [fallbackEnding];
        }

        var postCreditsStart = postCreditsCue.Start;
        var referenceOpening = baseline.Ranges.FirstOrDefault(range => range.Kind == MarkerKind.Opening);
        var expectedEndingDuration = referenceOpening?.Duration is >= 60 and <= 120
            ? referenceOpening.Duration
            : 90;
        var endingStart = fallbackEnding.SourceStart;
        if (postCreditsStart - endingStart < expectedEndingDuration * 0.8)
        {
            endingStart = Math.Max(baseline.SourceStart, postCreditsStart - expectedEndingDuration);
        }
        var ranges = new List<DetectedVideoRange>
        {
            fallbackEnding with
            {
                SourceStart = endingStart,
                Duration = Math.Max(3, postCreditsStart - endingStart),
                Description = fallbackEnding.Description +
                    " Конец титров подтверждён появлением отдельной экранной надписи следующей сюжетной сцены; начало перепроверено по длительности музыкального блока.",
                Confidence = Math.Max(fallbackEnding.Confidence, 0.82)
            },
            new(MarkerKind.PostCredits, postCreditsStart, Math.Max(3, previewStart - postCreditsStart),
                KindTitle(MarkerKind.PostCredits),
                $"Сцена после титров начинается с отдельной экранной надписи «{TrimForDescription(postCreditsCue.Text)}».", 0.86)
        };
        if (episodeTitle is not null && baseline.SourceEnd - previewStart >= 3)
        {
            ranges.Add(new DetectedVideoRange(
                MarkerKind.Preview, previewStart, baseline.SourceEnd - previewStart,
                KindTitle(MarkerKind.Preview),
                $"Блок перед надписью следующего эпизода «{TrimForDescription(episodeTitle.Text)}».", 0.76));
        }
        return ranges;
    }

    private static string TrimForDescription(string text)
    {
        var compact = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 90 ? compact : compact[..87] + "…";
    }

    private static AiAnalysisEnhancement ParseFrameClassifications(
        string responseJson,
        double start,
        double end,
        int frameCount,
        string model,
        DetectedVideoRange fallbackEnding)
    {
        using var envelope = JsonDocument.Parse(responseJson);
        if (!envelope.RootElement.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var contentElement))
        {
            return new AiAnalysisEnhancement(string.Empty, Array.Empty<DetectedVideoRange>(), model, true);
        }
        var content = ExtractJson(contentElement.GetString() ?? string.Empty);
        using var document = JsonDocument.Parse(content);
        if (!document.RootElement.TryGetProperty("labels", out var labelsElement) || labelsElement.ValueKind != JsonValueKind.Array)
        {
            return new AiAnalysisEnhancement(string.Empty, Array.Empty<DetectedVideoRange>(), model, true);
        }
        var labels = labelsElement.EnumerateArray()
            .Select(item => NormalizeFrameLabel(item.GetString()))
            .Take(frameCount)
            .ToList();
        while (labels.Count < frameCount) labels.Add("episode");
        labels = StabilizeFrameLabels(labels);
        var step = (end - start) / frameCount;
        var endingIndexes = labels.Select((label, index) => (label, index))
            .Where(item => item.label == "ending")
            .Select(item => item.index)
            .ToList();
        if (endingIndexes.Count < 3)
        {
            return new AiAnalysisEnhancement(
                "Покадровая проверка финала отклонена: модель не подтвердила непрерывную титровую последовательность.",
                [fallbackEnding], model, true);
        }
        var firstEndingIndex = endingIndexes.First();
        var lastEndingIndex = endingIndexes.Last();
        if (firstEndingIndex == 0 && fallbackEnding.SourceStart - start > 20)
        {
            return new AiAnalysisEnhancement(
                "Покадровая проверка финала отклонена: модель без доказательств расширила титры на весь проверяемый диапазон.",
                [fallbackEnding], model, true);
        }
        for (var index = firstEndingIndex; index <= lastEndingIndex; index++) labels[index] = "ending";
        for (var index = 0; index < firstEndingIndex; index++)
        {
            if (labels[index] is "postcredits" or "preview") labels[index] = "episode";
        }
        var ranges = new List<DetectedVideoRange>();
        for (var index = 0; index < labels.Count;)
        {
            var label = labels[index];
            var next = index + 1;
            while (next < labels.Count && labels[next] == label) next++;
            if (TryParseKind(label, out var kind) && kind is MarkerKind.Ending or MarkerKind.PostCredits or MarkerKind.Preview)
            {
                var rangeStart = start + index * step;
                var rangeEnd = start + next * step;
                ranges.Add(new DetectedVideoRange(kind, rangeStart, rangeEnd - rangeStart,
                    KindTitle(kind), $"Покадровая классификация: кадры {index + 1}–{next} из {frameCount}.", 0.78));
            }
            index = next;
        }
        var summary = document.RootElement.TryGetProperty("summary", out var summaryElement)
            ? summaryElement.GetString() ?? string.Empty
            : string.Empty;
        return new AiAnalysisEnhancement(summary, ranges, model, true);
    }

    private static string NormalizeFrameLabel(string? value)
    {
        var label = (value ?? string.Empty).Trim().ToLowerInvariant().Replace("-", string.Empty).Replace("_", string.Empty);
        return label switch
        {
            "ending" or "credits" or "эндинг" or "титры" => "ending",
            "postcredits" or "postcredit" or "послетитров" => "postcredits",
            "preview" or "nextpreview" or "превью" or "анонс" => "preview",
            _ => "episode"
        };
    }

    private static List<string> StabilizeFrameLabels(IReadOnlyList<string> labels)
    {
        var result = labels.ToList();
        for (var index = 1; index < result.Count - 1; index++)
        {
            if (result[index - 1] == result[index + 1] && result[index] != result[index - 1])
            {
                result[index] = result[index - 1];
            }
        }
        return result;
    }

    private static string BuildSystemPrompt()
        => """
           /no_think
           Не выводи рассуждения и теги <think>. Сразу верни финальный JSON.
           Ты анализатор структуры видео для монтажной программы. Отвечай только одним JSON-объектом без Markdown.
           Формат: {"summary":"краткий вывод на русском","segments":[{"type":"opening|ending|postcredits|preview|recap|note","start":0.0,"end":1.0,"title":"название","description":"обоснование","confidence":0.0}]}.
           start и end — абсолютные секунды исходного файла. Это многошаговая проверка: сначала найди нужный блок по обзорному листу, затем уточни начало и конец по крупным листам кандидатов и ближайшим событиям FFmpeg. Не копируй грубые границы кандидата автоматически. Опенинг и эндинг обычно длятся 60–120 секунд и не должны быть длиннее 180 секунд, сцена после титров — 180 секунд, превью — 90 секунд. Не добавляй обычные монтажные склейки — они уже обнаружены отдельно. Если доказательств недостаточно, пропусти сегмент или укажи низкую confidence. Для anime ищи опенинг, эндинг, сцену после титров, рекап и превью следующей серии.
           """;

    private static string BuildUserPrompt(
        MediaAsset asset,
        VideoAnalysisResult baseline,
        string query,
        IReadOnlyList<ContactSheetSpec> sheetSpecs,
        bool supportsVision)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Файл: {asset.Name}");
        builder.AppendLine($"Запрос пользователя: {query}");
        builder.AppendLine($"Анализируемый диапазон: {Format(baseline.SourceStart)}–{Format(baseline.SourceEnd)} секунд.");
        builder.AppendLine($"Техническая сводка: {baseline.Summary}");
        builder.AppendLine("Обнаруженные диапазоны:");
        foreach (var range in baseline.Ranges.Take(90))
        {
            builder.AppendLine(
                $"- {range.Kind}: {Format(range.SourceStart)}–{Format(range.SourceStart + range.Duration)}, {range.Title}, confidence {Format(range.Confidence)}");
        }

        if (supportsVision)
        {
            builder.AppendLine("Прикреплены контактные листы; на каждом кадры идут слева направо и сверху вниз:");
            for (var index = 0; index < sheetSpecs.Count; index++)
            {
                var sheet = sheetSpecs[index];
                builder.AppendLine(
                    $"- изображение {index + 1}: {sheet.Label}, {Format(sheet.Start)}–{Format(sheet.End)} сек., " +
                    $"{sheet.FrameCount} равномерных кадров;");
            }
            builder.AppendLine("Используй изображения для смысловой оценки, а события FFmpeg — для уточнения границ.");
        }
        else
        {
            builder.AppendLine("У модели нет зрения. Используй только техническую сводку и не утверждай содержание кадров с высокой уверенностью.");
        }

        return builder.ToString();
    }

    private static IReadOnlyList<ContactSheetSpec> BuildContactSheetSpecs(VideoAnalysisResult baseline)
    {
        var sourceStart = baseline.SourceStart;
        var sourceEnd = baseline.SourceEnd;
        var duration = sourceEnd - sourceStart;
        var sheets = new List<ContactSheetSpec>
        {
            new(sourceStart, sourceEnd, duration >= 4 ? 16 : 4, "весь анализируемый диапазон")
        };
        if (duration >= 240)
        {
            AddCandidateSheet(sheets, baseline, MarkerKind.Opening, "кандидат опенинга крупнее");
            AddCandidateSheet(sheets, baseline, MarkerKind.Ending, "кандидат эндинга крупнее");
            AddCandidateSheet(sheets, baseline, MarkerKind.PostCredits, "зона после титров крупнее");
            AddCandidateSheet(sheets, baseline, MarkerKind.Preview, "финальное превью крупнее");

            if (sheets.Count == 1)
            {
                var detailDuration = Math.Min(300, duration * 0.3);
                sheets.Add(new(sourceStart, sourceStart + detailDuration, 16, "начало эпизода крупнее"));
                sheets.Add(new(sourceEnd - detailDuration, sourceEnd, 16, "финал эпизода крупнее"));
            }
        }
        return sheets.Take(5).ToList();
    }

    private static IReadOnlyList<ContactSheetSpec> BuildGameplayContactSheetSpecs(VideoAnalysisResult baseline)
    {
        var duration = Math.Max(0.1, baseline.SourceEnd - baseline.SourceStart);
        var count = Math.Clamp((int)Math.Ceiling(duration / 300), 1, 8);
        var window = duration / count;
        var specs = new List<ContactSheetSpec>(count);
        for (var index = 0; index < count; index++)
        {
            var start = baseline.SourceStart + index * window;
            var end = index == count - 1 ? baseline.SourceEnd : Math.Min(baseline.SourceEnd, start + window);
            specs.Add(new ContactSheetSpec(start, end, duration >= 3 ? 16 : 4, $"обзор материала {index + 1}"));
        }
        return specs;
    }

    private static ImmutableArray<CoreAnalysisSegment> ParseGameplaySegments(
        string responseJson,
        Guid sourceId,
        VideoAnalysisResult baseline,
        CoreGameEditingProfile profile)
    {
        using var envelope = JsonDocument.Parse(responseJson);
        if (!envelope.RootElement.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var contentElement))
            throw new InvalidOperationException("ИИ вернул анализ материала без результата.");
        var raw = contentElement.GetString() ?? string.Empty;
        if (!raw.Contains('{') && message.TryGetProperty("thinking", out var thinking) &&
            thinking.ValueKind == JsonValueKind.String)
            raw = thinking.GetString() ?? raw;
        using var document = JsonDocument.Parse(ExtractJson(raw));
        if (!document.RootElement.TryGetProperty("segments", out var elements) ||
            elements.ValueKind != JsonValueKind.Array)
            return [];

        var allowedTags = profile.EventTags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var segments = ImmutableArray.CreateBuilder<CoreAnalysisSegment>();
        foreach (var element in elements.EnumerateArray())
        {
            if (!TryGetNumber(element, "start", out var start) || !TryGetNumber(element, "end", out var end))
                continue;
            start = Math.Clamp(start, baseline.SourceStart, baseline.SourceEnd);
            end = Math.Clamp(end, baseline.SourceStart, baseline.SourceEnd);
            if (end <= start + 0.1) continue;
            start = SnapToDetectedBoundary(start, baseline, 8);
            end = SnapToDetectedBoundary(end, baseline, 8);
            if (end <= start + 0.1) continue;

            var tags = ImmutableDictionary.CreateBuilder<string, double>(StringComparer.OrdinalIgnoreCase);
            if (element.TryGetProperty("tags", out var tagsElement) && tagsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in tagsElement.EnumerateObject())
                {
                    if (!allowedTags.Contains(property.Name) || !TryReadNumber(property.Value, out var score)) continue;
                    tags[property.Name] = Math.Clamp(score, 0.05, 1);
                }
            }
            if (tags.Count == 0) continue;

            var confidence = TryGetNumber(element, "confidence", out var parsedConfidence)
                ? Math.Clamp(parsedConfidence, 0.05, 0.98)
                : 0.6;
            var title = GetString(element, "title");
            var description = GetString(element, "description");
            var summary = string.IsNullOrWhiteSpace(description)
                ? string.IsNullOrWhiteSpace(title) ? "Игровое событие подтверждено кадрами." : title
                : description;
            segments.Add(new CoreAnalysisSegment(
                Guid.NewGuid(),
                sourceId,
                new CoreTimeRange(CoreTimelineTime.FromSeconds(start), CoreTimelineTime.FromSeconds(end - start)),
                tags.ContainsKey("teamfight") || tags.ContainsKey("pvp") || tags.ContainsKey("boss") ? 0.9 : 0.65,
                0.55,
                0,
                string.Empty,
                tags.ToImmutable(),
                confidence,
                [new CoreAnalysisEvidence(CoreMontageEvidenceKind.Vision, summary, title)]));
        }
        return segments
            .OrderBy(item => item.SourceRange.Start)
            .ThenByDescending(item => item.Confidence)
            .ToImmutableArray();
    }

    private static bool TryReadNumber(JsonElement element, out double value)
    {
        value = 0;
        if (element.ValueKind == JsonValueKind.Number) return element.TryGetDouble(out value);
        return element.ValueKind == JsonValueKind.String &&
               double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static double ScoreForPrompt(CoreAnalysisSegment segment, CoreGameEditingProfile profile)
    {
        var tags = segment.Tags.Sum(item =>
            profile.EventWeights.TryGetValue(item.Key, out var weight) ? item.Value * weight : item.Value * 0.15);
        return tags + segment.MotionScore * 0.2 + segment.LoudnessScore * 0.1 +
               segment.SpeechScore * 0.15 + segment.Confidence * 0.1;
    }

    private static CoreMontageRole ParseMontageRole(string value) => value.Trim().ToLowerInvariant() switch
    {
        "hook" => CoreMontageRole.Hook,
        "setup" => CoreMontageRole.Setup,
        "payoff" => CoreMontageRole.Payoff,
        "ending" => CoreMontageRole.Ending,
        _ => CoreMontageRole.Development
    };

    private static CoreTransitionKind? ParseMontageTransition(string value) => value.Trim().ToLowerInvariant() switch
    {
        "cross_dissolve" or "crossdissolve" => CoreTransitionKind.CrossDissolve,
        "dip_to_black" or "diptoblack" => CoreTransitionKind.DipToBlack,
        _ => null
    };

    private static void AddCandidateSheet(
        ICollection<ContactSheetSpec> sheets,
        VideoAnalysisResult baseline,
        MarkerKind kind,
        string label)
    {
        var candidate = baseline.Ranges.FirstOrDefault(range => range.Kind == kind);
        if (candidate is null)
        {
            return;
        }

        var margin = kind is MarkerKind.Opening or MarkerKind.Ending ? 28 : 16;
        var start = Math.Max(baseline.SourceStart, candidate.SourceStart - margin);
        var end = Math.Min(baseline.SourceEnd, candidate.SourceStart + candidate.Duration + margin);
        if (end - start >= 4)
        {
            sheets.Add(new ContactSheetSpec(start, end, 16, label));
        }
    }

    private static AiAnalysisEnhancement ParseEnhancement(
        string responseJson,
        VideoAnalysisResult baseline,
        string model,
        bool usedVision)
    {
        using var envelope = JsonDocument.Parse(responseJson);
        if (!envelope.RootElement.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var contentElement))
        {
            throw new InvalidOperationException("Локальный ИИ вернул ответ без результата.");
        }

        var rawContent = contentElement.GetString() ?? string.Empty;
        if (!rawContent.Contains('{') &&
            message.TryGetProperty("thinking", out var thinkingElement) &&
            thinkingElement.ValueKind == JsonValueKind.String)
        {
            rawContent = thinkingElement.GetString() ?? rawContent;
        }
        var content = ExtractJson(rawContent);
        using var result = JsonDocument.Parse(content);
        var summary = result.RootElement.TryGetProperty("summary", out var summaryElement)
            ? summaryElement.GetString() ?? string.Empty
            : string.Empty;
        var ranges = new List<DetectedVideoRange>();
        if (result.RootElement.TryGetProperty("segments", out var segmentsElement) &&
            segmentsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var segment in segmentsElement.EnumerateArray())
            {
                if (!TryParseKind(GetString(segment, "type"), out var kind) ||
                    !TryGetNumber(segment, "start", out var start) ||
                    !TryGetNumber(segment, "end", out var end))
                {
                    continue;
                }

                var rangeDuration = baseline.SourceEnd - baseline.SourceStart;
                if (baseline.SourceStart > 0.01 && start < baseline.SourceStart - 0.5 && end <= rangeDuration + 0.5)
                {
                    start += baseline.SourceStart;
                    end += baseline.SourceStart;
                }

                start = Math.Clamp(start, baseline.SourceStart, baseline.SourceEnd);
                end = Math.Clamp(end, baseline.SourceStart, baseline.SourceEnd);
                var confidence = TryGetNumber(segment, "confidence", out var confidenceValue)
                    ? Math.Clamp(confidenceValue, 0.05, 0.99)
                    : 0.5;
                if (!TryNormalizeSemanticRange(kind, baseline, ref start, ref end, ref confidence))
                {
                    continue;
                }
                if (end <= start + 0.1)
                {
                    continue;
                }

                var title = GetString(segment, "title");
                var description = GetString(segment, "description");
                ranges.Add(new DetectedVideoRange(
                    kind,
                    start,
                    end - start,
                    string.IsNullOrWhiteSpace(title) ? KindTitle(kind) : title,
                    description,
                    confidence));
            }
        }

        var normalizedRanges = NormalizeSemanticRelationships(ranges);
        return new AiAnalysisEnhancement(
            BuildValidatedSummary(normalizedRanges, summary),
            normalizedRanges,
            model,
            usedVision);
    }

    private static string BuildValidatedSummary(
        IReadOnlyList<DetectedVideoRange> ranges,
        string fallback)
    {
        if (ranges.Count == 0)
        {
            return string.IsNullOrWhiteSpace(fallback)
                ? "ИИ-сервер не подтвердил смысловые сегменты."
                : fallback;
        }

        var items = ranges.Select(range =>
            $"{KindTitle(range.Kind).ToLowerInvariant()} {Format(range.SourceStart)}–{Format(range.SourceStart + range.Duration)} сек.");
        return $"Серверная vision-проверка подтвердила: {string.Join("; ", items)}";
    }

    private static IReadOnlyList<DetectedVideoRange> NormalizeSemanticRelationships(
        IReadOnlyList<DetectedVideoRange> ranges)
    {
        var normalized = ranges
            .GroupBy(range => range.Kind)
            .Select(group => group.OrderByDescending(range => range.Confidence).First())
            .OrderBy(range => range.SourceStart)
            .ToList();

        var opening = normalized.FirstOrDefault(range => range.Kind == MarkerKind.Opening);
        var recapIndex = normalized.FindIndex(range => range.Kind == MarkerKind.Recap);
        if (opening is not null && recapIndex >= 0)
        {
            var recap = normalized[recapIndex];
            var recapEnd = Math.Min(recap.SourceStart + recap.Duration, opening.SourceStart);
            if (recapEnd - recap.SourceStart >= 3)
            {
                normalized[recapIndex] = recap with { Duration = recapEnd - recap.SourceStart };
            }
            else
            {
                normalized.RemoveAt(recapIndex);
            }
        }

        var ending = normalized.FirstOrDefault(range => range.Kind == MarkerKind.Ending);
        var postCredits = normalized.FirstOrDefault(range => range.Kind == MarkerKind.PostCredits);
        if (ending is not null && postCredits is not null)
        {
            var endingEnd = ending.SourceStart + ending.Duration;
            if (postCredits.SourceStart < endingEnd - 0.5)
            {
                normalized.Remove(postCredits);
            }
        }

        ending = normalized.FirstOrDefault(range => range.Kind == MarkerKind.Ending);
        var preview = normalized.FirstOrDefault(range => range.Kind == MarkerKind.Preview);
        if (ending is not null && preview is not null && preview.SourceStart < ending.SourceStart + ending.Duration - 0.5)
        {
            normalized.Remove(preview.Confidence <= ending.Confidence ? preview : ending);
        }

        postCredits = normalized.FirstOrDefault(range => range.Kind == MarkerKind.PostCredits);
        preview = normalized.FirstOrDefault(range => range.Kind == MarkerKind.Preview);
        if (postCredits is not null && preview is not null && preview.SourceStart < postCredits.SourceStart + postCredits.Duration - 0.5)
        {
            normalized.Remove(preview.Confidence <= postCredits.Confidence ? preview : postCredits);
        }

        return normalized.OrderBy(range => range.SourceStart).ThenBy(range => range.Kind).ToList();
    }

    private static bool TryNormalizeSemanticRange(
        MarkerKind kind,
        VideoAnalysisResult baseline,
        ref double start,
        ref double end,
        ref double confidence)
    {
        var candidates = baseline.Ranges.Where(range => range.Kind == kind).ToList();
        if (candidates.Count > 0)
        {
            var proposedStart = start;
            var proposedEnd = end;
            var candidate = candidates
                .OrderByDescending(range => Math.Max(
                    0,
                    Math.Min(proposedEnd, range.SourceStart + range.Duration) - Math.Max(proposedStart, range.SourceStart)))
                .ThenBy(range => Math.Abs(range.SourceStart - proposedStart))
                .First();
            var candidateStart = candidate.SourceStart;
            var candidateEnd = candidate.SourceStart + candidate.Duration;
            var closeToCandidate = proposedEnd >= candidateStart - 45 && proposedStart <= candidateEnd + 45;
            if (!closeToCandidate)
            {
                start = candidateStart;
                end = candidateEnd;
                confidence = Math.Min(confidence, 0.48);
                return true;
            }

            start = SnapToDetectedBoundary(proposedStart, baseline, 10);
            end = SnapToDetectedBoundary(proposedEnd, baseline, 10);
            var duration = end - start;
            var plausibleDuration = kind switch
            {
                MarkerKind.Opening or MarkerKind.Ending => duration is >= 45 and <= 180,
                MarkerKind.Preview => duration is >= 4 and <= 90,
                MarkerKind.PostCredits => duration is >= 3 and <= 180,
                MarkerKind.Recap or MarkerKind.Note => duration is >= 3 and <= 300,
                _ => duration is > 0.1 and <= 180
            };
            if (!plausibleDuration)
            {
                start = candidateStart;
                end = candidateEnd;
                confidence = Math.Min(confidence, 0.5);
                return true;
            }

            confidence = Math.Clamp(confidence * 0.65 + candidate.Confidence * 0.35, 0.05, 0.95);
            return true;
        }

        var maximumDuration = kind switch
        {
            MarkerKind.Opening or MarkerKind.Ending or MarkerKind.PostCredits => 180,
            MarkerKind.Preview => 90,
            MarkerKind.Recap or MarkerKind.Note => 300,
            _ => 180
        };
        if (end - start > maximumDuration)
        {
            return false;
        }

        confidence = Math.Min(confidence, 0.72);
        return true;
    }

    private static double SnapToDetectedBoundary(double target, VideoAnalysisResult baseline, double radius)
    {
        var blackBoundary = baseline.Ranges
            .Where(range => range.Kind == MarkerKind.BlackFrame)
            .SelectMany(range => new[] { range.SourceStart, range.SourceStart + range.Duration })
            .Where(time => Math.Abs(time - target) <= Math.Min(radius, 5))
            .OrderBy(time => Math.Abs(time - target))
            .Cast<double?>()
            .FirstOrDefault();
        if (blackBoundary.HasValue)
        {
            return blackBoundary.Value;
        }

        var sceneBoundary = baseline.Ranges
            .Where(range => range.Kind == MarkerKind.Scene)
            .SelectMany(range => new[] { range.SourceStart, range.SourceStart + range.Duration })
            .Where(time => Math.Abs(time - target) <= radius)
            .OrderBy(time => Math.Abs(time - target))
            .Cast<double?>()
            .FirstOrDefault();
        return sceneBoundary ?? target;
    }

    private static bool TryParseKind(string value, out MarkerKind kind)
    {
        kind = value.Trim().ToLowerInvariant() switch
        {
            "opening" or "опенинг" => MarkerKind.Opening,
            "ending" or "эндинг" => MarkerKind.Ending,
            "postcredits" or "post-credits" or "после титров" => MarkerKind.PostCredits,
            "preview" or "превью" => MarkerKind.Preview,
            "recap" or "рекап" => MarkerKind.Recap,
            "note" or "эпизод" or "момент" => MarkerKind.Note,
            _ => (MarkerKind)(-1)
        };
        return Enum.IsDefined(kind);
    }

    private static string KindTitle(MarkerKind kind) => kind switch
    {
        MarkerKind.Opening => "Опенинг",
        MarkerKind.Ending => "Эндинг",
        MarkerKind.PostCredits => "Сцена после титров",
        MarkerKind.Preview => "Превью следующей серии",
        MarkerKind.Recap => "Рекап",
        _ => "Заметка ИИ"
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
        CancellationToken cancellationToken)
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
                maxTokens
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
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException(
                $"ИИ вернул пустой структурированный ответ. done_reason={doneReason ?? "unknown"}, eval_count={evalCount}.");
        }

        return new StructuredInferenceResult(content, doneReason, evalCount);
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
            contextTokens,
            maxTokens,
            cancellationToken).ConfigureAwait(false);
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
    private sealed record StructuredInferenceResult(string Content, string? DoneReason, int EvalCount);
}

public sealed record AiServerClientOptions(
    Uri? Endpoint = null,
    string? ApiKey = null,
    string? PreferredModel = null)
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

        return new AiServerClientOptions(endpoint, apiKey?.Trim(), preferred.Trim());
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
