using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TidyWindow.App.ViewModels;

namespace TidyWindow.App.Views;

public partial class InstallHubPage : Page
{
    private readonly InstallHubViewModel _viewModel;
    private bool _disposed;
    private bool _scrollHandlersAttached;
    private bool _sizeHandlerAttached;
    private Thickness _scrollViewerDefaultMargin;
    private readonly Thickness _scrollViewerCompactMargin = new(24);
    private readonly Thickness _scrollViewerStackedMargin = new(16, 24, 16, 24);
    private WrapPanel? _bundleItemsHost;
    private WrapPanel? _packageItemsHost;
    private bool _bundleLayoutScheduled;
    private bool _packageLayoutScheduled;
    private double _lastBundleItemWidth = double.NaN;
    private double _lastPackageItemWidth = double.NaN;
    private bool _isStackLayout;
    private bool _useCompactMargin;

    private const double CompactLayoutBreakpoint = 1180d;
    private const double StackedLayoutBreakpoint = 980d;
    private const double WidthChangeTolerance = 0.5d;
    private const double LayoutHysteresis = 48d;

    public InstallHubPage(InstallHubViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;

        _scrollViewerDefaultMargin = ContentScrollViewer.Margin;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        var loadTask = _viewModel.EnsureLoadedAsync();

        EnsureScrollHandlers();

        if (!_sizeHandlerAttached)
        {
            ContentScrollViewer.SizeChanged += ContentScrollViewer_SizeChanged;
            _sizeHandlerAttached = true;
        }

        await loadTask;

        ApplyResponsiveLayout(ContentScrollViewer.ActualWidth);
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        DetachScrollHandlers();

        if (_sizeHandlerAttached)
        {
            ContentScrollViewer.SizeChanged -= ContentScrollViewer_SizeChanged;
            _sizeHandlerAttached = false;
        }

        _viewModel.Dispose();
        _disposed = true;

        Loaded -= OnPageLoaded;
        Unloaded -= OnPageUnloaded;
    }

    private async void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            EnsureScrollHandlers();

            if (!_sizeHandlerAttached)
            {
                ContentScrollViewer.SizeChanged += ContentScrollViewer_SizeChanged;
                _sizeHandlerAttached = true;
            }

            if (!_viewModel.IsInitialized)
            {
                await _viewModel.EnsureLoadedAsync();
            }

