using System.Collections.Immutable;
using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Media;

public sealed record MediaFingerprint(
    long Length,
    long LastWriteUtcTicks,
    string FastHash,
    string? VerifiedHash = null);

public sealed record MediaProbeResult(
    string Path,
    MediaKind Kind,
    TimelineTime Duration,
    ImmutableArray<MediaStreamDescriptor> Streams,
    MediaFingerprint Fingerprint,
    int Width = 0,
    int Height = 0,
    FrameRate? FrameRate = null,
    bool IsVariableFrameRate = false);

public enum RelinkCompatibility
{
    Compatible,
    MissingCandidate,
    MediaKindMismatch,
    VideoGeometryMismatch,
    AudioChannelMismatch,
    DurationMismatch,
    FingerprintMismatch
}

public sealed record RelinkCandidate(
    Guid SourceId,
    string CandidatePath,
    RelinkCompatibility Compatibility,
    MediaProbeResult? Probe,
    string Message)
{
    public bool CanApply => Compatibility == RelinkCompatibility.Compatible && Probe is not null;
}

public interface IMediaFingerprintService
{
    Task<MediaFingerprint> ComputeFastAsync(string path, CancellationToken cancellationToken = default);
    Task<MediaFingerprint> ComputeVerifiedAsync(string path, CancellationToken cancellationToken = default);
}

public interface IMediaProbe
{
    Task<MediaProbeResult> ProbeAsync(string path, bool verifyContent, CancellationToken cancellationToken = default);
}

public interface IMediaRegistry
{
    ProjectState RefreshOnlineState(ProjectState project);
    RelinkCompatibility CheckCompatibility(MediaSource source, MediaProbeResult candidate);
    Task<ImmutableArray<RelinkCandidate>> FindRelinkCandidatesAsync(
        ProjectState project,
        IEnumerable<string> searchRoots,
        CancellationToken cancellationToken = default);
    Task<RelinkCandidate> ValidateRelinkAsync(
        MediaSource source,
        string candidatePath,
        bool requireVerifiedFingerprint = false,
        CancellationToken cancellationToken = default);
}

public static class MediaRelink
{
    public static MediaSource Apply(MediaSource source, MediaProbeResult candidate)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(candidate);
        var video = candidate.Streams.FirstOrDefault(item => item.Kind == MediaStreamKind.Video);
        var audio = candidate.Streams.FirstOrDefault(item => item.Kind == MediaStreamKind.Audio);
        return source with
        {
            PreviousPath = source.Path,
            Path = candidate.Path,
            Name = Path.GetFileName(candidate.Path),
            Kind = candidate.Kind,
            Duration = candidate.Duration,
            HasAudio = audio is not null,
            Width = candidate.Width,
            Height = candidate.Height,
            FrameRate = candidate.FrameRate,
            VideoCodec = video?.Codec ?? string.Empty,
            AudioCodec = audio?.Codec ?? string.Empty,
            FileSize = candidate.Fingerprint.Length,
            LastWriteUtcTicks = candidate.Fingerprint.LastWriteUtcTicks,
            Fingerprint = candidate.Fingerprint.FastHash,
            FastFingerprint = candidate.Fingerprint.FastHash,
            VerifiedFingerprint = candidate.Fingerprint.VerifiedHash ?? source.VerifiedFingerprint,
            Streams = candidate.Streams,
            IsVariableFrameRate = candidate.IsVariableFrameRate,
            OnlineState = MediaOnlineState.Online,
            ProxyPath = string.Empty
        };
    }
}
