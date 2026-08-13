using System.Collections.Immutable;
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
public sealed class EditorProjectMapper
{
    private static readonly Guid TextTrackNamespace = new("3d990ce8-0bda-44b3-b896-0265beec35dc");

    public ProjectState ToCore(EditorProject project, long revision = 0)
    {
        ArgumentNullException.ThrowIfNull(project);
        var visualCount = Math.Max(2, project.VisualTrackCount);
        var audioCount = Math.Max(2, project.AudioTrackCount);
        var tracks = ImmutableArray.CreateBuilder<TimelineTrack>(visualCount + audioCount + 1);
        var trackIds = new Dictionary<(CoreTrackKind Kind, int Index), Guid>();
        foreach (var track in project.Tracks
                     .Where(item => item.Index >= 0)
                     .GroupBy(item => (item.Kind, item.Index))
                     .Select(group => group.First())
                     .OrderBy(item => item.Kind).ThenBy(item => item.Index))
        {
            var id = track.Id == Guid.Empty
                ? StableGuid(TextTrackNamespace, project.Id, (int)track.Kind, track.Index)
                : track.Id;
            trackIds[(track.Kind, track.Index)] = id;
            tracks.Add(new TimelineTrack(id, track.Kind, track.Index,
                string.IsNullOrWhiteSpace(track.Name) ? DefaultTrackName(track.Kind, track.Index) : track.Name,
                track.IsMuted, track.IsLocked, track.IsVisible));
        }
        for (var index = 0; index < visualCount; index++)
            EnsureTrack(tracks, trackIds, project.Id, CoreTrackKind.Visual, index);
        for (var index = 0; index < audioCount; index++)
            EnsureTrack(tracks, trackIds, project.Id, CoreTrackKind.Audio, index);
        EnsureTrack(tracks, trackIds, project.Id, CoreTrackKind.Text, 0);
        var textTrackId = trackIds[(CoreTrackKind.Text, 0)];

        var sources = project.Media.ToImmutableDictionary(asset => asset.Id, ToCoreSource);
        var clips = project.Clips.Select(clip =>
        {
            var kind = clip.Track == UiTrackKind.Visual ? CoreTrackKind.Visual : CoreTrackKind.Audio;
            return new MediaClip(
                clip.Id,
                clip.AssetId,
                trackIds[(kind, clip.TrackIndex)],
                Time(clip.Start),
                Time(clip.SourceStart),
                Time(clip.Duration),
                clip.LinkGroupId,
                kind == CoreTrackKind.Visual
                    ? new VideoParameters(
                        clip.Brightness, clip.Contrast, clip.Saturation, clip.Temperature,
                        clip.PositionX, clip.PositionY, clip.ScaleX, clip.ScaleY, clip.Rotation,
                        clip.CropLeft, clip.CropTop, clip.CropRight, clip.CropBottom, clip.Opacity)
                    : null,
                kind == CoreTrackKind.Audio
                    ? new AudioParameters(
                        clip.Volume, clip.IsMuted, clip.Pan, Time(clip.FadeIn), Time(clip.FadeOut),
                        clip.Bass, clip.Mid, clip.Treble)
                    : null);
        }).ToImmutableArray();
        var texts = project.TextOverlays.Select(overlay => new TextClip(
            overlay.Id,
            textTrackId,
            Time(overlay.Start),
            Time(overlay.Duration),
            overlay.Text,
            new TextStyle(
                overlay.FontFamily, overlay.FontSize, overlay.Color, overlay.X, overlay.Y,
                overlay.Rotation, overlay.BoxWidth, overlay.BoxHeight, overlay.IsSubtitle))).ToImmutableArray();
        var markers = project.Markers.Select(marker => new KadrStudio.Core.Domain.TimelineMarker(
            marker.Id,
            (CoreMarkerKind)(int)marker.Kind,
            Time(marker.Start),
            Time(marker.Duration),
            marker.Title,
            marker.Description,
            marker.AssetId == Guid.Empty ? null : marker.AssetId,
            Time(marker.SourceStart),
            marker.Confidence,
            marker.Query)).ToImmutableArray();

        var currentMediaIds = clips.Select(item => item.Id).ToHashSet();
        var retainedTransitions = project.MigrationSnapshot?.Transitions
            .Where(item => currentMediaIds.Contains(item.FromClipId) && currentMediaIds.Contains(item.ToClipId))
            .Where(item => IsTransitionStillValid(item, clips, tracks))
            .ToImmutableArray() ?? [];
        var snapshotSources = project.MigrationSnapshot?.Sources ?? ImmutableDictionary<Guid, MediaSource>.Empty;
        sources = sources.ToImmutableDictionary(
            pair => pair.Key,
            pair => snapshotSources.TryGetValue(pair.Key, out var snapshot)
                ? pair.Value with
                {
                    PreviousPath = snapshot.PreviousPath,
                    OnlineState = snapshot.OnlineState,
                    FastFingerprint = snapshot.FastFingerprint,
                    VerifiedFingerprint = snapshot.VerifiedFingerprint,
                    Streams = snapshot.Streams,
                    IsVariableFrameRate = snapshot.IsVariableFrameRate,
                    ProxyPath = snapshot.ProxyPath
                }
                : pair.Value);

        return new ProjectState
        {
            Id = project.Id,
            Name = project.Name,
            CanvasWidth = project.CanvasWidth,
            CanvasHeight = project.CanvasHeight,
            FrameRate = project.FrameRateValue,
            Revision = Math.Max(0, revision),
            CreatedAt = project.CreatedAt,
            UpdatedAt = project.UpdatedAt,
            Tracks = tracks.ToImmutable(),
            Sources = sources,
            MediaClips = clips,
            TextClips = texts,
            Transitions = retainedTransitions,
            Markers = markers,
            InPoint = project.InPoint is null ? null : Time(project.InPoint.Value),
            OutPoint = project.OutPoint is null ? null : Time(project.OutPoint.Value)
        };
    }

