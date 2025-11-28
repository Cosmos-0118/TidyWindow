using System.Windows;
using TidyWindow.App.ViewModels.Dialogs;

namespace TidyWindow.App.Views.Dialogs;

public partial class AntiSystemHoldingsWindow : Window
{
    public AntiSystemHoldingsWindow(AntiSystemHoldingsDialogViewModel viewModel)
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