            ApplyResponsiveLayout(ContentScrollViewer.ActualWidth);
        }
    }

    private void ContentScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            ApplyResponsiveLayout(e.NewSize.Width);
        }
    }

    private void EnsureScrollHandlers()
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

        BundlesList.PreviewMouseWheel -= OnNestedPreviewMouseWheel;
        PackagesList.PreviewMouseWheel -= OnNestedPreviewMouseWheel;
        OperationsList.PreviewMouseWheel -= OnNestedPreviewMouseWheel;
        _scrollHandlersAttached = false;
    }

    private static void OnNestedPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject dependencyObject)
        {
            return;
        }

        if (FindParentPage(dependencyObject) is not InstallHubPage page)
        {
            return;
        }

        page.RouteMouseWheel(e, dependencyObject);
    }

    private void RouteMouseWheel(MouseWheelEventArgs e, DependencyObject source)
    {
        if (ContentScrollViewer.ScrollableHeight <= 0)
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

        var targetOffset = ContentScrollViewer.VerticalOffset - e.Delta;
        if (targetOffset < 0)
        {
            targetOffset = 0;
        }
        else if (targetOffset > ContentScrollViewer.ScrollableHeight)
        {
            targetOffset = ContentScrollViewer.ScrollableHeight;
        }

        ContentScrollViewer.ScrollToVerticalOffset(targetOffset);
    }

    private static InstallHubPage? FindParentPage(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is InstallHubPage page)
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

    private void ApplyResponsiveLayout(double viewportWidth)
    {
        if (double.IsNaN(viewportWidth) || viewportWidth <= 0)
        {
            viewportWidth = ContentScrollViewer.ActualWidth;
        }

        if (double.IsNaN(viewportWidth) || viewportWidth <= 0)
        {
            return;
        }

        var stackLayout = _isStackLayout;
        if (viewportWidth <= StackedLayoutBreakpoint - LayoutHysteresis)
        {
            stackLayout = true;
        }
        else if (viewportWidth >= StackedLayoutBreakpoint + LayoutHysteresis)
        {
            stackLayout = false;
        }

        var compactMargins = _useCompactMargin;
        if (viewportWidth <= CompactLayoutBreakpoint - LayoutHysteresis)
        {
            compactMargins = true;
        }
        else if (viewportWidth >= CompactLayoutBreakpoint + LayoutHysteresis)
        {
            compactMargins = false;
        }

        _isStackLayout = stackLayout;
        _useCompactMargin = compactMargins;

        var targetMargin = stackLayout
            ? _scrollViewerStackedMargin
            : compactMargins
                ? _scrollViewerCompactMargin
                : _scrollViewerDefaultMargin;

        if (!ThicknessEquals(ContentScrollViewer.Margin, targetMargin))
        {
            ContentScrollViewer.Margin = targetMargin;
        }

        UpdateAdaptiveLists();
    }

    private void BundlesList_Loaded(object sender, RoutedEventArgs e)
    {
        _bundleItemsHost = FindItemsHost<WrapPanel>(BundlesList);
        UpdateBundlesLayout();
    }

    private void BundlesList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            UpdateBundlesLayout();
        }
    }

    private void PackagesList_Loaded(object sender, RoutedEventArgs e)
    {
        _packageItemsHost = FindItemsHost<WrapPanel>(PackagesList);
        UpdatePackagesLayout();
    }

    private void PackagesList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            UpdatePackagesLayout();
        }
    }

    private void UpdateAdaptiveLists()
    {
        UpdateBundlesLayout();
        UpdatePackagesLayout();
    }

    private void UpdateBundlesLayout()
    {
        if (!IsLoaded)
        {
            return;
        }

        _bundleItemsHost ??= FindItemsHost<WrapPanel>(BundlesList);
        if (_bundleItemsHost is null)
        {
            _lastBundleItemWidth = double.NaN;
            ScheduleBundleLayoutUpdate();
            return;
        }

        var availableWidth = GetScrollableContentWidth(BundlesList);
        if (availableWidth <= 0)
        {
            return;
        }

        var targetWidth = CalculateAdaptiveItemWidth(availableWidth, 216d, 280d, 22d, 3);
        if (!double.IsNaN(targetWidth) && ShouldUpdateWidth(ref _lastBundleItemWidth, targetWidth))
        {
            _bundleItemsHost.ItemWidth = targetWidth;
        }
    }

    private void UpdatePackagesLayout()
    {
        if (!IsLoaded)
        {
            return;
        }

        _packageItemsHost ??= FindItemsHost<WrapPanel>(PackagesList);
        if (_packageItemsHost is null)
        {
            _lastPackageItemWidth = double.NaN;
            SchedulePackageLayoutUpdate();
            return;
        }

        var availableWidth = GetScrollableContentWidth(PackagesList);
        if (availableWidth <= 0)
        {
            return;
        }

        var targetWidth = CalculateAdaptiveItemWidth(availableWidth, 236d, 360d, 22d, 3);
        if (!double.IsNaN(targetWidth) && ShouldUpdateWidth(ref _lastPackageItemWidth, targetWidth))
        {
            _packageItemsHost.ItemWidth = targetWidth;
        }
    }

    private void ScheduleBundleLayoutUpdate()
    {
        if (_bundleLayoutScheduled)
        {
            return;
        }

        _bundleLayoutScheduled = true;
        Dispatcher.InvokeAsync(() =>
        {
            _bundleLayoutScheduled = false;
            UpdateBundlesLayout();
        }, DispatcherPriority.Loaded);
    }

    private void SchedulePackageLayoutUpdate()
    {
        if (_packageLayoutScheduled)
        {
            return;
        }

        _packageLayoutScheduled = true;
        Dispatcher.InvokeAsync(() =>
        {
            _packageLayoutScheduled = false;
            UpdatePackagesLayout();
        }, DispatcherPriority.Loaded);
    }

    private static double CalculateAdaptiveItemWidth(double availableWidth, double minWidth, double maxWidth, double spacing, int maxColumns)
    {
        if (availableWidth <= 0)
        {
            return double.NaN;
        }

        var maxFeasibleColumns = Math.Min(maxColumns, Math.Max(1, (int)Math.Floor((availableWidth + spacing) / (minWidth + spacing))));

        for (var columns = maxFeasibleColumns; columns >= 1; columns--)
        {
            var width = (availableWidth - ((columns - 1) * spacing)) / columns;
            if (width >= minWidth || columns == 1)
            {
                return Math.Min(Math.Max(width, minWidth), maxWidth);
            }
        }

        return Math.Min(maxWidth, minWidth);
    }

    private void QueueOverlayBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        QueueDrawerToggle.IsChecked = false;
    }

    private static double GetScrollableContentWidth(ItemsControl control)
    {
        var padding = control.Padding;
        var width = control.ActualWidth - padding.Left - padding.Right;
        const double scrollbarAllowance = 18d;
        return Math.Max(0, width - scrollbarAllowance);
    }

    private static T? FindItemsHost<T>(ItemsControl control) where T : System.Windows.Controls.Panel
    {
        if (VisualTreeHelper.GetChildrenCount(control) == 0)
        {
            control.ApplyTemplate();
        }

        return FindVisualChild<T>(control);
    }

    private static bool ShouldUpdateWidth(ref double lastWidth, double nextWidth)
    {
        if (double.IsNaN(nextWidth))
        {
            return false;
        }

        if (double.IsNaN(lastWidth) || Math.Abs(lastWidth - nextWidth) > WidthChangeTolerance)
        {
            lastWidth = nextWidth;
            return true;
        }

        return false;
    }

    private static bool ThicknessEquals(Thickness left, Thickness right)
    {
        return Math.Abs(left.Left - right.Left) < 0.1
            && Math.Abs(left.Top - right.Top) < 0.1
            && Math.Abs(left.Right - right.Right) < 0.1
            && Math.Abs(left.Bottom - right.Bottom) < 0.1;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is null)
        {
            return null;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
            {
                return typedChild;
            }

            var descendent = FindVisualChild<T>(child);
            if (descendent is not null)
            {
                return descendent;
            }
        }

        return null;
    }
}
