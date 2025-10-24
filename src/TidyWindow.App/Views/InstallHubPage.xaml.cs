using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TidyWindow.App.ViewModels;

namespace TidyWindow.App.Views;

public partial class InstallHubPage : Page
{
    private readonly InstallHubViewModel _viewModel;
    private bool _disposed;
    private bool _scrollHandlersAttached;

    public InstallHubPage(InstallHubViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (_scrollHandlersAttached)
        {
            return;
        }

        AttachScrollHandler(BundlesList);
        AttachScrollHandler(PackagesList);
        AttachScrollHandler(OperationsList);
        _scrollHandlersAttached = true;
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        Loaded -= OnPageLoaded;
        Unloaded -= OnPageUnloaded;
        DetachScrollHandlers();
        _viewModel.Dispose();
        _disposed = true;
    }

    private void AttachScrollHandler(ItemsControl? control)
    {
        if (control is null)
        {
            return;
        }

        control.PreviewMouseWheel += OnNestedPreviewMouseWheel;
    }

    private void DetachScrollHandlers()
    {
        if (!_scrollHandlersAttached)
        {
            return;
        }

        BundlesList.PreviewMouseWheel -= OnNestedPreviewMouseWheel;
        PackagesList.PreviewMouseWheel -= OnNestedPreviewMouseWheel;
        OperationsList.PreviewMouseWheel -= OnNestedPreviewMouseWheel;
        _scrollHandlersAttached = false;
    }

    private void OnNestedPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (RootScrollViewer is null || RootScrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        if (sender is not DependencyObject dependencyObject)
        {
            return;
        }

        var nestedScrollViewer = FindChildScrollViewer(dependencyObject);
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

        var targetOffset = RootScrollViewer.VerticalOffset - e.Delta;
        if (targetOffset < 0)
        {
            targetOffset = 0;
        }
        else if (targetOffset > RootScrollViewer.ScrollableHeight)
        {
            targetOffset = RootScrollViewer.ScrollableHeight;
        }

        RootScrollViewer.ScrollToVerticalOffset(targetOffset);
    }

    private static ScrollViewer? FindChildScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer scrollViewer)
        {
            return scrollViewer;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var result = FindChildScrollViewer(child);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }
}
