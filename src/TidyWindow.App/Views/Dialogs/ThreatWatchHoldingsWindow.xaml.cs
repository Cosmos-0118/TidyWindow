using System.Windows;
using TidyWindow.App.ViewModels.Dialogs;

namespace TidyWindow.App.Views.Dialogs;

public partial class ThreatWatchHoldingsWindow : Window
{
    public ThreatWatchHoldingsWindow(ThreatWatchHoldingsDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
