using KadrStudio.Core.Domain;
using KadrStudio.Models;
using KadrStudio.Application.Media;
using CoreMarkerKind = KadrStudio.Core.Domain.MarkerKind;
using CoreMediaKind = KadrStudio.Core.Domain.MediaKind;
using CoreTrackKind = KadrStudio.Core.Domain.TrackKind;
using UiMarkerKind = KadrStudio.Models.MarkerKind;
using UiMediaKind = KadrStudio.Models.MediaKind;
using UiTrackKind = KadrStudio.Models.TrackKind;

namespace KadrStudio.Adapters;

/// <summary>
/// The only conversion boundary between mutable WPF controls and the immutable
/// editor core. UI collections never leak into storage, rendering or analysis.
/// </summary>
public sealed class ProjectViewMapper
{
    public ProjectViewState ToUi(ProjectState project, string? filePath = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var trackById = project.Tracks.ToDictionary(item => item.Id);
        var result = new ProjectViewState
        {
            Id = project.Id,
            Name = project.Name,
            CanvasWidth = project.CanvasWidth,
            CanvasHeight = project.CanvasHeight,
            FrameRateValue = project.FrameRate,
            CreatedAt = project.CreatedAt,
            UpdatedAt = project.UpdatedAt,
            FilePath = filePath,
            InPoint = project.InPoint?.TotalSeconds,
            OutPoint = project.OutPoint?.TotalSeconds
        };
        foreach (var track in project.Tracks)
            result.Tracks.Add(new EditorTrack
            {
                Id = track.Id, Kind = track.Kind, Index = track.Index, Name = track.Name,
                IsMuted = track.IsMuted, IsLocked = track.IsLocked, IsVisible = track.IsVisible
            });
        foreach (var source in project.Sources.Values) result.Media.Add(ToUiSource(source));
        foreach (var clip in project.MediaClips)
        {
            var track = trackById[clip.TrackId];
            if (track.Kind == CoreTrackKind.Text) continue;
            result.Clips.Add(new TimelineClip
            {
                Id = clip.Id,
                AssetId = clip.SourceId,
                Track = track.Kind == CoreTrackKind.Visual ? UiTrackKind.Visual : UiTrackKind.Audio,
                TrackIndex = track.Index,
                LinkGroupId = clip.LinkGroupId,
                Start = clip.Start.TotalSeconds,
                SourceStart = clip.SourceIn.TotalSeconds,
                Duration = clip.Duration.TotalSeconds,
                Brightness = clip.Video?.Brightness ?? 0,
                Contrast = clip.Video?.Contrast ?? 1,
                Saturation = clip.Video?.Saturation ?? 1,
                Temperature = clip.Video?.Temperature ?? 0,
                PositionX = clip.Video?.PositionX ?? 0.5,
                PositionY = clip.Video?.PositionY ?? 0.5,
                ScaleX = clip.Video?.ScaleX ?? 1,
                ScaleY = clip.Video?.ScaleY ?? 1,
                Rotation = clip.Video?.Rotation ?? 0,
                CropLeft = clip.Video?.CropLeft ?? 0,
                CropTop = clip.Video?.CropTop ?? 0,
                CropRight = clip.Video?.CropRight ?? 0,
                CropBottom = clip.Video?.CropBottom ?? 0,
                Opacity = clip.Video?.Opacity ?? 1,
                Volume = clip.Audio?.Volume ?? 1,
                IsMuted = clip.Audio?.IsMuted ?? false,
                Pan = clip.Audio?.Pan ?? 0,
                FadeIn = clip.Audio?.FadeIn.TotalSeconds ?? 0,
                FadeOut = clip.Audio?.FadeOut.TotalSeconds ?? 0,
                Bass = clip.Audio?.Bass ?? 0,
                Mid = clip.Audio?.Mid ?? 0,
                Treble = clip.Audio?.Treble ?? 0
            });
        }
        foreach (var clip in project.TextClips)
        {
            result.TextOverlays.Add(new TextOverlay
            {
                Id = clip.Id,
                Start = clip.Start.TotalSeconds,
                Duration = clip.Duration.TotalSeconds,
                Text = clip.Text,
                FontFamily = clip.Style.FontFamily,
                FontSize = clip.Style.FontSize,
                Color = clip.Style.Color,
                X = clip.Style.X,
                Y = clip.Style.Y,
                Rotation = clip.Style.Rotation,
                BoxWidth = clip.Style.BoxWidth,
                BoxHeight = clip.Style.BoxHeight,
                IsSubtitle = clip.Style.IsSubtitle
            });
        }
        foreach (var marker in project.Markers)
        {
            result.Markers.Add(new Models.TimelineMarker
            {
                Id = marker.Id,
                AssetId = marker.SourceId ?? Guid.Empty,
                Kind = (UiMarkerKind)(int)marker.Kind,
                Start = marker.Start.TotalSeconds,
                Duration = marker.Duration.TotalSeconds,
                Title = marker.Title,
                Description = marker.Description,
                SourceStart = marker.SourceStart.TotalSeconds,
                Confidence = marker.Confidence,
                Query = marker.Query
            });
        }
        foreach (var asset in result.Media)
            asset.IsMissing = project.Sources[asset.Id].OnlineState != MediaOnlineState.Online || !File.Exists(asset.Path);
        return result;
    }

