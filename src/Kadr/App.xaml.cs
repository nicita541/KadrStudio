using System.Windows;
using System.Windows.Threading;
using KadrStudio.Services;
using KadrStudio.ViewModels;
using KadrStudio.Views;

namespace KadrStudio;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);

        if (e.Args.Contains("--launch-smoke", StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunLaunchSmokeAsync();
            return;
        }

        if (e.Args.FirstOrDefault() is { } projectPath &&
            File.Exists(projectPath) &&
            Path.GetExtension(projectPath).Equals(".kadr", StringComparison.OrdinalIgnoreCase))
        {
            var editor = new MainWindow(Path.GetFullPath(projectPath));
            MainWindow = editor;
            editor.Show();
            return;
        }

        var startWindow = new StartWindow();
        MainWindow = startWindow;
        startWindow.Show();
    }

    private async Task RunLaunchSmokeAsync()
    {
        try
        {
            var basePath = AppContext.BaseDirectory;
            var requiredFiles = new[]
            {
                Path.Combine(basePath, "KadrStudio.exe"),
                Path.Combine(basePath, "tools", "ffmpeg.exe"),
                Path.Combine(basePath, "tools", "ffprobe.exe"),
                Path.Combine(basePath, "mediahost", "Kadr.MediaHost.exe")
            };
            var missing = requiredFiles.Where(path => !File.Exists(path)).ToArray();
            if (missing.Length > 0)
                throw new FileNotFoundException($"Publish is incomplete: {string.Join(", ", missing.Select(Path.GetFileName))}");

            var workspace = EditorWorkspaceCompositionRoot.Create();
            workspace.FfmpegLocator.EnsureAvailable();
            await using var viewModel = new MainViewModel(workspace);
            if (viewModel.CoreState.Tracks.IsDefaultOrEmpty)
                throw new InvalidDataException("Editor workspace has no default tracks.");
            Shutdown(0);
        }
        catch
        {
            Shutdown(1);
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"Произошла непредвиденная ошибка.\n\n{e.Exception.Message}",
            "Kadr Studio",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
