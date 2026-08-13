using System.Collections.Immutable;
using KadrStudio.Application.Editing;
using KadrStudio.Application.Media;
using KadrStudio.Core.Domain;
using KadrStudio.Infrastructure.Media;

namespace KadrStudio.Core.Tests;

public sealed class MediaRegistryTests
{
    [Fact]
    public async Task Fast_fingerprint_detects_content_change_with_same_size_and_timestamp()
    {
        using var directory = new TemporaryDirectory();
        var first = Path.Combine(directory.Path, "first.bin");
        var second = Path.Combine(directory.Path, "second.bin");
        await File.WriteAllBytesAsync(first, Enumerable.Repeat((byte)0x11, 200_000).ToArray());
        await File.WriteAllBytesAsync(second, Enumerable.Repeat((byte)0x22, 200_000).ToArray());
        var timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(first, timestamp);
        File.SetLastWriteTimeUtc(second, timestamp);
        var service = new FileMediaFingerprintService();

        var firstHash = await service.ComputeFastAsync(first);
        var secondHash = await service.ComputeFastAsync(second);

        Assert.Equal(firstHash.Length, secondHash.Length);
        Assert.Equal(firstHash.LastWriteUtcTicks, secondHash.LastWriteUtcTicks);
        Assert.NotEqual(firstHash.FastHash, secondHash.FastHash);
    }

    [Fact]
    public async Task Verified_fingerprint_hashes_complete_content()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "media.bin");
        await File.WriteAllBytesAsync(path, Enumerable.Range(0, 10_000).Select(item => (byte)item).ToArray());

        var fingerprint = await new FileMediaFingerprintService().ComputeVerifiedAsync(path);

        Assert.NotNull(fingerprint.VerifiedHash);
        Assert.Equal(64, fingerprint.FastHash.Length);
        Assert.Equal(64, fingerprint.VerifiedHash!.Length);
    }

    [Fact]
    public async Task Batch_relink_finds_compatible_named_media_and_applies_atomically()
    {
        using var directory = new TemporaryDirectory();
        var candidatePath = Path.Combine(directory.Path, "episode.mkv");
        await File.WriteAllBytesAsync(candidatePath, [1, 2, 3, 4]);
        var source = new MediaSource(
            Guid.NewGuid(), "Z:\\offline\\episode.mkv", "episode.mkv", MediaKind.Video,
            TimelineTime.FromSeconds(10), true, 1920, 1080, FrameRate.Fps23976,
            Streams:
            [
                new MediaStreamDescriptor(0, MediaStreamKind.Video, "hevc", Width: 1920, Height: 1080,
                    FrameRate: FrameRate.Fps23976),
                new MediaStreamDescriptor(1, MediaStreamKind.Audio, "aac", SampleRate: 48_000, Channels: 2)
            ],
            OnlineState: MediaOnlineState.Offline);
        var probeResult = new MediaProbeResult(
            candidatePath, MediaKind.Video, TimelineTime.FromSeconds(10), source.Streams,
            new MediaFingerprint(4, File.GetLastWriteTimeUtc(candidatePath).Ticks, "fast", "verified"),
            1920, 1080, FrameRate.Fps23976);
        var registry = new MediaRegistry(new FakeProbe(probeResult));
        var project = ProjectState.CreateNew() with { Sources = ImmutableDictionary<Guid, MediaSource>.Empty.Add(source.Id, source) };

        var found = await registry.FindRelinkCandidatesAsync(project, [directory.Path]);
        var session = new EditorSession(project);
        var result = session.Execute(new EditTransaction("relink", new RelinkSourcesCommand(found)));

        var relinked = result.State.Sources[source.Id];
        Assert.Equal(Path.GetFullPath(candidatePath), relinked.Path);
        Assert.Equal(source.Path, relinked.PreviousPath);
        Assert.Equal(MediaOnlineState.Online, relinked.OnlineState);
        Assert.Equal("verified", relinked.VerifiedFingerprint);
        Assert.Contains(source.Id, result.Changes.SourceIds);
    }

    [Fact]
    public async Task Relink_rejects_incompatible_audio_channel_layout()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "audio.wav");
        await File.WriteAllBytesAsync(path, [1]);
        var source = new MediaSource(
            Guid.NewGuid(), "Z:\\audio.wav", "audio.wav", MediaKind.Audio,
            TimelineTime.FromSeconds(5), true,
            Streams: [new MediaStreamDescriptor(0, MediaStreamKind.Audio, "pcm", SampleRate: 48_000, Channels: 2)]);
        var probe = new MediaProbeResult(
            path, MediaKind.Audio, TimelineTime.FromSeconds(5),
            [new MediaStreamDescriptor(0, MediaStreamKind.Audio, "pcm", SampleRate: 48_000, Channels: 1)],
            new MediaFingerprint(1, 1, "fast"));
        var registry = new MediaRegistry(new FakeProbe(probe));

        var candidate = await registry.ValidateRelinkAsync(source, path);

        Assert.Equal(RelinkCompatibility.AudioChannelMismatch, candidate.Compatibility);
        Assert.False(candidate.CanApply);
    }

    private sealed class FakeProbe(MediaProbeResult result) : IMediaProbe
    {
        public Task<MediaProbeResult> ProbeAsync(string path, bool verifyContent, CancellationToken cancellationToken = default)
            => Task.FromResult(result with { Path = Path.GetFullPath(path) });
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KadrStudio", "registry-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
