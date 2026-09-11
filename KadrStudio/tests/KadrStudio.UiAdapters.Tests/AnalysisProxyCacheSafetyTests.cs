using KadrStudio.Core.Domain;
using KadrStudio.Services;
using KadrStudio.Services.Editorial;
using Xunit;

namespace KadrStudio.UiAdapters.Tests;

public sealed class AnalysisProxyCacheSafetyTests
{
    [Fact]
    public async Task Bundle_disposal_does_not_own_arbitrary_temp_directory()
    {
        var directory = Path.Combine(KadrLocalDataPaths.TempRoot, "user-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "keep.txt");
        try
        {
            await File.WriteAllTextAsync(path, "keep");
            await new AnalysisProxyBundle(directory, []).DisposeAsync();
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("truncate")]
    [InlineData("same-length")]
    [InlineData("missing-chunk")]
    [InlineData("invalid-manifest")]
    public async Task Corrupt_bundle_is_rebuilt_without_removing_unknown_files(string damage)
    {
        var directory = Path.Combine(KadrLocalDataPaths.TempRoot, "cache-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var bundles = new HashSet<string>();
        try
        {
            var path = Path.Combine(directory, "source.wav");
            var locator = new FfmpegLocator();
            var runner = new ProcessRunner();
            var generated = await runner.RunAsync(locator.FfmpegPath,
                ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "anullsrc=r=16000:cl=mono", "-t", "0.2", path]);
            Assert.Equal(0, generated.ExitCode);
            var source = new MediaSource(Guid.NewGuid(), path, "source", MediaKind.Audio,
                TimelineTime.FromSeconds(0.2), true, 0, 0, new FrameRate(30), "", "pcm_s16le", Streams: []);
            var builder = new AnalysisProxyBuilder(locator, runner);
            await using var first = await builder.BuildAsync(source, CancellationToken.None);
            bundles.Add(first.Directory);
            var payload = Assert.Single(first.Files).Path;
            var original = await File.ReadAllBytesAsync(payload);
            switch (damage)
            {
                case "truncate": await File.WriteAllBytesAsync(payload, []); break;
                case "same-length": await File.WriteAllBytesAsync(payload, Enumerable.Repeat((byte)0xA5, original.Length).ToArray()); break;
                case "missing-chunk": File.Delete(payload); break;
                case "invalid-manifest": await File.WriteAllTextAsync(Path.Combine(first.Directory, "checksums.json"), "{"); break;
            }
            var foreign = Path.Combine(first.Directory, "user-note.txt");
            await File.WriteAllTextAsync(foreign, "preserve me");
            var rebuilt = await Task.WhenAll(builder.BuildAsync(source, CancellationToken.None),
                new AnalysisProxyBuilder(locator, runner).BuildAsync(source, CancellationToken.None));
            foreach (var repaired in rebuilt)
            {
                bundles.Add(repaired.Directory);
                Assert.Equal(original, await File.ReadAllBytesAsync(Assert.Single(repaired.Files).Path));
                await repaired.DisposeAsync();
            }
            Assert.Equal("preserve me", await File.ReadAllTextAsync(foreign));
            await using var restarted = await new AnalysisProxyBuilder(locator, runner).BuildAsync(source, CancellationToken.None);
            bundles.Add(restarted.Directory);
            Assert.Contains(restarted.Directory, rebuilt.Select(item => item.Directory));
        }
        finally
        {
            foreach (var bundle in bundles) Directory.Delete(bundle, true);
            Directory.Delete(directory, true);
        }
    }
}