    public KadrStudio.Core.Domain.TimelineMarker ToCoreMarker(Models.TimelineMarker marker)
        => new(
            marker.Id,
            (CoreMarkerKind)(int)marker.Kind,
            Time(marker.Start),
            Time(marker.Duration),
            marker.Title,
            marker.Description,
            marker.AssetId == Guid.Empty ? null : marker.AssetId,
            Time(marker.SourceStart),
            marker.Confidence,
            marker.Query);

    public TextClip ToCoreText(TextOverlay overlay, ProjectState project)
    {
        var textTrack = project.Tracks
            .Where(item => item.Kind == CoreTrackKind.Text)
            .OrderBy(item => item.Index)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("The project does not contain a text track.");
        return new TextClip(
            overlay.Id, textTrack.Id, Time(overlay.Start), Time(overlay.Duration), overlay.Text,
            new TextStyle(
                overlay.FontFamily, overlay.FontSize, overlay.Color, overlay.X, overlay.Y,
                overlay.Rotation, overlay.BoxWidth, overlay.BoxHeight, overlay.IsSubtitle));
    }

    public MediaSource ToCoreSource(MediaAsset asset)
    {
        if (asset.ProbeResult is { } probe)
        {
            var video = probe.Streams.FirstOrDefault(item => item.Kind == MediaStreamKind.Video);
            var audio = probe.Streams.FirstOrDefault(item => item.Kind == MediaStreamKind.Audio);
            return new MediaSource(
                asset.Id, probe.Path, asset.Name, probe.Kind, probe.Duration, audio is not null,
                probe.Width, probe.Height, probe.FrameRate, video?.Codec ?? string.Empty,
                audio?.Codec ?? string.Empty, probe.Fingerprint.Length, probe.Fingerprint.LastWriteUtcTicks,
                probe.Fingerprint.FastHash, OnlineState: MediaOnlineState.Online,
                FastFingerprint: probe.Fingerprint.FastHash,
                VerifiedFingerprint: probe.Fingerprint.VerifiedHash ?? string.Empty,
                Streams: probe.Streams, IsVariableFrameRate: probe.IsVariableFrameRate);
        }
        return new MediaSource(
            asset.Id, Path.GetFullPath(asset.Path), asset.Name, (CoreMediaKind)(int)asset.Kind,
            Time(asset.Duration), asset.HasAudio, asset.Width, asset.Height,
            asset.FrameRate > 0 ? ApproximateFrameRate(asset.FrameRate) : null,
            asset.VideoCodec, asset.AudioCodec, asset.FileSizeBytes,
            SafeLastWriteTicks(asset.Path), BuildFingerprint(asset),
            OnlineState: File.Exists(asset.Path) ? MediaOnlineState.Online : MediaOnlineState.Offline);
    }

    private static MediaAsset ToUiSource(MediaSource source)
        => new()
        {
            Id = source.Id,
            Path = source.Path,
            Name = source.Name,
            Kind = (UiMediaKind)(int)source.Kind,
            Duration = source.Duration.TotalSeconds,
            Width = source.Width,
            Height = source.Height,
            FrameRate = source.FrameRate?.FramesPerSecond ?? 0,
            HasAudio = source.HasAudio,
            VideoCodec = source.VideoCodec,
            AudioCodec = source.AudioCodec,
            FileSizeBytes = source.FileSize,
            ProbeResult = new MediaProbeResult(
                source.Path, source.Kind, source.Duration,
                source.Streams.IsDefault ? [] : source.Streams,
                new MediaFingerprint(source.FileSize, source.LastWriteUtcTicks,
                    string.IsNullOrWhiteSpace(source.FastFingerprint) ? source.Fingerprint : source.FastFingerprint,
                    string.IsNullOrWhiteSpace(source.VerifiedFingerprint) ? null : source.VerifiedFingerprint),
                source.Width, source.Height, source.FrameRate, source.IsVariableFrameRate)
        };

    private static FrameRate ApproximateFrameRate(double fps)
    {
        if (Math.Abs(fps - 23.976) < 0.01) return FrameRate.Fps23976;
        if (Math.Abs(fps - 29.97) < 0.01) return FrameRate.Fps2997;
        if (Math.Abs(fps - 59.94) < 0.01) return FrameRate.Fps5994;
        return new FrameRate(Math.Clamp((int)Math.Round(fps), 1, 240));
    }

    private static TimelineTime Time(double seconds) => TimelineTime.FromSeconds(Math.Max(0, seconds));

    private static long SafeLastWriteTicks(string path) { try { return File.GetLastWriteTimeUtc(path).Ticks; } catch { return 0; } }
    private static string BuildFingerprint(MediaAsset asset) => $"{asset.FileSizeBytes:x}-{SafeLastWriteTicks(asset.Path):x}";
}
