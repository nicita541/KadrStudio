using System.Globalization;
using KadrStudio.Models;

namespace KadrStudio.Services;

public sealed class ExportService(FfmpegLocator locator, ProcessRunner processRunner)
{
    public async Task ExportAsync(
        EditorProject project,
        string outputPath,
        ExportSettings settings,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        locator.EnsureAvailable();
        var visualClips = project.GetVisualClips();
        if (visualClips.Count == 0)
        {
            throw new InvalidOperationException("Добавьте хотя бы одно видео или изображение на таймлайн.");
        }
        ValidateSources(project);

        var fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"KadrExport-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var temporaryOutput = Path.Combine(temporaryDirectory, "render.mp4");
        try
        {
            progress?.Report(new ExportProgress(2, "Подготовка", "Построение многодорожечного проекта"));
            var useHardware = settings.UseHardwareEncoding && await CanUseNvencAsync(cancellationToken);
            if (settings.UseHardwareEncoding && !useHardware)
            {
                progress?.Report(new ExportProgress(3, "Подготовка", "NVENC недоступен — используется процессор"));
            }

            var arguments = BuildTimelineArguments(project, temporaryOutput, settings, useHardware);
            var result = await processRunner.RunAsync(
                locator.FfmpegPath,
                arguments,
                line =>
                {
                    if (FfmpegOutput.TryParseTime(line, out var seconds))
                    {
                        var ratio = Math.Clamp(seconds / Math.Max(0.1, project.Duration), 0, 1);
                        progress?.Report(new ExportProgress(5 + ratio * 93, "Экспорт дорожек", FormatProgressTime(seconds, project.Duration)));
                    }
                },
                cancellationToken);
            EnsureSuccess(result, "Не удалось экспортировать многодорожечный проект");
            File.Move(temporaryOutput, fullOutputPath, overwrite: true);
            progress?.Report(new ExportProgress(100, "Готово", Path.GetFileName(fullOutputPath)));
        }
        finally
        {
            TryDeleteTemporaryDirectory(temporaryDirectory);
        }
    }

