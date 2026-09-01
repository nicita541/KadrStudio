using System.Collections.ObjectModel;
using System.Windows;
using KadrStudio.Application.Storage;

namespace KadrStudio.Views;

public partial class RecoveryWindow : Window
{
    public RecoveryWindow(IEnumerable<RecoveryProjectInfo> recoveries)
    {
        InitializeComponent();
        Recoveries = new ObservableCollection<RecoveryProjectInfo>(recoveries);
        DataContext = this;
        RecoveryList.SelectedIndex = Recoveries.Count > 0 ? 0 : -1;
    }

    public ObservableCollection<RecoveryProjectInfo> Recoveries { get; }
    public RecoveryProjectInfo? SelectedRecovery { get; private set; }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (RecoveryList.SelectedItem is not RecoveryProjectInfo recovery) return;
        SelectedRecovery = recovery;
        DialogResult = true;
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
