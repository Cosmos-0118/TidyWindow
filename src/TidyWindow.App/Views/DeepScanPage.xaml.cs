using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.ViewModels;
using Forms = System.Windows.Forms;
using System.Windows.Media;
using WpfListView = System.Windows.Controls.ListView;

namespace TidyWindow.App.Views;

public partial class DeepScanPage : Page
{
    private readonly DeepScanViewModel _viewModel;
    private WpfListView? _findingsListView;
    private ScrollViewer? _rootScrollViewer;
    private bool _disposed;
    private bool _scrollHandlersAttached;

    public DeepScanPage(DeepScanViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        Loaded += Page_OnLoaded;
        Unloaded += OnPageUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private async void Page_OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= Page_OnLoaded;
        EnsureScrollHandlers();

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

        var commandProperty = _viewModel.GetType().GetProperty("DeleteFindingCommand");
        var deleteCommand = commandProperty?.GetValue(_viewModel);

        if (deleteCommand is IAsyncRelayCommand<DeepScanItemViewModel?> asyncCommandWithParam)
        {
            await asyncCommandWithParam.ExecuteAsync(item);
        }
        else if (deleteCommand is IAsyncRelayCommand asyncCommand)
        {
            await asyncCommand.ExecuteAsync(item);
        }
        else if (deleteCommand is IRelayCommand relayCommand && relayCommand.CanExecute(item))
        {
            relayCommand.Execute(item);
        }

        e.Handled = true;
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (_findingsListView is not null)
        {
            _findingsListView.Loaded -= FindingsListView_Loaded;
        }

        DetachScrollHandlers();

        Unloaded -= OnPageUnloaded;
        _disposed = true;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible && _disposed)
        {
            RestorePageBindings();
        }
    }

    private void RestorePageBindings()
    {
        if (_findingsListView is not null)
        {
            _findingsListView.Loaded += FindingsListView_Loaded;
        }

        Unloaded += OnPageUnloaded;
        _disposed = false;
        EnsureScrollHandlers();
    }

    private void FindingsListView_Loaded(object sender, RoutedEventArgs e)
    {
        _findingsListView ??= sender as WpfListView;
        if (_findingsListView is null)
        {
            return;
        }

        EnsureScrollHandlers();
    }

    private void EnsureScrollHandlers()
    {
        if (_scrollHandlersAttached)
        {
            return;
        }

        AttachScrollHandler(_findingsListView ?? GetFindingsListView());
        _scrollHandlersAttached = true;
    }

    private static void AttachScrollHandler(ItemsControl? control)
    {
        if (control is null)
        {
            return;
        }

        control.PreviewMouseWheel -= OnNestedPreviewMouseWheel;
        control.PreviewMouseWheel += OnNestedPreviewMouseWheel;
    }

    private void DetachScrollHandlers()
    {
        if (!_scrollHandlersAttached)
        {
            return;
        }

        DetachScrollHandler(_findingsListView ?? GetFindingsListView());
        _scrollHandlersAttached = false;
    }

    private static void DetachScrollHandler(ItemsControl? control)
    {
        if (control is null)
        {
            return;
        }

        control.PreviewMouseWheel -= OnNestedPreviewMouseWheel;
    }

    private void BubbleScroll(MouseWheelEventArgs e, DependencyObject source)
    {
        var rootScrollViewer = GetRootScrollViewer();
        if (rootScrollViewer is null || rootScrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        var nestedScrollViewer = FindDescendant<ScrollViewer>(source);
        if (nestedScrollViewer is null)
        {
            return;
        }

        var canScrollUp = e.Delta > 0 && nestedScrollViewer.VerticalOffset > 0;
        var canScrollDown = e.Delta < 0 && nestedScrollViewer.VerticalOffset < nestedScrollViewer.ScrollableHeight;

        if (canScrollUp || canScrollDown)
        {
            return;
        }

        e.Handled = true;

        var targetOffset = rootScrollViewer.VerticalOffset - e.Delta;
        if (targetOffset < 0)
        {
            targetOffset = 0;
        }
        else if (targetOffset > rootScrollViewer.ScrollableHeight)
        {
            targetOffset = rootScrollViewer.ScrollableHeight;
        }

        rootScrollViewer.ScrollToVerticalOffset(targetOffset);
    }

    private static void OnNestedPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject dependencyObject)
        {
            return;
        }

        if (FindParentPage(dependencyObject) is DeepScanPage page)
        {
            page.BubbleScroll(e, dependencyObject);
        }
    }

    private static DeepScanPage? FindParentPage(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is DeepScanPage page)
            {
                return page;
            }

            node = VisualTreeHelper.GetParent(node) ?? (node as FrameworkElement)?.Parent;
        }

        return null;
    }

    private static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null)
        {
            return null;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private WpfListView? GetFindingsListView()
    {
        _findingsListView ??= FindName("FindingsListView") as WpfListView;
        return _findingsListView;
    }

    private ScrollViewer? GetRootScrollViewer()
    {
        _rootScrollViewer ??= FindName("RootScrollViewer") as ScrollViewer;
        return _rootScrollViewer;
    }
}