    public EditorProject ToUi(ProjectState project, string? filePath = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var trackById = project.Tracks.ToDictionary(item => item.Id);
        var result = new EditorProject
        {
            FormatVersion = 2,
            Id = project.Id,
            Name = project.Name,
            CanvasWidth = project.CanvasWidth,
            CanvasHeight = project.CanvasHeight,
            FrameRateValue = project.FrameRate,
            CreatedAt = project.CreatedAt,
            UpdatedAt = project.UpdatedAt,
            FilePath = filePath,
            MigrationSnapshot = project,
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

    private static void EnsureTrack(
        ICollection<TimelineTrack> tracks,
        IDictionary<(CoreTrackKind Kind, int Index), Guid> ids,
        Guid projectId,
        CoreTrackKind kind,
        int index)
    {
        if (ids.ContainsKey((kind, index))) return;
        var id = StableGuid(TextTrackNamespace, projectId, (int)kind, index);
        ids.Add((kind, index), id);
        tracks.Add(new TimelineTrack(id, kind, index, DefaultTrackName(kind, index)));
    }

    private static string DefaultTrackName(CoreTrackKind kind, int index) => kind switch
    {
        CoreTrackKind.Visual => $"V{index + 1}",
        CoreTrackKind.Audio => $"A{index + 1}",
        _ => $"T{index + 1}"
    };

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

    private static bool IsTransitionStillValid(
        TimelineTransition transition,
        ImmutableArray<MediaClip> clips,
        IEnumerable<TimelineTrack> tracks)
    {
        var from = clips.FirstOrDefault(item => item.Id == transition.FromClipId);
        var to = clips.FirstOrDefault(item => item.Id == transition.ToClipId);
        var track = tracks.FirstOrDefault(item => item.Id == transition.TrackId);
        return from is not null && to is not null && track is not null &&
               from.TrackId == track.Id && to.TrackId == track.Id && from.End == to.Start &&
               transition.Duration > TimelineTime.Zero && transition.Duration <= from.Duration &&
               transition.Duration <= to.Duration && transition.Start >= from.Start &&
               transition.End <= to.End && transition.Range.Contains(from.End);
    }
    private static long SafeLastWriteTicks(string path) { try { return File.GetLastWriteTimeUtc(path).Ticks; } catch { return 0; } }
    private static string BuildFingerprint(MediaAsset asset) => $"{asset.FileSizeBytes:x}-{SafeLastWriteTicks(asset.Path):x}";

    private static Guid StableGuid(Guid namespaceId, Guid projectId, int kind, int index)
    {
        Span<byte> bytes = stackalloc byte[16];
        namespaceId.TryWriteBytes(bytes);
        Span<byte> project = stackalloc byte[16];
        projectId.TryWriteBytes(project);
        for (var offset = 0; offset < 16; offset++) bytes[offset] ^= project[offset];
        BitConverter.TryWriteBytes(bytes[8..12], kind);
        BitConverter.TryWriteBytes(bytes[12..16], index);
        return new Guid(bytes);
    }
}
