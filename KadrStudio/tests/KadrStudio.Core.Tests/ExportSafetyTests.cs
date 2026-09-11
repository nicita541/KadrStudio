using KadrStudio.Application.Rendering;
using KadrStudio.Core.Domain;
using KadrStudio.Infrastructure.Jobs;
using KadrStudio.Infrastructure.Rendering;
using KadrStudio.Infrastructure.Storage;
using System.Runtime.InteropServices;

namespace KadrStudio.Core.Tests;

public sealed class ExportSafetyTests
{
    [Fact]
    public void Explicit_replacement_cannot_target_a_hardlink_to_a_source()
    {
        var root = Path.Combine(Path.GetTempPath(), "KadrStudio", "export-hardlink", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.mp4");
            var alias = Path.Combine(root, "result.mp4");
            File.WriteAllText(source, "protected original");
            Assert.True(CreateHardLink(alias, source, IntPtr.Zero),
                $"CreateHardLink failed: {Marshal.GetLastWin32Error()}");

            Assert.Throws<IOException>(() => OutputFileGuard.Validate(alias, [source], allowOverwrite: true));
            Assert.Equal("protected original", File.ReadAllText(source));
            Assert.Equal("protected original", File.ReadAllText(alias));
        }
        finally { Directory.Delete(root, true); }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);

    [Fact]
    public async Task Export_refuses_existing_destination_without_explicit_replacement()
    {
        var path = Path.Combine(Path.GetTempPath(), "kadr-export-" + Guid.NewGuid().ToString("N") + ".mp4");
        await File.WriteAllTextAsync(path, "previous export");
        try
        {
            var plan = new RenderPlan(Guid.NewGuid(), 0, 320, 240, FrameRate.Fps30,
                new TimeRange(TimelineTime.Zero, TimelineTime.FromSeconds(1)), [], [], [], "v", "a", "o", "c");
            await using var scheduler = new BackgroundJobScheduler();
            using var executable = new ExecutableFixture();
            var engine = new FfmpegRenderEngine(executable.Path, new UnreachableRenderer(), scheduler);
            await Assert.ThrowsAsync<IOException>(() => engine.RenderAsync(plan,
                new RenderOutputOptions(RenderPurpose.Export, path, 320, 240)));
            Assert.Equal("previous export", await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Export_refuses_source_path_before_starting_renderer(bool audio)
    {
        var root = Path.Combine(Path.GetTempPath(), "KadrStudio", "export-safety", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "original.mp4");
        await File.WriteAllTextAsync(path, "original bytes");
        try
        {
            var range = new TimeRange(TimelineTime.Zero, TimelineTime.FromSeconds(1));
            var plan = new RenderPlan(Guid.NewGuid(), 0, 320, 240, FrameRate.Fps30, range,
                audio ? [] : [new RenderVisualLayer(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0,
                    path, MediaKind.Video, range, TimelineTime.Zero, new VideoParameters())],
                audio ? [new RenderAudioLayer(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0,
                    path, range, TimelineTime.Zero, new AudioParameters())] : [],
                [], "video", "audio", "overlay", "content");
            await using var scheduler = new BackgroundJobScheduler();
            using var executable = new ExecutableFixture();
            var engine = new FfmpegRenderEngine(executable.Path, new UnreachableRenderer(), scheduler);

            await Assert.ThrowsAsync<IOException>(() => engine.RenderAsync(plan,
                new RenderOutputOptions(RenderPurpose.Export, path, 320, 240)));

            Assert.Equal("original bytes", await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class UnreachableRenderer : IRenderCommandBuilder
    {
        public ExternalRenderCommand Build(RenderPlan plan, RenderOutputOptions options)
            => throw new InvalidOperationException("Unsafe export reached the renderer.");
    }

    private sealed class ExecutableFixture : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kadr-render-fixture-" + Guid.NewGuid().ToString("N"));
        public ExecutableFixture()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path, "not executed: command construction must be rejected first");
            File.WriteAllText(System.IO.Path.Combine(_root, "ffprobe.exe"), "not executed");
        }
        public string Path => System.IO.Path.Combine(_root, "ffmpeg.exe");
        public void Dispose() => Directory.Delete(_root, true);
    }
}
