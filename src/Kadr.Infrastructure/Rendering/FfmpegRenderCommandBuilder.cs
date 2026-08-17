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

        var isPipe = options.Purpose is RenderPurpose.FrameServer or RenderPurpose.AudioServer;
        var outputPath = isPipe ? options.OutputPath : Path.GetFullPath(options.OutputPath);
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-y" };
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
        return new ExternalRenderCommand("ffmpeg", arguments.ToImmutableArray(), outputPath,
            plan.GetPipelineSignature(options.IncludeVideo, options.IncludeAudio, options.IncludeOverlays));
    }

    private static RenderInputs AddInputs(
        ICollection<string> arguments,
        RenderPlan plan,
        RenderOutputOptions options)
    {
        var indexes = new Dictionary<Guid, int>();
        var windows = new Dictionary<Guid, DecodeWindow>();
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
            if (indexes.ContainsKey(clipId)) continue;
            var timelineRange = layer is RenderVisualLayer v ? v.TimelineRange : ((RenderAudioLayer)layer).TimelineRange;
            var sourceIn = layer is RenderVisualLayer vv ? vv.SourceIn : ((RenderAudioLayer)layer).SourceIn;
            var sourcePath = layer is RenderVisualLayer vvv ? vvv.SourcePath : ((RenderAudioLayer)layer).SourcePath;
            var window = ResolveDecodeWindow(plan, clipId, timelineRange, sourceIn);
            var isImage = layer is RenderVisualLayer { SourceKind: MediaKind.Image };
            if (isImage)
            {
                arguments.Add("-loop"); arguments.Add("1");
                arguments.Add("-framerate"); arguments.Add(FrameRateValue(plan.FrameRate));
            }
            else
            {
                arguments.Add("-ss"); arguments.Add(Format(window.SourceOffset.TotalSeconds));
            }
            arguments.Add("-t"); arguments.Add(Format(window.Duration.TotalSeconds));
            arguments.Add("-i"); arguments.Add(sourcePath);
            indexes.Add(clipId, indexes.Count);
            windows.Add(clipId, window);
        }
        return new RenderInputs(indexes, windows);
    }

    private static string BuildVideoGraph(
        ICollection<string> filters,
        RenderPlan plan,
        RenderOutputOptions options,
        RenderInputs inputs)
    {
        var duration = Format(plan.Duration.TotalSeconds);
        var frameRate = FrameRateValue(plan.FrameRate);
        var background = options.TransparentBackground ? "black@0" : "black";
        filters.Add($"color=c={background}:s={options.Width}x{options.Height}:r={frameRate}:d={duration},format=rgba[vbase]");
        var previous = "vbase";
        for (var index = 0; index < plan.VisualLayers.Length; index++)
        {
            var layer = plan.VisualLayers[index];
            var prepared = $"vprepared{index}";
            var window = inputs.Windows[layer.ClipId];
            var surface = $"vsurface{index}";
            var canvas = $"vcanvas{index}";
            var layout = $"vlayout{index}";
            filters.Add(
                $"[{inputs.Indexes[layer.ClipId]}:v:0]" + BuildVideoSurfaceFilters(layer.Parameters, options) +
                $"fps={frameRate},format=rgba,setpts=PTS-STARTPTS[{surface}]");
            filters.Add($"color=c=black@0:s={options.Width}x{options.Height}:r={frameRate}:" +
                        $"d={Format(window.Duration.TotalSeconds)},format=rgba[{canvas}]");
            filters.Add(
                $"[{canvas}][{surface}]overlay=" +
                $"x='{Format(options.Width * layer.Parameters.PositionX)}-overlay_w/2':" +
                $"y='{Format(options.Height * layer.Parameters.PositionY)}-overlay_h/2':" +
                $"eof_action=pass:shortest=1[{layout}]");
            filters.Add($"[{layout}]" + BuildVideoTransitionFilters(plan, layer, window) +
                        $"setpts=PTS-STARTPTS+{Format(RelativeStart(window.Range, plan.Range))}/TB[{prepared}]");
            var composed = $"vcomposed{index}";
            var overlayX = BuildTransitionOverlayX(plan, layer, options.Width);
            filters.Add(
                $"[{previous}][{prepared}]overlay=x='{overlayX}':y=0:eof_action=pass:shortest=0:" +
                $"enable='between(t,{Format(RelativeStart(window.Range, plan.Range))}," +
                $"{Format(RelativeEnd(window.Range, plan.Range))})'[{composed}]");
            previous = composed;
        }
        for (var index = 0; options.IncludeOverlays && index < plan.TextLayers.Length; index++)
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
        var outputFormat = options.Purpose == RenderPurpose.FrameServer ? "bgra" : "yuv420p";
        filters.Add($"[{previous}]format={outputFormat}[vout]");
        return "vout";
    }

    private static string BuildAudioGraph(
        ICollection<string> filters,
        RenderPlan plan,
        RenderInputs inputs)
    {
        filters.Add($"anullsrc=channel_layout=stereo:sample_rate={plan.AudioSampleRate}:d={Format(plan.Duration.TotalSeconds)}[asilence]");
        var labels = new List<string> { "[asilence]" };
        for (var index = 0; index < plan.AudioLayers.Length; index++)
        {
            var layer = plan.AudioLayers[index];
            var window = inputs.Windows[layer.ClipId];
            var duration = window.Duration.TotalSeconds;
            var delaySamples = Math.Max(0, (long)Math.Round(
                RelativeStart(window.Range, plan.Range) * plan.AudioSampleRate,
                MidpointRounding.AwayFromZero));
            var label = $"aprepared{index}";
            filters.Add(
                $"[{inputs.Indexes[layer.ClipId]}:a:0]aresample={plan.AudioSampleRate}," +
                "aformat=sample_fmts=fltp:channel_layouts=stereo," +
                $"atrim=0:{Format(duration)},asetpts=PTS-STARTPTS," +
                $"volume={Format(layer.Parameters.Volume)}," + BuildAudioFilters(layer.Parameters, duration) +
                BuildAudioTransitionFilters(plan, layer, window) +
                $"adelay={delaySamples}S|{delaySamples}S[{label}]");
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
            if (options.Purpose == RenderPurpose.FrameServer)
            {
                arguments.Add("-c:v"); arguments.Add("rawvideo");
                arguments.Add("-pix_fmt"); arguments.Add("bgra");
                arguments.Add("-f"); arguments.Add("rawvideo");
            }
            else if (options.Purpose == RenderPurpose.StillFrame)
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
            if (options.Purpose != RenderPurpose.FrameServer)
            {
                arguments.Add("-pix_fmt"); arguments.Add("yuv420p");
            }
        }
        if (audio)
        {
            arguments.Add("-c:a"); arguments.Add(options.Purpose == RenderPurpose.AudioServer ? "pcm_f32le" : "aac");
            if (options.Purpose != RenderPurpose.AudioServer)
            {
                arguments.Add("-b:a"); arguments.Add(options.Purpose == RenderPurpose.Preview ? "128k" : "192k");
            }
            arguments.Add("-ar"); arguments.Add(plan.AudioSampleRate.ToString(CultureInfo.InvariantCulture));
            arguments.Add("-ac"); arguments.Add("2");
            if (options.Purpose == RenderPurpose.AudioServer)
            {
                arguments.Add("-f"); arguments.Add("f32le");
            }
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

    private static string BuildVideoSurfaceFilters(VideoParameters parameters, RenderOutputOptions options)
    {
        var filters = new List<string>();
        if (parameters.CropLeft > 0 || parameters.CropTop > 0 ||
            parameters.CropRight > 0 || parameters.CropBottom > 0)
        {
            filters.Add($"crop=iw*{Format(1 - parameters.CropLeft - parameters.CropRight)}:" +
                        $"ih*{Format(1 - parameters.CropTop - parameters.CropBottom)}:" +
                        $"iw*{Format(parameters.CropLeft)}:ih*{Format(parameters.CropTop)}");
        }
        filters.Add($"scale={options.Width}:{options.Height}:force_original_aspect_ratio=decrease");
        if (Math.Abs(parameters.ScaleX - 1) > 0.0001 || Math.Abs(parameters.ScaleY - 1) > 0.0001)
            filters.Add($"scale='max(2,trunc(iw*{Format(parameters.ScaleX)}/2)*2)':" +
                        $"'max(2,trunc(ih*{Format(parameters.ScaleY)}/2)*2)'");
        if (Math.Abs(parameters.Rotation) > 0.0001)
            filters.Add($"rotate={Format(parameters.Rotation * Math.PI / 180)}:" +
                        "ow=rotw(iw):oh=roth(ih):c=black@0");
        filters.Add("setsar=1");
        if (Math.Abs(parameters.Brightness) > 0.0001 || Math.Abs(parameters.Contrast - 1) > 0.0001 ||
            Math.Abs(parameters.Saturation - 1) > 0.0001)
            filters.Add($"eq=brightness={Format(parameters.Brightness)}:contrast={Format(parameters.Contrast)}:saturation={Format(parameters.Saturation)}");
        if (Math.Abs(parameters.Temperature) > 0.0001)
            filters.Add($"colorchannelmixer=rr={Format(Math.Clamp(1 + parameters.Temperature * 0.22, 0.5, 1.5))}:gg=1:" +
                        $"bb={Format(Math.Clamp(1 - parameters.Temperature * 0.22, 0.5, 1.5))}");
        filters.Add("format=rgba");
        if (Math.Abs(parameters.Opacity - 1) > 0.0001)
            filters.Add($"colorchannelmixer=aa={Format(parameters.Opacity)}");
        return string.Join(',', filters) + ',';
    }

    private static DecodeWindow ResolveDecodeWindow(
        RenderPlan plan,
        Guid clipId,
        TimeRange clipRange,
        TimelineTime sourceIn)
    {
        var start = clipRange.Start >= plan.Range.Start ? clipRange.Start : plan.Range.Start;
        var end = clipRange.End <= plan.Range.End ? clipRange.End : plan.Range.End;
        foreach (var transition in plan.VideoTransitions
                     .Where(item => item.From.ClipId == clipId || item.To.ClipId == clipId)
                     .Select(item => item.TimelineRange)
                     .Concat(plan.AudioTransitions
                         .Where(item => item.From.ClipId == clipId || item.To.ClipId == clipId)
                         .Select(item => item.TimelineRange)))
        {
            if (transition.Start < start && transition.End > plan.Range.Start)
                start = transition.Start >= plan.Range.Start ? transition.Start : plan.Range.Start;
            if (transition.End > end && transition.Start < plan.Range.End)
                end = transition.End <= plan.Range.End ? transition.End : plan.Range.End;
        }
        var offset = sourceIn + (start - clipRange.Start);
        return new DecodeWindow(new TimeRange(start, end - start), offset);
    }

    private static string BuildVideoTransitionFilters(
        RenderPlan plan,
        RenderVisualLayer layer,
        DecodeWindow window)
    {
        var filters = new List<string>();
        foreach (var transition in plan.VideoTransitions.Where(item => item.From.ClipId == layer.ClipId))
        {
            var localStart = (transition.TimelineRange.Start - window.Range.Start).TotalSeconds;
            var duration = transition.TimelineRange.Duration.TotalSeconds;
            if (transition.Kind is TransitionKind.DipToBlack or TransitionKind.DipToWhite)
            {
                var color = transition.Kind == TransitionKind.DipToWhite ? "white" : "black";
                filters.Add($"fade=t=out:st={Format(localStart)}:d={Format(duration / 2)}:" +
                            $"alpha=0:color={color}");
            }
        }
        foreach (var transition in plan.VideoTransitions.Where(item => item.To.ClipId == layer.ClipId))
        {
            var localStart = (transition.TimelineRange.Start - window.Range.Start).TotalSeconds;
            var duration = transition.TimelineRange.Duration.TotalSeconds;
            switch (transition.Kind)
            {
                case TransitionKind.CrossDissolve:
                    var elapsed = (window.Range.Start - transition.TimelineRange.Start).TotalSeconds;
                    filters.Add("geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':" +
                                $"a='alpha(X,Y)*clip((T+{Format(elapsed)})/{Format(duration)},0,1)'");
                    break;
                case TransitionKind.DipToBlack:
                case TransitionKind.DipToWhite:
                    filters.Add($"fade=t=in:st={Format(localStart + duration / 2)}:" +
                                $"d={Format(duration / 2)}:alpha=1");
                    break;
                case TransitionKind.Wipe:
                    var progress = $"clip((T-{Format(localStart)})/{Format(duration)},0,1)";
                    filters.Add("geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':" +
                                $"a='alpha(X,Y)*lte(X/W,{progress})'");
                    break;
            }
        }
        return filters.Count == 0 ? string.Empty : string.Join(',', filters) + ',';
    }

    private static string BuildTransitionOverlayX(RenderPlan plan, RenderVisualLayer layer, int width)
    {
        var slide = plan.VideoTransitions.FirstOrDefault(item =>
            item.Kind == TransitionKind.Slide && item.To.ClipId == layer.ClipId);
        if (slide is null) return "0";
        var duration = slide.TimelineRange.Duration.TotalSeconds;
        var elapsed = (plan.Range.Start - slide.TimelineRange.Start).TotalSeconds;
        return $"{width}*(1-clip((t+{Format(elapsed)})/{Format(duration)},0,1))";
    }

    private static string BuildAudioTransitionFilters(
        RenderPlan plan,
        RenderAudioLayer layer,
        DecodeWindow window)
    {
        var filters = new List<string>();
        foreach (var transition in plan.AudioTransitions.Where(item => item.From.ClipId == layer.ClipId))
        {
            var elapsed = (window.Range.Start - transition.TimelineRange.Start).TotalSeconds;
            var duration = transition.TimelineRange.Duration.TotalSeconds;
            filters.Add($"volume='cos(clip((t+{Format(elapsed)})/{Format(duration)},0,1)*PI/2)':eval=frame");
        }
        foreach (var transition in plan.AudioTransitions.Where(item => item.To.ClipId == layer.ClipId))
        {
            var elapsed = (window.Range.Start - transition.TimelineRange.Start).TotalSeconds;
            var duration = transition.TimelineRange.Duration.TotalSeconds;
            filters.Add($"volume='sin(clip((t+{Format(elapsed)})/{Format(duration)},0,1)*PI/2)':eval=frame");
        }
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

    private sealed record DecodeWindow(TimeRange Range, TimelineTime SourceOffset)
    {
        public TimelineTime Duration => Range.Duration;
    }

    private sealed record RenderInputs(
        IReadOnlyDictionary<Guid, int> Indexes,
        IReadOnlyDictionary<Guid, DecodeWindow> Windows);
}
