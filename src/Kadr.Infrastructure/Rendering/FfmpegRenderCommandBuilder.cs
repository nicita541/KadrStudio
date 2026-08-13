using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using KadrStudio.Application.Rendering;
using KadrStudio.Core.Domain;

namespace KadrStudio.Infrastructure.Rendering;

public sealed class FfmpegRenderCommandBuilder : IRenderCommandBuilder
{
    public ExternalRenderCommand Build(RenderPlan plan, RenderOutputOptions options)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.OutputPath)) throw new ArgumentException("An output path is required.", nameof(options));
        if (options.Width <= 0 || options.Height <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        if (!options.IncludeVideo && !options.IncludeAudio) throw new ArgumentException("At least one output stream is required.", nameof(options));

        var outputPath = Path.GetFullPath(options.OutputPath);
        var arguments = new List<string> { "-hide_banner", "-y" };
        var inputs = AddInputs(arguments, plan, options);
        var filters = new List<string>();
        string? videoOutput = options.IncludeVideo ? BuildVideoGraph(filters, plan, options, inputs) : null;
        string? audioOutput = options.IncludeAudio ? BuildAudioGraph(filters, plan, inputs) : null;

        if (filters.Count > 0) arguments.AddRange(["-filter_complex", string.Join(';', filters)]);
        if (videoOutput is not null) arguments.AddRange(["-map", $"[{videoOutput}]"]);
        if (audioOutput is not null) arguments.AddRange(["-map", $"[{audioOutput}]"]);
        arguments.AddRange(["-t", Format(plan.Duration.TotalSeconds)]);
        AddEncoding(arguments, plan, options, videoOutput is not null, audioOutput is not null);
        arguments.Add(outputPath);
        return new ExternalRenderCommand("ffmpeg", arguments.ToImmutableArray(), outputPath, plan.ContentSignature);
    }

    private static Dictionary<Guid, int> AddInputs(
        ICollection<string> arguments,
        RenderPlan plan,
        RenderOutputOptions options)
    {
        var inputs = new Dictionary<Guid, int>();
        var layers = (options.IncludeVideo ? plan.VisualLayers.Cast<object>() : [])
            .Concat(options.IncludeAudio ? plan.AudioLayers : [])
            .ToArray();
        foreach (var layer in layers)
        {
            var clipId = layer switch
            {
                RenderVisualLayer visual => visual.ClipId,
                RenderAudioLayer audio => audio.ClipId,
                _ => throw new UnreachableException()
            };
            if (inputs.ContainsKey(clipId)) continue;
            var timelineRange = layer is RenderVisualLayer v ? v.TimelineRange : ((RenderAudioLayer)layer).TimelineRange;
            var sourceIn = layer is RenderVisualLayer vv ? vv.SourceIn : ((RenderAudioLayer)layer).SourceIn;
            var sourcePath = layer is RenderVisualLayer vvv ? vvv.SourcePath : ((RenderAudioLayer)layer).SourcePath;
            var intersectionStart = timelineRange.Start >= plan.Range.Start ? timelineRange.Start : plan.Range.Start;
            var intersectionEnd = timelineRange.End <= plan.Range.End ? timelineRange.End : plan.Range.End;
            var sourceOffset = sourceIn + (intersectionStart - timelineRange.Start);
            var duration = intersectionEnd - intersectionStart;
            var isImage = layer is RenderVisualLayer { SourceKind: MediaKind.Image };
            if (isImage)
            {
                arguments.Add("-loop"); arguments.Add("1");
                arguments.Add("-framerate"); arguments.Add(FrameRateValue(plan.FrameRate));
            }
            else
            {
                arguments.Add("-ss"); arguments.Add(Format(sourceOffset.TotalSeconds));
            }
            arguments.Add("-t"); arguments.Add(Format(duration.TotalSeconds));
            arguments.Add("-i"); arguments.Add(sourcePath);
            inputs.Add(clipId, inputs.Count);
        }
        return inputs;
    }

    private static string BuildVideoGraph(
        ICollection<string> filters,
        RenderPlan plan,
        RenderOutputOptions options,
        IReadOnlyDictionary<Guid, int> inputs)
    {
        var duration = Format(plan.Duration.TotalSeconds);
        var frameRate = FrameRateValue(plan.FrameRate);
        filters.Add($"color=c=black:s={options.Width}x{options.Height}:r={frameRate}:d={duration},format=rgba[vbase]");
        var previous = "vbase";
        for (var index = 0; index < plan.VisualLayers.Length; index++)
        {
            var layer = plan.VisualLayers[index];
            var start = RelativeStart(layer.TimelineRange, plan.Range);
            var end = RelativeEnd(layer.TimelineRange, plan.Range);
            var prepared = $"vprepared{index}";
            filters.Add(
                $"[{inputs[layer.ClipId]}:v:0]scale={options.Width}:{options.Height}:force_original_aspect_ratio=decrease," +
                $"pad={options.Width}:{options.Height}:(ow-iw)/2:(oh-ih)/2:black,setsar=1,fps={frameRate},format=rgba," +
                BuildColorFilters(layer.Parameters) +
                $"setpts=PTS-STARTPTS+{Format(start)}/TB[{prepared}]");
            var composed = $"vcomposed{index}";
            filters.Add(
                $"[{previous}][{prepared}]overlay=0:0:eof_action=pass:shortest=0:" +
                $"enable='between(t,{Format(start)},{Format(end)})'[{composed}]");
            previous = composed;
        }
        for (var index = 0; index < plan.TextLayers.Length; index++)
        {
            var layer = plan.TextLayers[index];
            var output = $"vtext{index}";
            var fontSize = Math.Max(4, layer.Style.FontSize * options.Height / plan.CanvasHeight);
            var box = layer.Style.IsSubtitle ? ":box=1:boxcolor=black@0.58:boxborderw=10" : string.Empty;
            var x = $"max(0,min(w-text_w,w*{Format(layer.Style.X)}-text_w/2))";
            var y = $"max(0,min(h-text_h,h*{Format(layer.Style.Y)}-text_h/2))";
            filters.Add(
                $"[{previous}]drawtext=fontfile='{ResolveFontFile(layer.Style.FontFamily)}':" +
                $"text='{EscapeDrawText(layer.Text)}':expansion=none:fontsize={Format(fontSize)}:" +
                $"fontcolor={NormalizeFontColor(layer.Style.Color)}:borderw=2:bordercolor=black@0.9{box}:" +
                $"x='{x}':y='{y}':enable='between(t,{Format(RelativeStart(layer.TimelineRange, plan.Range))}," +
                $"{Format(RelativeEnd(layer.TimelineRange, plan.Range))})'[{output}]");
            previous = output;
        }
        filters.Add($"[{previous}]format=yuv420p[vout]");
        return "vout";
    }

    private static string BuildAudioGraph(
        ICollection<string> filters,
        RenderPlan plan,
        IReadOnlyDictionary<Guid, int> inputs)
    {
        filters.Add($"anullsrc=channel_layout=stereo:sample_rate=48000:d={Format(plan.Duration.TotalSeconds)}[asilence]");
        var labels = new List<string> { "[asilence]" };
        for (var index = 0; index < plan.AudioLayers.Length; index++)
        {
            var layer = plan.AudioLayers[index];
            var duration = IntersectionDuration(layer.TimelineRange, plan.Range);
            var delay = Math.Max(0, (long)Math.Round(RelativeStart(layer.TimelineRange, plan.Range) * 1000));
            var label = $"aprepared{index}";
            filters.Add(
                $"[{inputs[layer.ClipId]}:a:0]aresample=48000,atrim=0:{Format(duration)},asetpts=PTS-STARTPTS," +
                $"volume={Format(layer.Parameters.Volume)}," + BuildAudioFilters(layer.Parameters, duration) +
                $"adelay={delay}|{delay}[{label}]");
            labels.Add($"[{label}]");
        }
        filters.Add(
            $"{string.Concat(labels)}amix=inputs={labels.Count}:duration=longest:dropout_transition=0:normalize=0," +
            $"atrim=0:{Format(plan.Duration.TotalSeconds)}[aout]");
        return "aout";
    }

    private static void AddEncoding(
        ICollection<string> arguments,
        RenderPlan plan,
        RenderOutputOptions options,
        bool video,
        bool audio)
    {
        if (video)
        {
            if (options.Purpose == RenderPurpose.StillFrame)
            {
                arguments.Add("-frames:v"); arguments.Add("1");
            }
            else if (options.UseHardwareEncoding)
            {
                arguments.Add("-c:v"); arguments.Add("h264_nvenc");
                arguments.Add("-preset"); arguments.Add("p5");
                arguments.Add("-cq"); arguments.Add(options.VideoQuality.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                arguments.Add("-c:v"); arguments.Add("libx264");
                arguments.Add("-preset"); arguments.Add(options.Purpose == RenderPurpose.Preview ? "veryfast" : "medium");
                arguments.Add("-crf"); arguments.Add(options.VideoQuality.ToString(CultureInfo.InvariantCulture));
            }
            arguments.Add("-r"); arguments.Add(FrameRateValue(plan.FrameRate));
            arguments.Add("-pix_fmt"); arguments.Add("yuv420p");
        }
        if (audio)
        {
            arguments.Add("-c:a"); arguments.Add("aac");
            arguments.Add("-b:a"); arguments.Add(options.Purpose == RenderPurpose.Preview ? "128k" : "192k");
            arguments.Add("-ar"); arguments.Add("48000");
            arguments.Add("-ac"); arguments.Add("2");
        }
        if (options.Purpose is RenderPurpose.Export or RenderPurpose.Preview)
        {
            arguments.Add("-movflags"); arguments.Add("+faststart");
        }
    }

    private static double RelativeStart(TimeRange clip, TimeRange plan)
        => Math.Max(0, (clip.Start - plan.Start).TotalSeconds);

    private static double RelativeEnd(TimeRange clip, TimeRange plan)
        => Math.Min(plan.Duration.TotalSeconds, (clip.End - plan.Start).TotalSeconds);

    private static double IntersectionDuration(TimeRange clip, TimeRange plan)
        => Math.Max(0, RelativeEnd(clip, plan) - RelativeStart(clip, plan));

    private static string BuildColorFilters(VideoParameters parameters)
    {
        var filters = new List<string>();
        if (Math.Abs(parameters.Brightness) > 0.0001 || Math.Abs(parameters.Contrast - 1) > 0.0001 ||
            Math.Abs(parameters.Saturation - 1) > 0.0001)
            filters.Add($"eq=brightness={Format(parameters.Brightness)}:contrast={Format(parameters.Contrast)}:saturation={Format(parameters.Saturation)}");
        if (Math.Abs(parameters.Temperature) > 0.0001)
            filters.Add($"colorchannelmixer=rr={Format(Math.Clamp(1 + parameters.Temperature * 0.22, 0.5, 1.5))}:gg=1:" +
                        $"bb={Format(Math.Clamp(1 - parameters.Temperature * 0.22, 0.5, 1.5))}");
        return filters.Count == 0 ? string.Empty : string.Join(',', filters) + ',';
    }

    private static string BuildAudioFilters(AudioParameters parameters, double clipDuration)
    {
        var filters = new List<string>();
        if (Math.Abs(parameters.Bass) > 0.01) filters.Add($"bass=g={Format(parameters.Bass)}");
        if (Math.Abs(parameters.Mid) > 0.01) filters.Add($"equalizer=f=1000:t=q:w=1:g={Format(parameters.Mid)}");
        if (Math.Abs(parameters.Treble) > 0.01) filters.Add($"treble=g={Format(parameters.Treble)}");
        if (Math.Abs(parameters.Pan) > 0.001)
        {
            var left = parameters.Pan > 0 ? 1 - parameters.Pan : 1;
            var right = parameters.Pan < 0 ? 1 + parameters.Pan : 1;
            filters.Add($"pan=stereo|c0={Format(left)}*c0|c1={Format(right)}*c1");
        }
        if (parameters.FadeIn > TimelineTime.Zero)
            filters.Add($"afade=t=in:st=0:d={Format(Math.Min(parameters.FadeIn.TotalSeconds, clipDuration))}");
        if (parameters.FadeOut > TimelineTime.Zero)
        {
            var fade = Math.Min(parameters.FadeOut.TotalSeconds, clipDuration);
            filters.Add($"afade=t=out:st={Format(Math.Max(0, clipDuration - fade))}:d={Format(fade)}");
        }
        return filters.Count == 0 ? string.Empty : string.Join(',', filters) + ',';
    }

    private static string ResolveFontFile(string fontFamily)
    {
        var file = fontFamily.ToLowerInvariant() switch
        {
            "arial" => "arial.ttf",
            "impact" => "impact.ttf",
            "times new roman" => "times.ttf",
            _ => "segoeui.ttf"
        };
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), file)
            .Replace('\\', '/').Replace(":", "\\:", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
    }

    private static string EscapeDrawText(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string NormalizeFontColor(string color)
    {
        var value = color.Trim().TrimStart('#');
        return value.Length is 6 or 8 && value.All(Uri.IsHexDigit) ? $"0x{value}" : "white";
    }

    private static string FrameRateValue(FrameRate frameRate)
        => frameRate.Denominator == 1
            ? frameRate.Numerator.ToString(CultureInfo.InvariantCulture)
            : $"{frameRate.Numerator}/{frameRate.Denominator}";

    private static string Format(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