    private async Task<bool> CanUseNvencAsync(CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            locator.FfmpegPath,
            [
                "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "color=black:size=320x240:duration=0.1",
                "-c:v", "h264_nvenc", "-f", "null", "-"
            ],
            cancellationToken: cancellationToken);
        return result.ExitCode == 0;
    }

    private static IReadOnlyList<string> BuildTimelineArguments(
        EditorProject project,
        string outputPath,
        ExportSettings settings,
        bool useHardware)
    {
        var visualClips = project.GetVisualClips()
            .OrderBy(clip => clip.TrackIndex)
            .ThenBy(clip => clip.Start)
            .ToList();
        var audioOnlyClips = project.GetAudioClips()
            .OrderBy(clip => clip.TrackIndex)
            .ThenBy(clip => clip.Start)
            .ToList();
        var inputs = new List<InputClip>();
        var arguments = new List<string> { "-hide_banner", "-y" };

        foreach (var clip in visualClips.Concat(audioOnlyClips))
        {
            var asset = project.FindAsset(clip.AssetId)!;
            if (asset.Kind == MediaKind.Image)
            {
                arguments.AddRange(["-loop", "1", "-framerate", "30", "-t", Format(clip.Duration), "-i", asset.Path]);
            }
            else
            {
                arguments.AddRange(["-ss", Format(clip.SourceStart), "-t", Format(clip.Duration), "-i", asset.Path]);
            }
            inputs.Add(new InputClip(clip, asset, inputs.Count));
        }

        var (width, height) = settings.GetSize();
        var duration = Math.Max(0.1, project.Duration);
        var filters = new List<string>
        {
            $"color=c=black:s={width}x{height}:r=30:d={Format(duration)},format=rgba[base0]"
        };
        var previousVideo = "base0";
        var videoLayer = 0;
        foreach (var input in inputs.Where(input => input.Clip.Track == TrackKind.Visual))
        {
            var videoLabel = $"video{videoLayer}";
            var colorFilters = BuildColorFilters(input.Clip);
            filters.Add(
                $"[{input.Index}:v:0]scale={width}:{height}:force_original_aspect_ratio=decrease," +
                $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:black,setsar=1,fps=30,format=rgba," +
                colorFilters +
                $"setpts=PTS-STARTPTS+{Format(input.Clip.Start)}/TB[{videoLabel}]");
            var outputLabel = $"layer{videoLayer}";
            filters.Add(
                $"[{previousVideo}][{videoLabel}]overlay=0:0:eof_action=pass:shortest=0:" +
                $"enable='between(t,{Format(input.Clip.Start)},{Format(input.Clip.End)})'[{outputLabel}]");
            previousVideo = outputLabel;
            videoLayer++;
        }
        var textLayer = 0;
        foreach (var overlay in project.TextOverlays.OrderBy(overlay => overlay.Start))
        {
            var fontSize = Math.Max(10, overlay.FontSize * height / 540.0);
            var textSource = $"textsrc{textLayer}";
            var textOutput = $"textlayer{textLayer}";
            var fontPath = ResolveFontFile(overlay.FontFamily);
            var fontColor = NormalizeFontColor(overlay.Color);
            var box = overlay.IsSubtitle ? ":box=1:boxcolor=black@0.58:boxborderw=10" : string.Empty;
            filters.Add(
                $"color=c=black@0:s={width}x{height}:r=30:d={Format(duration)},format=rgba," +
                $"drawtext=fontfile='{fontPath}':text='{EscapeDrawText(overlay.Text)}':expansion=none:" +
                $"fontsize={Format(fontSize)}:fontcolor={fontColor}:borderw=2:bordercolor=black@0.9{box}:" +
                $"x=max(0\\,min(w-text_w\\,w*{Format(overlay.X)}-text_w/2)):" +
                $"y=max(0\\,min(h-text_h\\,h*{Format(overlay.Y)}-text_h/2))[{textSource}]");
            var sourceForOverlay = textSource;
            if (Math.Abs(overlay.Rotation) > 0.01)
            {
                var rotated = $"textrot{textLayer}";
                filters.Add($"[{textSource}]rotate={Format(overlay.Rotation * Math.PI / 180)}:c=none:ow=iw:oh=ih[{rotated}]");
                sourceForOverlay = rotated;
            }
            filters.Add(
                $"[{previousVideo}][{sourceForOverlay}]overlay=0:0:eof_action=pass:shortest=0:" +
                $"enable='between(t,{Format(overlay.Start)},{Format(overlay.End)})'[{textOutput}]");
            previousVideo = textOutput;
            textLayer++;
        }
        filters.Add($"[{previousVideo}]format=yuv420p[vout]");

        filters.Add($"anullsrc=channel_layout=stereo:sample_rate=48000:d={Format(duration)}[asilence]");
        var audioLabels = new List<string> { "[asilence]" };
        var audioIndex = 0;
        foreach (var input in inputs.Where(input =>
                     input.Clip.Track == TrackKind.Audio &&
                     input.Asset.HasAudio &&
                     input.Asset.Kind is MediaKind.Video or MediaKind.Audio))
        {
            var label = $"audio{audioIndex++}";
            var delay = Math.Max(0, (long)Math.Round(input.Clip.Start * 1000));
            var audioFilters = BuildAudioFilters(input.Clip);
            filters.Add(
                $"[{input.Index}:a:0]aresample=48000,atrim=0:{Format(input.Clip.Duration)},asetpts=PTS-STARTPTS," +
                $"volume={(input.Clip.IsMuted ? "0" : Format(input.Clip.Volume))}," +
                audioFilters +
                $"adelay={delay}|{delay}[{label}]");
            audioLabels.Add($"[{label}]");
        }
        filters.Add(
            $"{string.Concat(audioLabels)}amix=inputs={audioLabels.Count}:duration=longest:dropout_transition=0:normalize=0," +
            $"atrim=0:{Format(duration)}[aout]");

        arguments.AddRange([
            "-filter_complex", string.Join(';', filters),
            "-map", "[vout]", "-map", "[aout]",
            "-t", Format(duration)
        ]);
        if (useHardware)
        {
            arguments.AddRange(["-c:v", "h264_nvenc", "-preset", "p5", "-cq", settings.Quality.ToString(CultureInfo.InvariantCulture)]);
        }
        else
        {
            arguments.AddRange(["-c:v", "libx264", "-preset", "medium", "-crf", settings.Quality.ToString(CultureInfo.InvariantCulture)]);
        }
        arguments.AddRange([
            "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2",
            "-movflags", "+faststart", outputPath
        ]);
        return arguments;
    }

    private static void ValidateSources(EditorProject project)
    {
        var missing = project.Clips
            .Select(clip => project.FindAsset(clip.AssetId))
            .Where(asset => asset is null || !File.Exists(asset.Path))
            .Select(asset => asset?.Name ?? "Неизвестный файл")
            .Distinct()
            .ToList();
        if (missing.Count > 0)
        {
            throw new FileNotFoundException("Не найдены исходные файлы:\n" + string.Join("\n", missing));
        }
    }

    private static void EnsureSuccess(ProcessResult result, string message)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{message}.\n{FfmpegOutput.LastMeaningfulLine(result.StandardError)}");
        }
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string BuildColorFilters(TimelineClip clip)
    {
        var filters = new List<string>();
        if (Math.Abs(clip.Brightness) > 0.001 || Math.Abs(clip.Contrast - 1) > 0.001 || Math.Abs(clip.Saturation - 1) > 0.001)
        {
            filters.Add($"eq=brightness={Format(clip.Brightness)}:contrast={Format(clip.Contrast)}:saturation={Format(clip.Saturation)}");
        }
        if (Math.Abs(clip.Temperature) > 0.001)
        {
            var red = Math.Clamp(1 + clip.Temperature * 0.22, 0.5, 1.5);
            var blue = Math.Clamp(1 - clip.Temperature * 0.22, 0.5, 1.5);
            filters.Add($"colorchannelmixer=rr={Format(red)}:gg=1:bb={Format(blue)}");
        }
        return filters.Count == 0 ? string.Empty : string.Join(',', filters) + ",";
    }

    private static string BuildAudioFilters(TimelineClip clip)
    {
        var filters = new List<string>();
        if (Math.Abs(clip.Bass) > 0.01)
        {
            filters.Add($"bass=g={Format(clip.Bass)}");
        }
        if (Math.Abs(clip.Mid) > 0.01)
        {
            filters.Add($"equalizer=f=1000:t=q:w=1:g={Format(clip.Mid)}");
        }
        if (Math.Abs(clip.Treble) > 0.01)
        {
            filters.Add($"treble=g={Format(clip.Treble)}");
        }
        if (Math.Abs(clip.Pan) > 0.001)
        {
            var left = clip.Pan > 0 ? 1 - clip.Pan : 1;
            var right = clip.Pan < 0 ? 1 + clip.Pan : 1;
            filters.Add($"pan=stereo|c0={Format(left)}*c0|c1={Format(right)}*c1");
        }
        if (clip.FadeIn > 0.01)
        {
            filters.Add($"afade=t=in:st=0:d={Format(Math.Min(clip.FadeIn, clip.Duration))}");
        }
        if (clip.FadeOut > 0.01)
        {
            var fadeDuration = Math.Min(clip.FadeOut, clip.Duration);
            filters.Add($"afade=t=out:st={Format(Math.Max(0, clip.Duration - fadeDuration))}:d={Format(fadeDuration)}");
        }
        return filters.Count == 0 ? string.Empty : string.Join(',', filters) + ",";
    }

    private static string ResolveFontFile(string fontFamily)
    {
        var fileName = fontFamily.ToLowerInvariant() switch
        {
            "arial" => "arial.ttf",
            "impact" => "impact.ttf",
            "times new roman" => "times.ttf",
            _ => "segoeui.ttf"
        };
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), fileName)
            .Replace('\\', '/');
        return path.Replace(":", "\\:").Replace("'", "\\'");
    }

    private static string EscapeDrawText(string value)
        => value.Replace("\\", "\\\\")
            .Replace("'", "\\'")
            .Replace(":", "\\:")
            .Replace("\r", string.Empty)
            .Replace("\n", "\\n");

    private static string NormalizeFontColor(string value)
    {
        var trimmed = (value ?? string.Empty).Trim().TrimStart('#');
        return trimmed.Length is 6 or 8 && trimmed.All(Uri.IsHexDigit) ? $"0x{trimmed}" : "white";
    }

    private static string FormatProgressTime(double current, double total)
        => $"{TimeSpan.FromSeconds(Math.Max(0, current)).ToString(@"mm\:ss")} / " +
           TimeSpan.FromSeconds(Math.Max(0, total)).ToString(@"mm\:ss");

    private static void TryDeleteTemporaryDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Windows удалит временные файлы позднее.
        }
    }

    private sealed record InputClip(TimelineClip Clip, MediaAsset Asset, int Index);
}
