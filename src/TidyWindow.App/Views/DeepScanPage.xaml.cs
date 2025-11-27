using System;
using System.Threading.Tasks;
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
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
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

    private async void DeleteButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: DeepScanItemViewModel item })
        {
            return;
        }

        var itemKind = item.IsDirectory ? "folder" : "file";
        var message = $"We cannot tell whether '{item.Name}' is important. Deleting this {itemKind} is permanent and your responsibility.\n\nDo you want to continue?";
        var confirmation = System.Windows.MessageBox.Show(
            message,
            "Confirm permanent deletion",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        await ExecuteDeleteCommandAsync(item);

        e.Handled = true;
    }

    private async Task ExecuteDeleteCommandAsync(DeepScanItemViewModel item)
    {
        var deleteCommand = _viewModel.DeleteFindingCommand;

        if (deleteCommand is IAsyncRelayCommand<DeepScanItemViewModel?> asyncCommandWithParam)
        {
            await asyncCommandWithParam.ExecuteAsync(item);
            return;
        }

        if (deleteCommand is IAsyncRelayCommand asyncCommand)
        {
            await asyncCommand.ExecuteAsync(item);
            return;
        }

        if (deleteCommand is IRelayCommand relayCommand && relayCommand.CanExecute(item))
        {
            relayCommand.Execute(item);
        }
    }
}
