using System.Windows;
using System.Windows.Controls;
using KadrStudio.Core.Domain;

namespace KadrStudio.Views;

public sealed class AudioStreamSelectionWindow : Window
{
    private sealed record Choice(int? Index, string Label);
    private readonly ComboBox _streams;
    public int? SelectedStreamIndex => (_streams.SelectedItem as Choice)?.Index;

    public AudioStreamSelectionWindow(MediaSource source)
    {
        Title = "Аудиодорожка для таймлайна";
        SetResourceReference(BackgroundProperty, "WindowBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = source.Name, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var audio = source.Streams.Where(stream => stream.Kind == MediaStreamKind.Audio).ToArray();
        var choices = audio.Select(stream => new Choice(stream.StreamIndex,
            $"Поток {stream.StreamIndex}: {stream.Language} {stream.Title} · {stream.Codec}, {stream.Channels} кан."
            + (stream.IsDefault ? " (по умолчанию)" : ""))).ToList();
        choices.Add(new Choice(null, "Все аудиопотоки — на отдельных дорожках"));
        _streams = new ComboBox
        {
            ItemsSource = choices, DisplayMemberPath = nameof(Choice.Label),
            SelectedIndex = Math.Max(0, Array.FindIndex(audio, stream => stream.IsDefault)),
            MinHeight = 30
        };
        panel.Children.Add(_streams);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var accept = new Button { Content = "Добавить", IsDefault = true, Padding = new Thickness(14, 6, 14, 6) };
        accept.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(accept);
        buttons.Children.Add(new Button { Content = "Отмена", IsCancel = true, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(14, 6, 14, 6) });
        panel.Children.Add(buttons);
        Content = panel;
    }
}
