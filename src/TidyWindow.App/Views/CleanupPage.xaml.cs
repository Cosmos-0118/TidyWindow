using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TidyWindow.App.ViewModels;
using WpfApplication = System.Windows.Application;

namespace TidyWindow.App.Views;

public partial class CleanupPage : Page
{
    private readonly CleanupViewModel _viewModel;
    private bool _disposed;
    private bool _scrollHandlersAttached;

    public CleanupPage(CleanupViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        _viewModel.ConfirmElevation = ConfirmElevation;
        _viewModel.AdministratorRestartRequested += OnAdministratorRestartRequested;
        Unloaded += CleanupPage_Unloaded;
        IsVisibleChanged += OnIsVisibleChanged;
        Loaded += OnPageLoaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnPageLoaded;
        EnsureScrollHandlers();
    }

    private bool ConfirmElevation(string message)
    {
        var warning = string.IsNullOrWhiteSpace(message)
            ? "These items may be protected. Restart as administrator to continue?"
            : message;

        var result = System.Windows.MessageBox.Show(warning, "Administrator privileges needed", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes);
        return result == MessageBoxResult.Yes;
    }

    private void OnAdministratorRestartRequested(object? sender, EventArgs e)
    {
        var app = WpfApplication.Current;
        if (app is null)
        {
            return;
        }

        app.Dispatcher.BeginInvoke(new Action(app.Shutdown));
    }

    private void CleanupPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _viewModel.AdministratorRestartRequested -= OnAdministratorRestartRequested;
        _viewModel.ConfirmElevation = null;
        Unloaded -= CleanupPage_Unloaded;
        Loaded -= OnPageLoaded;
        DetachScrollHandlers();
        _disposed = true;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible && _disposed)
        {
            RestoreViewModelBindings();
        }
    }

    private void RestoreViewModelBindings()
    {
        _viewModel.ConfirmElevation = ConfirmElevation;
        _viewModel.AdministratorRestartRequested += OnAdministratorRestartRequested;
        Unloaded += CleanupPage_Unloaded;
        Loaded += OnPageLoaded;
        _disposed = false;
        EnsureScrollHandlers();
    }

    private void EnsureScrollHandlers()
    {
        if (_scrollHandlersAttached)
        {
            return;
        }

        AttachScrollHandler(CategoryListView);
        AttachScrollHandler(ItemListView);
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

        CategoryListView.PreviewMouseWheel -= OnNestedPreviewMouseWheel;
        ItemListView.PreviewMouseWheel -= OnNestedPreviewMouseWheel;
        _scrollHandlersAttached = false;
    }

    private void BubbleScroll(MouseWheelEventArgs e, DependencyObject source)
    {
        if (RootScrollViewer is null || RootScrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        var nestedScrollViewer = FindChildScrollViewer(source);
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

    private static void OnNestedPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject dependencyObject)
        {
            return;
        }

        if (FindParentPage(dependencyObject) is CleanupPage page)
        {
            page.BubbleScroll(e, dependencyObject);
        }
    }

    private static CleanupPage? FindParentPage(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is CleanupPage page)
            {
                return page;
            }

            node = VisualTreeHelper.GetParent(node) ?? (node as FrameworkElement)?.Parent;
        }

        return null;
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
