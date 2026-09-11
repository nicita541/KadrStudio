using System.Windows;
using System.Windows.Controls;
using KadrStudio.Application.Media;

namespace KadrStudio.Views;

public sealed class RelinkPreviewWindow : Window
{
    public RelinkPreviewWindow(MediaRelinkPreview preview)
    {
        Title = "Проверка соответствия исходников";
        Width = 880;
        Height = 480;
        MinWidth = 600;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(18) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var apply = new Button { Content = "Применить соответствия", IsDefault = true, Margin = new Thickness(8), Padding = new Thickness(12, 6, 12, 6),
            IsEnabled = !preview.Candidates.IsDefaultOrEmpty && preview.Candidates.All(candidate => candidate.CanApply) };
        apply.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(apply);
        buttons.Children.Add(new Button { Content = "Отмена", IsCancel = true, Margin = new Thickness(8), Padding = new Thickness(12, 6, 12, 6) });
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        var rows = new StackPanel();
        if (preview.Candidates.IsDefaultOrEmpty)
            rows.Children.Add(new TextBlock { Text = "Совместимые файлы не найдены. Можно указать файл вручную в медиатеке.", TextWrapping = TextWrapping.Wrap });
        foreach (var candidate in preview.Candidates)
        {
            var source = preview.Snapshot.State.Sources[candidate.SourceId];
            var verified = !string.IsNullOrWhiteSpace(source.VerifiedFingerprint) &&
                string.Equals(source.VerifiedFingerprint, candidate.Probe?.Fingerprint.VerifiedHash, StringComparison.OrdinalIgnoreCase);
            var status = !candidate.CanApply ? $"Несовместимый файл: {candidate.Compatibility}" : verified
                ? "Совпадение содержимого подтверждено SHA-256."
                : "Характеристики совместимы; полное совпадение содержимого не подтверждено.";
            rows.Children.Add(new TextBlock
            {
                Margin = new Thickness(0, 0, 0, 20), TextWrapping = TextWrapping.Wrap,
                Text = $"{source.Name}\nБыло: {source.Path}\nНайдено: {candidate.CandidatePath}\n{status}"
            });
        }
        panel.Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = panel;
    }
}
