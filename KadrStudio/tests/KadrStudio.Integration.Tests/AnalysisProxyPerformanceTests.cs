using System.Diagnostics;
using System.Text.Json;
using KadrStudio.Core.Domain;
using KadrStudio.Services;
using KadrStudio.Services.Editorial;
using Xunit;

namespace KadrStudio.Integration.Tests;

public sealed class AnalysisProxyPerformanceTests
{
    [Fact(Timeout = 180_000)]
    public async Task Benchmark_analysis_proxy_cache_when_requested()
    {
        var sourcePath = Environment.GetEnvironmentVariable("KADR_ANALYSIS_PROXY_BENCHMARK_SOURCE");
        var outputPath = Environment.GetEnvironmentVariable("KADR_ANALYSIS_PROXY_BENCHMARK_OUTPUT");
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(outputPath)) return;

        var locator = new FfmpegLocator();
        locator.EnsureAvailable();
        var processRunner = new ProcessRunner();
        var probe = await new MediaProbeService(locator, processRunner).ProbeAsync(sourcePath, verifyContent: true);
        var video = probe.Streams.First(item => item.Kind == MediaStreamKind.Video);
        var audio = probe.Streams.FirstOrDefault(item => item.Kind == MediaStreamKind.Audio);
        var source = new MediaSource(
            Guid.NewGuid(), Path.GetFullPath(sourcePath), Path.GetFileName(sourcePath), MediaKind.Video,
            probe.Duration, audio is not null, probe.Width, probe.Height,
            probe.FrameRate ?? new FrameRate(30), video.Codec, audio?.Codec ?? "",
            probe.Fingerprint.Length, probe.Fingerprint.LastWriteUtcTicks, probe.Fingerprint.FastHash,
            FastFingerprint: probe.Fingerprint.FastHash,
            VerifiedFingerprint: probe.Fingerprint.VerifiedHash ?? "",
            Streams: probe.Streams,
            IsVariableFrameRate: probe.IsVariableFrameRate);
        var builder = new AnalysisProxyBuilder(locator, processRunner);

        var cold = Stopwatch.StartNew();
        await using (var bundle = await builder.BuildAsync(source, CancellationToken.None))
            Assert.All(bundle.Files, item => Assert.True(File.Exists(item.Path)));
        cold.Stop();

        var warm = Stopwatch.StartNew();
        await using (var bundle = await builder.BuildAsync(source, CancellationToken.None))
            Assert.All(bundle.Files, item => Assert.True(File.Exists(item.Path)));
        warm.Stop();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(new
        {
            source = source.Path,
            sourceBytes = new FileInfo(source.Path).Length,
            coldMs = cold.Elapsed.TotalMilliseconds,
            warmMs = warm.Elapsed.TotalMilliseconds,
            speedup = cold.Elapsed.TotalMilliseconds / Math.Max(0.001, warm.Elapsed.TotalMilliseconds)
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
