using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KadrStudio.Models;

namespace KadrStudio.Services;

/// <summary>
/// Builds independent, timeline-based video and audio preview segments.
/// Video inputs are composited by track order and never carry audio. Audio inputs
/// are mixed separately and never carry video. This keeps either preview pipeline
/// from replacing or invalidating the source currently used by the other one.
/// </summary>
public sealed class PreviewCompositionService
{
    public const double SegmentStep = 15;
    public const double SegmentOverlap = 4;

    private readonly FfmpegLocator locator;
    private readonly ProcessRunner processRunner;
    private readonly string _videoDirectory;
    private readonly string _audioDirectory;
    private readonly string _stillDirectory;
    private readonly SemaphoreSlim _videoGate = new(1, 1);
    private readonly SemaphoreSlim _audioGate = new(1, 1);
    private readonly SemaphoreSlim _stillGate = new(1, 1);
    public PreviewCompositionService(
        FfmpegLocator locator,
        ProcessRunner processRunner,
        string? cacheRoot = null)
    {
        this.locator = locator;
        this.processRunner = processRunner;
        var fullRoot = Path.GetFullPath(cacheRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kadr Studio", "Cache"));
        _videoDirectory = Path.Combine(fullRoot, string.IsNullOrWhiteSpace(cacheRoot) ? "CompositedPreviewVideo" : "video");
        _audioDirectory = Path.Combine(fullRoot, string.IsNullOrWhiteSpace(cacheRoot) ? "MixedPreviewAudio" : "audio");
        _stillDirectory = Path.Combine(fullRoot, string.IsNullOrWhiteSpace(cacheRoot) ? "CompositedPreviewStills" : "stills");
    }

    public string GetVideoSignature(EditorProject project, bool halfQuality)
        => Capture(project, PreviewPipeline.Video, halfQuality).Signature;

    public string GetAudioSignature(EditorProject project)
        => Capture(project, PreviewPipeline.Audio, halfQuality: true).Signature;

    public bool HasRenderableVideo(EditorProject project)
        => project.GetVisualClips().Any(clip =>
            project.FindAsset(clip.AssetId) is { IsMissing: false, Kind: MediaKind.Video or MediaKind.Image } asset &&
            File.Exists(asset.Path));

    public bool HasRenderableAudio(EditorProject project)
        => project.GetAudioClips().Any(clip =>
            project.FindAsset(clip.AssetId) is { IsMissing: false, HasAudio: true, Kind: MediaKind.Video or MediaKind.Audio } asset &&
            File.Exists(asset.Path));

    public Task<TimelinePreviewSegment> EnsureVideoSegmentAsync(
        EditorProject project,
        double timelinePosition,
        bool halfQuality,
        CancellationToken cancellationToken = default)
    {
        var snapshot = Capture(project, PreviewPipeline.Video, halfQuality);
        var range = CreateRange(timelinePosition);
        return RenderVideoSegmentAsync(snapshot, range, cancellationToken);
    }

    public Task<TimelinePreviewSegment> EnsureAudioSegmentAsync(
        EditorProject project,
        double timelinePosition,
        CancellationToken cancellationToken = default)
    {
        var snapshot = Capture(project, PreviewPipeline.Audio, halfQuality: true);
        var range = CreateRange(timelinePosition);
        return RenderAudioSegmentAsync(snapshot, range, cancellationToken);
    }

    public Task<CompositedStillFrame> EnsureStillFrameAsync(
        EditorProject project,
        double timelinePosition,
        bool halfQuality,
        CancellationToken cancellationToken = default)
    {
        var snapshot = Capture(project, PreviewPipeline.Video, halfQuality);
        var frameRate = Math.Max(15, snapshot.FrameRate);
        var frameNumber = Math.Max(0, (long)Math.Round(timelinePosition * frameRate));
        var exactPosition = frameNumber / (double)frameRate;
        return RenderStillFrameAsync(snapshot, exactPosition, frameNumber, cancellationToken);
    }

    public void InvalidateCachedFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var fullPath = Path.GetFullPath(path);
        var roots = new[] { _videoDirectory, _audioDirectory, _stillDirectory }
            .Select(root => Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
        if (!roots.Any(root => fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))) return;
        try
        {
            if (File.Exists(fullPath)) File.Delete(fullPath);
        }
        catch
        {
            // The WPF media pipeline may release the old source a moment later.
        }
    }

