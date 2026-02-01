using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels;
using Forms = System.Windows.Forms;

namespace TidyWindow.App.Views;

public partial class DeepScanPage : Page, INavigationAware
{
    private readonly DeepScanViewModel _viewModel;

    public DeepScanPage(DeepScanViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;
        _viewModel.PageChanged += OnPageChanged;
        Unloaded += OnUnloaded;
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

    private void OnLocationOverlayMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Close the overlay when clicking the dimmed background (not the content panel)
        if (e.OriginalSource is not System.Windows.Controls.Grid grid || grid.Name != "LocationOverlay")
        {
            return;
        }

        if (_viewModel.HideLocationPickerCommand.CanExecute(null))
        {
            _viewModel.HideLocationPickerCommand.Execute(null);
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

        await ExecuteDeleteCommandAsync(item, _viewModel.DeleteFindingCommand);

        e.Handled = true;
    }

    private async void ForceDeleteButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: DeepScanItemViewModel item })
        {
            return;
        }

        if (!_viewModel.CanForceDelete)
        {
            return;
        }

        var itemKind = item.IsDirectory ? "folder" : "file";
        var message =
            $"Force delete will take ownership, break locks, and may schedule removal on reboot. This can disrupt apps if you remove an essential {itemKind}.\n\nAre you absolutely sure you want to proceed?";

        var confirmation = System.Windows.MessageBox.Show(
            message,
            "Force delete warning",
            MessageBoxButton.YesNo,
            MessageBoxImage.Stop,
            MessageBoxResult.No);

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        await ExecuteDeleteCommandAsync(item, _viewModel.ForceDeleteFindingCommand);

        e.Handled = true;
    }

    private async Task ExecuteDeleteCommandAsync(DeepScanItemViewModel item, object? command)
    {
        if (command is IAsyncRelayCommand<DeepScanItemViewModel?> asyncCommandWithParam)
        {
            await asyncCommandWithParam.ExecuteAsync(item);
            return;
        }

        if (command is IAsyncRelayCommand asyncCommand)
        {
            await asyncCommand.ExecuteAsync(item);
            return;
        }

        if (command is IRelayCommand relayCommand && relayCommand.CanExecute(item))
        {
            relayCommand.Execute(item);
        }
    }

    private void OnPageChanged(object? sender, EventArgs e)
    {
        ScrollToTop();
    }

    private void ScrollToTop()
    {
        var listViewer = FindScrollViewer(FindingsListView);
        listViewer?.ScrollToVerticalOffset(0);
        RootScrollViewer?.ScrollToVerticalOffset(0);

        Dispatcher.BeginInvoke(() =>
        {
            var refreshedViewer = FindScrollViewer(FindingsListView);
            refreshedViewer?.ScrollToVerticalOffset(0);
            RootScrollViewer?.ScrollToVerticalOffset(0);
        }, DispatcherPriority.Render);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _viewModel.PageChanged -= OnPageChanged;
        Unloaded -= OnUnloaded;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject? root)
    {
        if (root is null)
        {
            return null;
        }

        if (root is ScrollViewer viewer)
        {
            return viewer;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var result = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public void OnNavigatedTo()
    {
        // Re-subscribe to page changed events
        _viewModel.PageChanged -= OnPageChanged;
        _viewModel.PageChanged += OnPageChanged;
    }

    /// <inheritdoc />
    public void OnNavigatingFrom()
    {
        // No cleanup needed
    }
}
