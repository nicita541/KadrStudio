using KadrStudio.Models;
using KadrStudio.Services;
using Xunit;

namespace KadrStudio.Integration.Tests;

public sealed class AnalysisSubtitleIntegrationTests
{
    private static readonly object EnvironmentLock = new();

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
