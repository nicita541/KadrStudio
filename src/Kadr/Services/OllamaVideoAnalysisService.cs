using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KadrStudio.Models;

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

    public async Task<EditCommandPlan> PlanEditsAsync(
        EditorProject project,
        string prompt,
        string model,
        TimelineClip? selectedClip,
        CancellationToken cancellationToken = default)
    {
        if (EditingCommandPlanner.TryCreateDeterministic(project, prompt, selectedClip, out var deterministic))
        {
            return deterministic;
        }

        await EnsureServerAsync(cancellationToken);
        var context = new StringBuilder();
        context.AppendLine($"Длительность проекта: {Format(project.Duration)} секунд.");
        if (selectedClip is not null)
        {
            var asset = project.FindAsset(selectedClip.AssetId);
            context.AppendLine(
                $"Выбранный клип: {asset?.Name}, дорожка {selectedClip.Track}{selectedClip.TrackIndex + 1}, " +
                $"{Format(selectedClip.Start)}–{Format(selectedClip.End)} сек.");
        }
        context.AppendLine("Смысловые метки:");
        foreach (var marker in project.Markers
                     .Where(marker => marker.Kind is MarkerKind.Opening or MarkerKind.Ending or MarkerKind.PostCredits or MarkerKind.Preview or MarkerKind.Recap or MarkerKind.Note)
                     .OrderBy(marker => marker.Start)
                     .Take(60))
        {
            context.AppendLine($"- {marker.Kind}: {Format(marker.Start)}–{Format(marker.End)}; {marker.Title}");
        }
        context.AppendLine("Клипы:");
        foreach (var clip in project.Clips.OrderBy(clip => clip.Track).ThenBy(clip => clip.TrackIndex).ThenBy(clip => clip.Start).Take(100))
        {
            context.AppendLine(
                $"- {clip.Track}{clip.TrackIndex + 1}: {Format(clip.Start)}–{Format(clip.End)}; {project.FindAsset(clip.AssetId)?.Name}");
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
                start = Math.Clamp(start, 0, project.Duration);
                end = Math.Clamp(end, 0, project.Duration);
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
                $"Не удалось подготовить кадры для локального ИИ.\n{PreviewProxyService.LastMeaningfulLine(result.StandardError)}");
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
