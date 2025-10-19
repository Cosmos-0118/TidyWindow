using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.ViewModels;
using Forms = System.Windows.Forms;

namespace TidyWindow.App.Views;

public partial class DeepScanPage : Page
{
    private readonly DeepScanViewModel _viewModel;

    public DeepScanPage(DeepScanViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        Loaded += Page_OnLoaded;
    }

    private async void Page_OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= Page_OnLoaded;

        if (_viewModel.RefreshCommand is IAsyncRelayCommand asyncCommand)
        {
            await asyncCommand.ExecuteAsync(null);
        }
        else
        {
            _viewModel.RefreshCommand.Execute(null);
        }
    }

    private void BrowseButton_OnClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            SelectedPath = _viewModel.TargetPath,
            ShowNewFolderButton = false,
            Description = "Select a folder to scan"
        };

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            _viewModel.TargetPath = dialog.SelectedPath;
        }
    }
}