    private async Task<TimelinePreviewSegment> RenderVideoSegmentAsync(
        PreviewSnapshot snapshot,
        PreviewRange range,
        CancellationToken cancellationToken)
    {
        locator.EnsureAvailable();
        Directory.CreateDirectory(_videoDirectory);
        var fileName = $"{snapshot.Signature}-{range.Start:000000.000}.mp4";
        var outputPath = Path.Combine(_videoDirectory, fileName);
        if (IsUsable(outputPath)) return new TimelinePreviewSegment(
            PreviewPipeline.Video, snapshot.Signature, outputPath, range.Start, range.Duration);

        await _videoGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsUsable(outputPath)) return new TimelinePreviewSegment(
                PreviewPipeline.Video, snapshot.Signature, outputPath, range.Start, range.Duration);

            var temporaryPath = outputPath + $"-{Guid.NewGuid():N}.tmp.mp4";
            try
            {
                var arguments = BuildVideoArguments(snapshot, range, temporaryPath);
                var result = await processRunner.RunAsync(locator.FfmpegPath, arguments, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                EnsureRendered(result, temporaryPath, "Не удалось собрать видеодорожки предпросмотра");
                File.Move(temporaryPath, outputPath, overwrite: true);
            }
            finally
            {
                TryDeleteTemporary(temporaryPath);
            }
        }
        finally
        {
            _videoGate.Release();
        }

