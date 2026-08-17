using System.Diagnostics;
using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
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

public sealed class OllamaVideoAnalysisService : IDisposable
{
    private const string WorkspaceHost = "127.0.0.1:11435";
    private static readonly Uri ApiBaseAddress = new($"http://{WorkspaceHost}/");
    private static readonly Regex EpisodeTitlePattern = new(
        @"(?i)\b(?:сер(?:ия|ии)|эпизод|episode|next|следующ\w*)\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly FfmpegLocator _ffmpegLocator;
    private readonly ProcessRunner _processRunner;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private Process? _serverProcess;
    private string? _modelRoot;

    public OllamaVideoAnalysisService(FfmpegLocator ffmpegLocator, ProcessRunner processRunner)
    {
        _ffmpegLocator = ffmpegLocator;
        _processRunner = processRunner;
        _httpClient = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = ApiBaseAddress,
            Timeout = TimeSpan.FromMinutes(15)
        };
    }

    public string ModelRoot => _modelRoot ??= ResolveModelRoot();

    public async Task<IReadOnlyList<OllamaModelInfo>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureServerAsync(cancellationToken);
        using var response = await _httpClient.GetAsync("api/tags", cancellationToken);
        await EnsureSuccessAsync(response, "Не удалось получить список локальных моделей", cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("models", out var modelsElement))
        {
            return Array.Empty<OllamaModelInfo>();
        }

