using System.Windows;
using System.Windows.Threading;
using KadrStudio.Views;

namespace KadrStudio;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);

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