        return new TimelinePreviewSegment(
            PreviewPipeline.Video, snapshot.Signature, outputPath, range.Start, range.Duration);
    }

    private async Task<TimelinePreviewSegment> RenderAudioSegmentAsync(
        PreviewSnapshot snapshot,
        PreviewRange range,
        CancellationToken cancellationToken)
    {
        locator.EnsureAvailable();
        Directory.CreateDirectory(_audioDirectory);
        var fileName = $"{snapshot.Signature}-{range.Start:000000.000}.m4a";
        var outputPath = Path.Combine(_audioDirectory, fileName);
        if (IsUsable(outputPath)) return new TimelinePreviewSegment(
            PreviewPipeline.Audio, snapshot.Signature, outputPath, range.Start, range.Duration);

        await _audioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsUsable(outputPath)) return new TimelinePreviewSegment(
                PreviewPipeline.Audio, snapshot.Signature, outputPath, range.Start, range.Duration);

            var temporaryPath = outputPath + $"-{Guid.NewGuid():N}.tmp.m4a";
            try
            {
                var arguments = BuildAudioArguments(snapshot, range, temporaryPath);
                var result = await processRunner.RunAsync(locator.FfmpegPath, arguments, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                EnsureRendered(result, temporaryPath, "Не удалось свести аудиодорожки предпросмотра");
                File.Move(temporaryPath, outputPath, overwrite: true);
            }
            finally
            {
                TryDeleteTemporary(temporaryPath);
            }
        }
        finally
        {
            _audioGate.Release();
        }

        return new TimelinePreviewSegment(
            PreviewPipeline.Audio, snapshot.Signature, outputPath, range.Start, range.Duration);
    }

    private async Task<CompositedStillFrame> RenderStillFrameAsync(
        PreviewSnapshot snapshot,
        double timelinePosition,
        long frameNumber,
        CancellationToken cancellationToken)
    {
        locator.EnsureAvailable();
        Directory.CreateDirectory(_stillDirectory);
        var outputPath = Path.Combine(_stillDirectory, $"{snapshot.Signature}-{frameNumber:0000000000}.jpg");
        if (IsUsable(outputPath, 512)) return new CompositedStillFrame(
            snapshot.Signature, outputPath, timelinePosition);

        await _stillGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsUsable(outputPath, 512)) return new CompositedStillFrame(
                snapshot.Signature, outputPath, timelinePosition);

            var temporaryPath = outputPath + $"-{Guid.NewGuid():N}.tmp.jpg";
            try
            {
                var arguments = BuildStillArguments(snapshot, timelinePosition, temporaryPath);
                var result = await processRunner.RunAsync(locator.FfmpegPath, arguments, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                EnsureRendered(result, temporaryPath, "Не удалось собрать точный кадр предпросмотра", 512);
                File.Move(temporaryPath, outputPath, overwrite: true);
            }
            finally
            {
                TryDeleteTemporary(temporaryPath);
            }
        }
        finally
        {
            _stillGate.Release();
        }

        return new CompositedStillFrame(snapshot.Signature, outputPath, timelinePosition);
    }

    private static IReadOnlyList<string> BuildVideoArguments(
        PreviewSnapshot snapshot,
        PreviewRange range,
        string outputPath)
    {
        var width = snapshot.HalfQuality ? 640 : 960;
        var height = snapshot.HalfQuality ? 360 : 540;
        var frameRate = snapshot.HalfQuality ? 15 : Math.Clamp(snapshot.FrameRate, 24, 30);
        var inputs = snapshot.Clips
            .Where(clip => clip.Track == TrackKind.Visual && clip.Kind is MediaKind.Video or MediaKind.Image)
            .Where(clip => clip.Start < range.End && clip.End > range.Start)
            .OrderBy(clip => clip.TrackIndex)
            .ThenBy(clip => clip.Start)
            .ToList();
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        foreach (var clip in inputs)
        {
            var overlapStart = Math.Max(range.Start, clip.Start);
            var localOffset = Math.Max(0, overlapStart - clip.Start);
            var inputDuration = Math.Max(0.05, Math.Min(range.End, clip.End) - overlapStart);
            if (clip.Kind == MediaKind.Image)
            {
                arguments.AddRange(["-loop", "1", "-framerate", frameRate.ToString(CultureInfo.InvariantCulture),
                    "-t", Format(inputDuration), "-i", clip.Path]);
            }
            else
            {
                arguments.AddRange(["-ss", Format(clip.SourceStart + localOffset),
                    "-t", Format(inputDuration), "-i", clip.Path]);
            }
        }

        var filters = new List<string>
        {
            $"color=c=black:s={width}x{height}:r={frameRate}:d={Format(range.Duration)},format=rgba[base0]"
        };
        var previous = "base0";
        for (var index = 0; index < inputs.Count; index++)
        {
            var clip = inputs[index];
            var overlapStart = Math.Max(range.Start, clip.Start);
            var relativeStart = Math.Max(0, overlapStart - range.Start);
            var relativeEnd = Math.Min(range.Duration, clip.End - range.Start);
            var color = BuildColorFilters(clip);
            filters.Add(
                $"[{index}:v:0]scale={width}:{height}:force_original_aspect_ratio=decrease," +
                $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:black,setsar=1,fps={frameRate},format=rgba," +
                color + $"setpts=PTS-STARTPTS+{Format(relativeStart)}/TB[v{index}]");
            filters.Add(
                $"[{previous}][v{index}]overlay=0:0:eof_action=pass:shortest=0:" +
                $"enable='between(t,{Format(relativeStart)},{Format(relativeEnd)})'[layer{index}]");
            previous = $"layer{index}";
        }
        filters.Add($"[{previous}]format=yuv420p[vout]");
        arguments.AddRange([
            "-filter_complex_threads", "2",
            "-filter_complex", string.Join(';', filters),
            "-map", "[vout]", "-an", "-t", Format(range.Duration),
            "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency",
            "-crf", snapshot.HalfQuality ? "30" : "25",
            "-g", frameRate.ToString(CultureInfo.InvariantCulture),
            "-keyint_min", frameRate.ToString(CultureInfo.InvariantCulture),
            "-sc_threshold", "0", "-pix_fmt", "yuv420p", "-movflags", "+faststart", outputPath
        ]);
        return arguments;
    }

    private static IReadOnlyList<string> BuildAudioArguments(
        PreviewSnapshot snapshot,
        PreviewRange range,
        string outputPath)
    {
        var inputs = snapshot.Clips
            .Where(clip => clip.Track == TrackKind.Audio && clip.HasAudio && clip.Kind is MediaKind.Video or MediaKind.Audio)
            .Where(clip => clip.Start < range.End && clip.End > range.Start)
            .OrderBy(clip => clip.TrackIndex)
            .ThenBy(clip => clip.Start)
            .ToList();
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        foreach (var clip in inputs)
        {
            var overlapStart = Math.Max(range.Start, clip.Start);
            var localOffset = Math.Max(0, overlapStart - clip.Start);
            var inputDuration = Math.Max(0.05, Math.Min(range.End, clip.End) - overlapStart);
            arguments.AddRange(["-ss", Format(clip.SourceStart + localOffset),
                "-t", Format(inputDuration), "-i", clip.Path]);
        }

        var filters = new List<string>
        {
            $"anullsrc=channel_layout=stereo:sample_rate=48000:d={Format(range.Duration)}[silence]"
        };
        var labels = new List<string> { "[silence]" };
        for (var index = 0; index < inputs.Count; index++)
        {
            var clip = inputs[index];
            var overlapStart = Math.Max(range.Start, clip.Start);
            var localOffset = Math.Max(0, overlapStart - clip.Start);
            var relativeStart = Math.Max(0, overlapStart - range.Start);
            var inputDuration = Math.Max(0.05, Math.Min(range.End, clip.End) - overlapStart);
            var delay = Math.Max(0, (long)Math.Round(relativeStart * 1000));
            var effects = BuildAudioFilters(clip, localOffset, inputDuration);
            filters.Add(
                $"[{index}:a:0]aresample=48000,atrim=0:{Format(inputDuration)},asetpts=PTS-STARTPTS," +
                $"volume={(clip.IsMuted ? "0" : Format(clip.Volume))}," + effects +
                $"adelay={delay}|{delay}[a{index}]");
            labels.Add($"[a{index}]");
        }
        filters.Add(
            $"{string.Concat(labels)}amix=inputs={labels.Count}:duration=longest:dropout_transition=0:normalize=0," +
            $"atrim=0:{Format(range.Duration)},asetpts=PTS-STARTPTS[aout]");
        arguments.AddRange([
            "-filter_complex_threads", "2",
            "-filter_complex", string.Join(';', filters),
            "-map", "[aout]", "-vn", "-t", Format(range.Duration),
            "-c:a", "aac", "-b:a", "160k", "-ar", "48000", "-ac", "2",
            "-movflags", "+faststart", outputPath
        ]);
        return arguments;
    }

    private static IReadOnlyList<string> BuildStillArguments(
        PreviewSnapshot snapshot,
        double timelinePosition,
        string outputPath)
    {
        const int width = 960;
        const int height = 540;
        var inputs = snapshot.Clips
            .Where(clip => clip.Track == TrackKind.Visual && clip.Kind is MediaKind.Video or MediaKind.Image)
            .Where(clip => timelinePosition >= clip.Start && timelinePosition < clip.End)
            .OrderBy(clip => clip.TrackIndex)
            .ThenBy(clip => clip.Start)
            .ToList();
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        foreach (var clip in inputs)
        {
            if (clip.Kind == MediaKind.Image)
            {
                arguments.AddRange(["-loop", "1", "-i", clip.Path]);
            }
            else
            {
                var sourcePosition = clip.SourceStart + Math.Max(0, timelinePosition - clip.Start);
                arguments.AddRange(["-ss", Format(sourcePosition), "-i", clip.Path]);
            }
        }
        var filters = new List<string> { $"color=c=black:s={width}x{height}:r=1:d=1,format=rgba[base0]" };
        var previous = "base0";
        for (var index = 0; index < inputs.Count; index++)
        {
            var color = BuildColorFilters(inputs[index]);
            filters.Add(
                $"[{index}:v:0]scale={width}:{height}:force_original_aspect_ratio=decrease," +
                $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:black,setsar=1,format=rgba," +
                color + $"trim=duration=1,setpts=PTS-STARTPTS[v{index}]");
            filters.Add($"[{previous}][v{index}]overlay=0:0:eof_action=pass:shortest=0[layer{index}]");
            previous = $"layer{index}";
        }
        filters.Add($"[{previous}]format=yuvj420p[vout]");
        arguments.AddRange([
            "-filter_complex", string.Join(';', filters),
            "-map", "[vout]", "-frames:v", "1", "-q:v", "3", outputPath
        ]);
        return arguments;
    }

    private static PreviewSnapshot Capture(EditorProject project, PreviewPipeline pipeline, bool halfQuality)
    {
        var clipSource = pipeline == PreviewPipeline.Video ? project.GetVisualClips() : project.GetAudioClips();
        var clips = new List<PreviewClipSnapshot>();
        foreach (var clip in clipSource)
        {
            var asset = project.FindAsset(clip.AssetId);
            if (asset is null || asset.IsMissing || string.IsNullOrWhiteSpace(asset.Path) || !File.Exists(asset.Path)) continue;
            if (pipeline == PreviewPipeline.Video && asset.Kind is not (MediaKind.Video or MediaKind.Image)) continue;
            if (pipeline == PreviewPipeline.Audio && (asset.Kind is not (MediaKind.Video or MediaKind.Audio) || !asset.HasAudio)) continue;
            clips.Add(new PreviewClipSnapshot(
                clip.Id, clip.AssetId, asset.Path, asset.Kind, asset.HasAudio,
                clip.Track, clip.TrackIndex, clip.Start, clip.SourceStart, clip.Duration,
                clip.Volume, clip.IsMuted, clip.Pan, clip.FadeIn, clip.FadeOut,
                clip.Bass, clip.Mid, clip.Treble,
                clip.Brightness, clip.Contrast, clip.Saturation, clip.Temperature,
                asset.FileSizeBytes, SafeLastWriteTicks(asset.Path)));
        }
        var signatureSource = new StringBuilder()
            .Append("preview-v6|").Append(pipeline).Append('|');
        if (pipeline == PreviewPipeline.Video)
        {
            signatureSource.Append(halfQuality).Append('|')
                .Append(project.CanvasWidth).Append('x').Append(project.CanvasHeight).Append('@').Append(project.FrameRate);
        }
        else
        {
            signatureSource.Append("stereo@48000");
        }
        foreach (var clip in clips.OrderBy(item => item.TrackIndex).ThenBy(item => item.Start).ThenBy(item => item.Id))
        {
            signatureSource.Append('|').Append(clip.Id).Append('|').Append(clip.AssetId).Append('|').Append(clip.Path)
                .Append('|').Append(clip.FileSize).Append('|').Append(clip.LastWriteTicks)
                .Append('|').Append(clip.TrackIndex).Append('|').Append(Format(clip.Start))
                .Append('|').Append(Format(clip.SourceStart)).Append('|').Append(Format(clip.Duration));
            if (pipeline == PreviewPipeline.Video)
            {
                signatureSource.Append('|').Append(Format(clip.Brightness)).Append('|').Append(Format(clip.Contrast))
                    .Append('|').Append(Format(clip.Saturation)).Append('|').Append(Format(clip.Temperature));
            }
            else
            {
                signatureSource.Append('|').Append(Format(clip.Volume)).Append('|').Append(clip.IsMuted)
                    .Append('|').Append(Format(clip.Pan)).Append('|').Append(Format(clip.FadeIn))
                    .Append('|').Append(Format(clip.FadeOut)).Append('|').Append(Format(clip.Bass))
                    .Append('|').Append(Format(clip.Mid)).Append('|').Append(Format(clip.Treble));
            }
        }
        var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signatureSource.ToString())))
            .ToLowerInvariant()[..24];
        return new PreviewSnapshot(signature, project.FrameRate, halfQuality, clips);
    }

    private static PreviewRange CreateRange(double timelinePosition)
    {
        var bounded = Math.Max(0, timelinePosition);
        var start = Math.Floor(bounded / SegmentStep) * SegmentStep;
        return new PreviewRange(start, SegmentStep + SegmentOverlap);
    }

    private static string BuildColorFilters(PreviewClipSnapshot clip)
    {
        var filters = new List<string>();
        if (Math.Abs(clip.Brightness) > 0.001 || Math.Abs(clip.Contrast - 1) > 0.001 || Math.Abs(clip.Saturation - 1) > 0.001)
            filters.Add($"eq=brightness={Format(clip.Brightness)}:contrast={Format(clip.Contrast)}:saturation={Format(clip.Saturation)}");
        if (Math.Abs(clip.Temperature) > 0.001)
        {
            var red = Math.Clamp(1 + clip.Temperature * 0.22, 0.5, 1.5);
            var blue = Math.Clamp(1 - clip.Temperature * 0.22, 0.5, 1.5);
            filters.Add($"colorchannelmixer=rr={Format(red)}:gg=1:bb={Format(blue)}");
        }
        return filters.Count == 0 ? string.Empty : string.Join(',', filters) + ",";
    }

    private static string BuildAudioFilters(PreviewClipSnapshot clip, double localOffset, double inputDuration)
    {
        var filters = new List<string>();
        if (Math.Abs(clip.Bass) > 0.01) filters.Add($"bass=g={Format(clip.Bass)}");
        if (Math.Abs(clip.Mid) > 0.01) filters.Add($"equalizer=f=1000:t=q:w=1:g={Format(clip.Mid)}");
        if (Math.Abs(clip.Treble) > 0.01) filters.Add($"treble=g={Format(clip.Treble)}");
        if (Math.Abs(clip.Pan) > 0.001)
        {
            var left = clip.Pan > 0 ? 1 - clip.Pan : 1;
            var right = clip.Pan < 0 ? 1 + clip.Pan : 1;
            filters.Add($"pan=stereo|c0={Format(left)}*c0|c1={Format(right)}*c1");
        }
        if (clip.FadeIn > localOffset + 0.01)
            filters.Add($"afade=t=in:st=0:d={Format(Math.Min(inputDuration, clip.FadeIn - localOffset))}");
        if (clip.FadeOut > 0.01)
        {
            var fadeStart = clip.Duration - clip.FadeOut - localOffset;
            if (fadeStart < inputDuration)
            {
                var start = Math.Max(0, fadeStart);
                filters.Add($"afade=t=out:st={Format(start)}:d={Format(Math.Max(0.01, Math.Min(clip.FadeOut, inputDuration - start)))}");
            }
        }
        return filters.Count == 0 ? string.Empty : string.Join(',', filters) + ",";
    }

    private static bool IsUsable(string path, long minimumLength = 1024)
        => File.Exists(path) && new FileInfo(path).Length > minimumLength;

    private static void EnsureRendered(ProcessResult result, string path, string message, long minimumLength = 1024)
    {
        if (result.ExitCode != 0 || !IsUsable(path, minimumLength))
            throw new InvalidOperationException($"{message}.\n{FfmpegOutput.LastMeaningfulLine(result.StandardError)}");
    }

    private static void TryDeleteTemporary(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static long SafeLastWriteTicks(string path)
    {
        try { return File.GetLastWriteTimeUtc(path).Ticks; } catch { return 0; }
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private sealed record PreviewRange(double Start, double Duration)
    {
        public double End => Start + Duration;
    }

    private sealed record PreviewSnapshot(
        string Signature,
        int FrameRate,
        bool HalfQuality,
        IReadOnlyList<PreviewClipSnapshot> Clips);

    private sealed record PreviewClipSnapshot(
        Guid Id,
        Guid AssetId,
        string Path,
        MediaKind Kind,
        bool HasAudio,
        TrackKind Track,
        int TrackIndex,
        double Start,
        double SourceStart,
        double Duration,
        double Volume,
        bool IsMuted,
        double Pan,
        double FadeIn,
        double FadeOut,
        double Bass,
        double Mid,
        double Treble,
        double Brightness,
        double Contrast,
        double Saturation,
        double Temperature,
        long FileSize,
        long LastWriteTicks)
    {
        public double End => Start + Duration;
    }
}

public enum PreviewPipeline
{
    Video,
    Audio
}

public sealed record TimelinePreviewSegment(
    PreviewPipeline Pipeline,
    string Signature,
    string Path,
    double TimelineStart,
    double Duration)
{
    public double TimelineEnd => TimelineStart + Duration;
    public bool Contains(double timelinePosition)
        => timelinePosition >= TimelineStart - 0.001 && timelinePosition < TimelineEnd - 0.001;
}

public sealed record CompositedStillFrame(string Signature, string Path, double TimelinePosition);