        var models = new List<OllamaModelInfo>();
        foreach (var modelElement in modelsElement.EnumerateArray())
        {
            var name = modelElement.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var size = modelElement.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var value)
                ? value
                : 0;
            var capabilities = await GetCapabilitiesAsync(name, cancellationToken);
            models.Add(new OllamaModelInfo(name, size, capabilities.Contains("vision", StringComparer.OrdinalIgnoreCase)));
        }

        return models
            .OrderByDescending(model => model.SupportsVision)
            .ThenBy(model => model.SizeBytes)
            .ToList();
    }

    public async Task<OllamaAnalysisEnhancement> EnhanceAsync(
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

            progress?.Report(new VideoAnalysisProgress(96, $"Шаг 5/5: смысловая проверка локальным ИИ ({model})"));
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
            using var response = await _httpClient.PostAsJsonAsync(
                "api/chat",
                new
                {
                    model,
                    stream = false,
                    think = false,
                    format = "json",
                    messages,
                    options = new { temperature = 0.1, num_ctx = 16384, num_predict = 4096 }
                },
                cancellationToken);
            await EnsureSuccessAsync(response, $"Локальная модель {model} не выполнила анализ", cancellationToken);
            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
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

    public async Task<ImmutableArray<CoreAnalysisSegment>> AnalyzeGameplayAsync(
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
                        "Ты анализатор игрового видео. Верни только JSON без Markdown: " +
                        "{\"segments\":[{\"start\":0.0,\"end\":3.0,\"title\":\"событие\",\"description\":\"что видно\"," +
                        "\"confidence\":0.8,\"tags\":{\"tag\":0.9}}]}. " +
                        "start/end — абсолютные секунды исходника. Используй только события, которые видны на кадрах; " +
                        "не выдумывай содержание между редкими кадрами. Один сегмент должен описывать одно событие."
                },
                new
                {
                    role = "user",
                    content =
                        $"Игра/профиль: {profile.DisplayName} ({profile.GameFamily}).\n" +
                        $"Искомые теги: {tags}.\nПравила монтажа: {profile.PlanningGuidance}\n" +
                        $"Диапазон: {Format(baseline.SourceStart)}–{Format(baseline.SourceEnd)} сек.\n" +
                        $"Контактные листы:\n{sheetDescription}\n" +
                        "Отмечай только уверенно распознанные игровые события и интерфейсные признаки.",
                    images = images.ToArray()
                }
            };
            using var response = await _httpClient.PostAsJsonAsync(
                "api/chat",
                new
                {
                    model,
                    stream = false,
                    think = false,
                    format = "json",
                    messages,
                    options = new { temperature = 0.08, num_ctx = 16384, num_predict = 4096 }
                },
                cancellationToken);
            await EnsureSuccessAsync(response, $"Локальная модель {model} не выполнила игровой анализ", cancellationToken);
            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
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
                $"Уточнение игровых границ FFmpeg: {completed + 1}/{selected.Count}"));
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
                "Границы уточнены плотным FFmpeg-проходом около игрового события.",
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

        using var response = await _httpClient.PostAsJsonAsync(
            "api/chat",
            new
            {
                model,
                stream = false,
                think = false,
                format = "json",
                messages = new object[]
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
                },
                options = new { temperature = 0.05, num_ctx = 8192, num_predict = 1024 }
            },
            cancellationToken);
        await EnsureSuccessAsync(response, $"Локальная модель {model} не составила план монтажа", cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
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

        using var response = await _httpClient.PostAsJsonAsync(
            "api/chat",
            new
            {
                model,
                stream = false,
                think = false,
                format = "json",
                messages = new object[]
                {
                    new
                    {
                        role = "system",
                        content =
                            "Ты режиссёр игрового видео. Ты не меняешь таймлайн, а возвращаешь декларативный план JSON без Markdown: " +
                            "{\"summary\":\"...\",\"items\":[{\"segment_id\":\"32 hex\",\"role\":\"hook|setup|development|payoff|ending\"," +
                            "\"reason\":\"почему\",\"transition_after\":\"none|cross_dissolve|dip_to_black\",\"volume\":1.0,\"subtitles\":true}]}. " +
                            "Используй только переданные segment_id, каждый максимум один раз. Обязательные элементы включай всегда. " +
                            "Строй понятную причинно-следственную историю и соблюдай целевую длительность."
                    },
                    new { role = "user", content = contextText.ToString() }
                },
                options = new { temperature = 0.12, num_ctx = 16384, num_predict = 4096 }
            },
            cancellationToken);
        await EnsureSuccessAsync(response, $"Локальная модель {model} не составила план игрового монтажа", cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        using var envelope = JsonDocument.Parse(responseJson);
        var rawContent = envelope.RootElement.GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
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
            throw new InvalidOperationException("Локальная модель не выбрала ни одного допустимого фрагмента.");
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
        if (await IsServerAvailableAsync(cancellationToken))
        {
            return;
        }

        await _startGate.WaitAsync(cancellationToken);
        try
        {
            if (await IsServerAvailableAsync(cancellationToken))
            {
                return;
            }

            var executable = FindOllamaExecutable();
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("serve");
            startInfo.Environment["OLLAMA_HOST"] = WorkspaceHost;
            startInfo.Environment["OLLAMA_MODELS"] = ModelRoot;
            _serverProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Не удалось запустить локальный Ollama для Kadr Studio.");

            for (var attempt = 0; attempt < 40; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(250, cancellationToken);
                if (await IsServerAvailableAsync(cancellationToken))
                {
                    return;
                }

                if (_serverProcess.HasExited)
                {
                    break;
                }
            }

            throw new InvalidOperationException(
                "Локальный Ollama не запустился. Проверьте, что порт 11435 свободен и Ollama установлен.");
        }
        finally
        {
            _startGate.Release();
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _startGate.Dispose();
        if (_serverProcess is null)
        {
            return;
        }

        try
        {
            if (!_serverProcess.HasExited)
            {
                _serverProcess.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Сервер мог быть уже остановлен пользователем или системой.
        }
        finally
        {
            _serverProcess.Dispose();
        }
    }

    private async Task<IReadOnlyList<string>> GetCapabilitiesAsync(string model, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/show", new { model }, cancellationToken);
        await EnsureSuccessAsync(response, $"Не удалось проверить модель {model}", cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("capabilities", out var capabilitiesElement))
        {
            return Array.Empty<string>();
        }

        return capabilitiesElement
            .EnumerateArray()
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToList();
    }

    private async Task<bool> IsServerAvailableAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1.5));
        try
        {
            using var response = await _httpClient.GetAsync("api/version", timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
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

    private async Task<OllamaAnalysisEnhancement> RefineFinalStructureAsync(
        MediaAsset asset,
        VideoAnalysisResult baseline,
        OllamaAnalysisEnhancement firstPass,
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
            using var response = await _httpClient.PostAsJsonAsync(
                "api/chat",
                new
                {
                    model,
                    stream = false,
                    think = false,
                    format = "json",
                    messages,
                    options = new { temperature = 0.0, num_ctx = 16384, num_predict = 2048 }
                }, cancellationToken);
            await EnsureSuccessAsync(response, "Локальный ИИ не смог отдельно проверить финал", cancellationToken);
            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
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

    private static OllamaAnalysisEnhancement ParseFrameClassifications(
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
            return new OllamaAnalysisEnhancement(string.Empty, Array.Empty<DetectedVideoRange>(), model, true);
        }
        var content = ExtractJson(contentElement.GetString() ?? string.Empty);
        using var document = JsonDocument.Parse(content);
        if (!document.RootElement.TryGetProperty("labels", out var labelsElement) || labelsElement.ValueKind != JsonValueKind.Array)
        {
            return new OllamaAnalysisEnhancement(string.Empty, Array.Empty<DetectedVideoRange>(), model, true);
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
            return new OllamaAnalysisEnhancement(
                "Покадровая проверка финала отклонена: модель не подтвердила непрерывную титровую последовательность.",
                [fallbackEnding], model, true);
        }
        var firstEndingIndex = endingIndexes.First();
        var lastEndingIndex = endingIndexes.Last();
        if (firstEndingIndex == 0 && fallbackEnding.SourceStart - start > 20)
        {
            return new OllamaAnalysisEnhancement(
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
        return new OllamaAnalysisEnhancement(summary, ranges, model, true);
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
            specs.Add(new ContactSheetSpec(start, end, duration >= 3 ? 16 : 4, $"игровой обзор {index + 1}"));
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
            throw new InvalidOperationException("Локальный ИИ вернул игровой анализ без результата.");
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

    private static OllamaAnalysisEnhancement ParseEnhancement(
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
        return new OllamaAnalysisEnhancement(
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
                ? "Локальный ИИ не подтвердил смысловые сегменты."
                : fallback;
        }

        var items = ranges.Select(range =>
            $"{KindTitle(range.Kind).ToLowerInvariant()} {Format(range.SourceStart)}–{Format(range.SourceStart + range.Duration)} сек.");
        return $"Локальная vision-проверка подтвердила: {string.Join("; ", items)}";
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
        _ => "Заметка локального ИИ"
    };

    private static string ExtractJson(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            var preview = content.Length > 240 ? content[..240] + "…" : content;
            throw new InvalidOperationException(
                $"Локальный ИИ вернул результат не в формате JSON. Ответ: {preview}");
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

    private static string ResolveModelRoot()
    {
        var configured = Environment.GetEnvironmentVariable("KADR_STUDIO_OLLAMA_MODELS");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            Directory.CreateDirectory(configured);
            return Path.GetFullPath(configured);
        }

        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = new DirectoryInfo(start);
            for (var level = 0; directory is not null && level < 8; level++, directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, ".ollama", "models");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new DirectoryNotFoundException(
            "Не найдена папка моделей проекта .ollama\\models. Она должна лежать рядом с проектом Kadr Studio.");
    }

    private static string FindOllamaExecutable()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe")
        };
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim(), "ollama.exe")));
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("Ollama не установлен. Установите Ollama и повторите анализ.");
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
}

public sealed record OllamaModelInfo(string Name, long SizeBytes, bool SupportsVision)
{
    public string DisplayName => $"{Name} · {(SupportsVision ? "видит кадры" : "текст")} · {SizeBytes / 1024d / 1024d / 1024d:0.0} ГБ";
}

public sealed record OllamaAnalysisEnhancement(
    string Summary,
    IReadOnlyList<DetectedVideoRange> Ranges,
    string Model,
    bool UsedVision);
