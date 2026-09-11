using System.Collections;
using System.Reflection;
using KadrStudio.Core.Domain;
using KadrStudio.Infrastructure.Caching;
using KadrStudio.Playback;
using KadrStudio.Services;
using Xunit;

namespace KadrStudio.UiAdapters.Tests;

public sealed class PreviewProxyLifetimeTests
{
    [Fact]
    public async Task Repeated_configuration_releases_completed_jobs_and_retired_tokens()
    {
        var root = Path.Combine(Path.GetTempPath(), "kadr-proxy-lifetime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "fixture.mp4");
            File.WriteAllText(path, "cancel before decoder starts");
            using var artifacts = new DiskMediaArtifactCache(Path.Combine(root, "cache"));
            await using var store = new PreviewProxyStore(new FfmpegLocator(), artifacts);
            for (var index = 0; index < 100; index++)
            {
                var project = ProjectState.CreateNew();
                var source = new MediaSource(Guid.NewGuid(), path, "fixture", MediaKind.Video,
                    TimelineTime.FromSeconds(2), false, 2560, 1440, Streams: []);
                project = project with
                {
                    Sources = project.Sources.Add(source.Id, source),
                    MediaClips = [new MediaClip(Guid.NewGuid(), source.Id,
                        project.Tracks.First(track => track.Kind == TrackKind.Visual).Id,
                        TimelineTime.Zero, TimelineTime.Zero, source.Duration, Video: new VideoParameters())]
                };
                store.Queue(project, highResolutionOnly: true);
            }
            store.Configure(ProjectState.CreateNew());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (RetainedCount(store, "_allJobs") != 0 || RetainedCount(store, "_retiredGenerations") != 0)
                await Task.Delay(10, timeout.Token);
            Assert.Equal("Оригинал", store.StatusText);
        }
        finally { Directory.Delete(root, true); }
    }

    private static int RetainedCount(PreviewProxyStore store, string field)
    {
        var sync = typeof(PreviewProxyStore).GetField("_sync", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;
        lock (sync)
            return ((IEnumerable)typeof(PreviewProxyStore).GetField(field,
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!).Cast<object>().Count();
    }
}
