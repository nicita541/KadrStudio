using System.Collections.Immutable;
using KadrStudio.Application.Media;
using KadrStudio.Core.Domain;

namespace KadrStudio.Infrastructure.Media;

public sealed class MediaRegistry(IMediaProbe probe) : IMediaRegistry
{
    public ProjectState RefreshOnlineState(ProjectState project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project with
        {
            Sources = project.Sources.ToImmutableDictionary(
                pair => pair.Key,
                pair => pair.Value with
                {
                    OnlineState = File.Exists(pair.Value.Path)
                        ? MediaOnlineState.Online
                        : MediaOnlineState.Offline
                })
        };
    }

    public RelinkCompatibility CheckCompatibility(MediaSource source, MediaProbeResult candidate)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(candidate);
        if (source.Kind != candidate.Kind) return RelinkCompatibility.MediaKindMismatch;
        if (source.Kind != MediaKind.Audio && source.Width > 0 && source.Height > 0 &&
            (source.Width != candidate.Width || source.Height != candidate.Height))
            return RelinkCompatibility.VideoGeometryMismatch;
        var oldAudio = source.Streams.IsDefault
            ? null
            : source.Streams.FirstOrDefault(item => item.Kind == MediaStreamKind.Audio);
        var newAudio = candidate.Streams.FirstOrDefault(item => item.Kind == MediaStreamKind.Audio);
        if (oldAudio is not null && (newAudio is null || oldAudio.Channels != newAudio.Channels))
            return RelinkCompatibility.AudioChannelMismatch;
        var tolerance = Math.Max(TimelineTime.FromSeconds(0.5).Ticks, source.FrameRate?.FrameDuration.Ticks ?? 0);
        if (Math.Abs(source.Duration.Ticks - candidate.Duration.Ticks) > tolerance)
            return RelinkCompatibility.DurationMismatch;
        if (!string.IsNullOrWhiteSpace(source.VerifiedFingerprint) &&
            !string.IsNullOrWhiteSpace(candidate.Fingerprint.VerifiedHash) &&
            !source.VerifiedFingerprint.Equals(candidate.Fingerprint.VerifiedHash, StringComparison.OrdinalIgnoreCase))
            return RelinkCompatibility.FingerprintMismatch;
        return RelinkCompatibility.Compatible;
    }

    public async Task<ImmutableArray<RelinkCandidate>> FindRelinkCandidatesAsync(
        ProjectState project,
        IEnumerable<string> searchRoots,
        CancellationToken cancellationToken = default)
    {
        var offline = project.Sources.Values.Where(item => !File.Exists(item.Path)).ToArray();
        if (offline.Length == 0) return [];
        var roots = searchRoots.Select(Path.GetFullPath).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var filesByName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            foreach (var path in EnumerateFilesSafely(root, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                filesByName.GetOrAdd(Path.GetFileName(path), static _ => []).Add(path);
            }
        }
        var result = ImmutableArray.CreateBuilder<RelinkCandidate>();
        foreach (var source in offline)
        {
            if (!filesByName.TryGetValue(source.Name, out var candidates)) continue;
            foreach (var path in candidates)
            {
                RelinkCandidate candidate;
                try
                {
                    candidate = await ValidateRelinkAsync(source, path, false, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    continue;
                }
                if (candidate.CanApply)
                {
                    result.Add(candidate);
                    break;
                }
            }
        }
        return result.ToImmutable();
    }

    public async Task<RelinkCandidate> ValidateRelinkAsync(
        MediaSource source,
        string candidatePath,
        bool requireVerifiedFingerprint = false,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(candidatePath))
            return new RelinkCandidate(source.Id, Path.GetFullPath(candidatePath),
                RelinkCompatibility.MissingCandidate, null, "Candidate file does not exist.");
        var candidate = await probe.ProbeAsync(candidatePath, requireVerifiedFingerprint, cancellationToken).ConfigureAwait(false);
        var compatibility = CheckCompatibility(source, candidate);
        return new RelinkCandidate(source.Id, candidate.Path, compatibility, candidate,
            compatibility == RelinkCompatibility.Compatible ? "Compatible media." : $"Relink rejected: {compatibility}.");
    }

    private static IEnumerable<string> EnumerateFilesSafely(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            string[] files;
            string[] directories;
            try
            {
                files = Directory.GetFiles(directory);
                directories = Directory.GetDirectories(directory);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                continue;
            }
            foreach (var file in files) yield return file;
            foreach (var child in directories) pending.Push(child);
        }
    }
}

internal static class DictionaryExtensions
{
    public static TValue GetOrAdd<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key, Func<TKey, TValue> factory)
        where TKey : notnull
    {
        if (dictionary.TryGetValue(key, out var value)) return value;
        value = factory(key);
        dictionary.Add(key, value);
        return value;
    }
}
