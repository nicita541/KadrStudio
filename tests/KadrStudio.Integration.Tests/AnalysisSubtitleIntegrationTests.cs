using System.Collections.Immutable;
using KadrStudio.Application.Media;
using KadrStudio.Core.Domain;
using KadrStudio.Models;
using KadrStudio.Services;
using Xunit;
using UiMediaKind = KadrStudio.Models.MediaKind;
using UiMarkerKind = KadrStudio.Models.MarkerKind;

namespace KadrStudio.Integration.Tests;

public sealed class AnalysisSubtitleIntegrationTests
{
    private static readonly object EnvironmentLock = new();

    [Fact]
    public void Anime_fingerprint_similarity_combines_visual_and_audio_recurrence()
    {
        var left = new AnimeSectionFingerprint(
            [0x0011223344556677UL, 0x8899AABBCCDDEEFFUL, 0x0F0F0F0F0F0F0F0FUL,
             0xAAAAAAAAAAAAAAAAUL, 0x5555555555555555UL, 0x0123456789ABCDEFUL,
             0xFEDCBA9876543210UL, 0x1111111111111111UL],
            [10, 30, 60, 100, 180, 220, 140, 50]);
        var same = left with { AudioEnvelope = [11, 29, 61, 98, 181, 219, 142, 49] };
        var different = new AnimeSectionFingerprint(
            Enumerable.Repeat(ulong.MaxValue, 8).ToImmutableArray(),
            Enumerable.Repeat((byte)255, 8).ToImmutableArray());

        Assert.True(AnimeFingerprintService.Similarity(left, same) > 0.95);
        Assert.True(AnimeFingerprintService.Similarity(left, different) <
                    AnimeFingerprintService.Similarity(left, same));
    }

    [Fact(Timeout = 60_000)]
    public async Task Semantic_boundary_cascade_verifies_hard_cut_to_the_exact_source_frame()
    {
        var root = CreateRoot();
        try
        {
            var locator = new FfmpegLocator();
            locator.EnsureAvailable();
            var source = Path.Combine(root, "hard-cut.mp4");
            var create = await new ProcessRunner().RunAsync(locator.FfmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "color=red:s=320x180:r=24:d=1",
                "-f", "lavfi", "-i", "color=blue:s=320x180:r=24:d=2",
                "-filter_complex", "[0:v][1:v]concat=n=2:v=1:a=0[v]",
                "-map", "[v]", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", source
            ]);
            Assert.Equal(0, create.ExitCode);
            var rate = new FrameRate(24);
            var probe = new MediaProbeResult(
                source, KadrStudio.Core.Domain.MediaKind.Video, TimelineTime.FromSeconds(3),
                [new MediaStreamDescriptor(0, MediaStreamKind.Video, "h264", Width: 320, Height: 180, FrameRate: rate)],
                new MediaFingerprint(new FileInfo(source).Length, File.GetLastWriteTimeUtc(source).Ticks, "test"),
                320, 180, rate, false);
            var asset = new MediaAsset
            {
                Id = Guid.NewGuid(), Path = source, Name = "hard-cut.mp4", Kind = UiMediaKind.Video,
                Duration = 3, Width = 320, Height = 180, FrameRate = 24, ProbeResult = probe
            };
            var approximate = new DetectedVideoRange(
                UiMarkerKind.Opening, 0.72, 2.28, "Опенинг", "rough semantic suggestion", 0.6);
            var baseline = new VideoAnalysisResult("baseline", 0, 3, [approximate]);
            var service = new VideoAnalysisService(locator, new ProcessRunner());

            var verification = await service.VerifyBoundaryAsync(source, 0.72, 0, 3, 24);
            var refined = await service.RefineSemanticBoundariesAsync(asset, baseline);

            var opening = Assert.Single(refined.Ranges);
            Assert.True(verification.CoarseCandidateCount > 0,
                $"No coarse candidates; result={verification}");
            Assert.True(Math.Abs(verification.VerifiedTime - 1) <= 1d / 24 + 0.001,
                $"Boundary verification did not choose the hard cut: {verification}");
            Assert.InRange(Math.Abs(opening.SourceStart - 1), 0, 1d / 24 + 0.001);
            Assert.Contains("кандидаты склеек", opening.Description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("1 кадр", opening.Description, StringComparison.OrdinalIgnoreCase);
        }
        finally { DeleteRoot(root); }
    }

    [Fact(Timeout = 60_000)]
    public async Task Soft_fade_is_not_reported_as_an_unambiguous_exact_boundary()
    {
        var root = CreateRoot();
        try
        {
            var locator = new FfmpegLocator();
            locator.EnsureAvailable();
            var source = Path.Combine(root, "soft-fade.mp4");
            var create = await new ProcessRunner().RunAsync(locator.FfmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "color=white:s=320x180:r=24:d=2",
                "-vf", "fade=t=out:st=0.5:d=1",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", source
            ]);
            Assert.Equal(0, create.ExitCode);

            var verification = await new VideoAnalysisService(locator, new ProcessRunner())
                .VerifyBoundaryAsync(source, 1, 0, 2, 24);

            Assert.False(verification.HasUnambiguousCandidate);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void Srt_parser_keeps_multiline_text_as_one_cue_and_removes_markup()
    {
        var cues = AutoSubtitleService.ParseSrt(
            "1\r\n00:00:01,000 --> 00:00:03,250\r\n<i>Первая</i> строка\r\nВторая строка\r\n\r\n");

        var cue = Assert.Single(cues);
        Assert.Equal(1, cue.Start);
        Assert.Equal(3.25, cue.End);
        Assert.Equal("Первая строка Вторая строка", cue.Text);
    }

    [Fact]
    public void Whisper_availability_is_explicit_when_local_binary_and_model_are_configured()
    {
        var root = CreateRoot();
        lock (EnvironmentLock)
        {
            var previousExe = Environment.GetEnvironmentVariable("KADR_STUDIO_WHISPER_EXE");
            var previousModel = Environment.GetEnvironmentVariable("KADR_STUDIO_WHISPER_MODEL");
            try
            {
                var executable = Path.Combine(root, "whisper-cli.exe");
                var model = Path.Combine(root, "ggml-test.bin");
                File.WriteAllBytes(executable, [0]);
                File.WriteAllBytes(model, [0]);
                Environment.SetEnvironmentVariable("KADR_STUDIO_WHISPER_EXE", executable);
                Environment.SetEnvironmentVariable("KADR_STUDIO_WHISPER_MODEL", model);

                var availability = new AutoSubtitleService(new FfmpegLocator(), new ProcessRunner())
                    .GetWhisperAvailability();

                Assert.True(availability.IsReady);
                Assert.Equal(executable, availability.ExecutablePath);
                Assert.Equal(model, availability.ModelPath);
            }
            finally
            {
                Environment.SetEnvironmentVariable("KADR_STUDIO_WHISPER_EXE", previousExe);
                Environment.SetEnvironmentVariable("KADR_STUDIO_WHISPER_MODEL", previousModel);
                DeleteRoot(root);
            }
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "KadrStudio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
