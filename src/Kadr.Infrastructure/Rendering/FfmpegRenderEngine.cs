using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using KadrStudio.Application.Jobs;
using KadrStudio.Application.Rendering;
using KadrStudio.Core.Domain;

namespace KadrStudio.Infrastructure.Rendering;

public sealed partial class FfmpegRenderEngine(
    string ffmpegPath,
    IRenderCommandBuilder commandBuilder,
    IBackgroundJobScheduler scheduler) : IRenderEngine
{
    private readonly string _ffmpegPath = ResolveExecutable(ffmpegPath);
    private readonly string _ffprobePath = ResolveSiblingExecutable(ffmpegPath, "ffprobe.exe");

    public async Task<string> RenderAsync(
        RenderPlan plan,
        RenderOutputOptions options,
        IProgress<RenderProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);
        var fullOutput = Path.GetFullPath(options.OutputPath);
        var directory = Path.GetDirectoryName(fullOutput)!;
        Directory.CreateDirectory(directory);
        var extension = Path.GetExtension(fullOutput);
        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileNameWithoutExtension(fullOutput)}.{Guid.NewGuid():N}.tmp{extension}");
        var temporaryOptions = options with { OutputPath = temporary };
        var command = commandBuilder.Build(plan, temporaryOptions);
        var isExport = options.Purpose == RenderPurpose.Export;
        var request = new JobRequest<string>(
            JobKey.Create(
                "render", plan.GetPipelineSignature(options.IncludeVideo, options.IncludeAudio, options.IncludeOverlays),
                options.Purpose, options.Width, options.Height,
                options.VideoQuality, options.UseHardwareEncoding, options.IncludeVideo, options.IncludeAudio,
                options.IncludeOverlays, options.TransparentBackground, fullOutput),
            isExport ? JobLane.Export : JobLane.MediaDecode,
            isExport ? JobPriority.UserInitiated : JobPriority.Realtime,
            async token =>
            {
                if (isExport) scheduler.SetExportActive(true);
                try
                {
                    progress?.Report(new RenderProgress(0, TimelineTime.Zero, "Starting"));
                    try
                    {
                        await ExecuteAsync(command, plan.Duration, progress, token).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (
                        temporaryOptions.UseHardwareEncoding &&
                        HardwareEncodingFallback.IsUnavailable(exception))
                    {
                        TryDelete(temporary);
                        progress?.Report(new RenderProgress(0, TimelineTime.Zero, "NVENC unavailable — CPU fallback"));
                        var cpuCommand = commandBuilder.Build(
                            plan, temporaryOptions with { UseHardwareEncoding = false });
                        await ExecuteAsync(cpuCommand, plan.Duration, progress, token).ConfigureAwait(false);
                    }
                    await VerifyOutputAsync(temporary, temporaryOptions, plan, token).ConfigureAwait(false);
                    File.Move(temporary, fullOutput, overwrite: true);
                    progress?.Report(new RenderProgress(1, plan.Duration, "Completed"));
                    return fullOutput;
                }
                finally
                {
                    TryDelete(temporary);
                    if (isExport) scheduler.SetExportActive(false);
                }
            },
            PauseDuringExport: !isExport && options.Purpose != RenderPurpose.StillFrame);
        var handle = scheduler.Schedule(request);
        using var registration = cancellationToken.Register(handle.Cancel);
        return await handle.Completion.ConfigureAwait(false);
    }

    private async Task ExecuteAsync(
        ExternalRenderCommand command,
        TimelineTime duration,
        IProgress<RenderProgress>? progress,
        CancellationToken token)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var argument in command.Arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException("FFmpeg did not start.");
        var errors = new Queue<string>();
        using var registration = token.Register(() => TryKill(process));
        var stderrTask = ReadErrorAsync(process.StandardError, duration, progress, errors, token);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(token);
        await process.WaitForExitAsync(token).ConfigureAwait(false);
        await Task.WhenAll(stderrTask, stdoutTask).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"FFmpeg exited with code {process.ExitCode}.\n{string.Join(Environment.NewLine, errors)}");
    }

    private static async Task ReadErrorAsync(
        StreamReader reader,
        TimelineTime duration,
        IProgress<RenderProgress>? progress,
        Queue<string> errors,
        CancellationToken token)
    {
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            if (errors.Count == 20) errors.Dequeue();
            errors.Enqueue(line);
            var match = TimePattern().Match(line);
            if (!match.Success || !TimeSpan.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var time)) continue;
            var rendered = TimelineTime.FromSeconds(time.TotalSeconds);
            var fraction = Math.Clamp(rendered.TotalSeconds / duration.TotalSeconds, 0, 1);
            progress?.Report(new RenderProgress(fraction, rendered, "Rendering"));
        }
    }

    private static string ResolveExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("The FFmpeg path is required.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("FFmpeg was not found.", fullPath);
        return fullPath;
    }

    private static string ResolveSiblingExecutable(string ffmpegPath, string fileName)
    {
        var sibling = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ffmpegPath))!, fileName);
        if (!File.Exists(sibling)) throw new FileNotFoundException("FFprobe was not found.", sibling);
        return sibling;
    }

    private async Task VerifyOutputAsync(
        string path,
        RenderOutputOptions options,
        RenderPlan plan,
        CancellationToken token)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < 512)
            throw new InvalidDataException("FFmpeg did not create a valid output file.");
        if (options.Purpose is RenderPurpose.FrameServer or RenderPurpose.AudioServer) return;

        var startInfo = new ProcessStartInfo
        {
            FileName = _ffprobePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var argument in new[]
        {
            "-v", "error", "-show_entries", "stream=codec_type:format=duration", "-of", "json", path
        }) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("FFprobe did not start.");
        using var registration = token.Register(() => TryKill(process));
        var outputTask = process.StandardOutput.ReadToEndAsync(token);
        var errorTask = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidDataException($"Готовый файл не прошёл FFprobe-проверку: {error}");

        using var document = JsonDocument.Parse(output);
        var streams = document.RootElement.GetProperty("streams")
            .EnumerateArray()
            .Select(item => item.GetProperty("codec_type").GetString())
            .ToArray();
        if (options.IncludeVideo && !streams.Contains("video", StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("В экспортированном файле отсутствует видеопоток.");
        if (options.IncludeAudio && !streams.Contains("audio", StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("В экспортированном файле отсутствует аудиопоток.");
        if (options.Purpose == RenderPurpose.StillFrame) return;
        var durationText = document.RootElement.GetProperty("format").GetProperty("duration").GetString();
        if (!double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration))
            throw new InvalidDataException("FFprobe не вернул длительность экспортированного файла.");
        var tolerance = Math.Max(0.25, 2d * plan.FrameRate.Denominator / plan.FrameRate.Numerator);
        if (Math.Abs(duration - plan.Duration.TotalSeconds) > tolerance)
            throw new InvalidDataException(
                $"Длительность экспорта {duration:0.###} с не совпадает с таймлайном {plan.Duration.TotalSeconds:0.###} с.");
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    [GeneratedRegex(@"time=(\d{2}:\d{2}:\d{2}(?:\.\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex TimePattern();
}

public static class HardwareEncodingFallback
{
    private static readonly string[] Markers =
    [
        "nvenc", "cuda", "no capable devices", "cannot load", "unknown encoder",
        "device setup failed", "operation not permitted"
    ];

    public static bool IsUnavailable(Exception exception)
    {
        var message = exception.ToString();
        return Markers.Any(marker => message.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
