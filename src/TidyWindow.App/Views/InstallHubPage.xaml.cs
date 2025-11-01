using System;
using System.Threading.Tasks;
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
    private bool _sizeHandlerAttached;
    private System.Windows.Controls.ListView? _packagesListView;
    private ScrollViewer? _packagesScrollViewer;
    private GridViewColumn? _packageColumn;
    private GridViewColumn? _managerColumn;
    private GridViewColumn? _adminColumn;
    private GridViewColumn? _statusColumn;
    private GridViewColumn? _actionsColumn;
    private Thickness _scrollViewerDefaultMargin;
    private readonly Thickness _scrollViewerCompactMargin = new(24);
    private readonly Thickness _scrollViewerStackedMargin = new(16, 24, 16, 24);
    private Thickness _secondaryColumnDefaultMargin;
    private readonly Thickness _secondaryColumnCompactMargin = new(12, 0, 0, 0);
    private readonly Thickness _secondaryColumnStackedMargin = new(0, 24, 0, 0);
    private double _primaryColumnDefaultMinWidth;
    private double _secondaryColumnDefaultMinWidth;
    private bool _isStackedLayout;

    private const double WideLayoutBreakpoint = 1320d;
    private const double CompactLayoutBreakpoint = 1180d;
    private const double StackedLayoutBreakpoint = 980d;
    private const double CompactPrimaryMinWidth = 320d;
    private const double CompactSecondaryMinWidth = 280d;
    private const double GridPaddingWidth = 56d;

    private const double PackagePreferredWidth = 280d;
    private const double PackageCompactWidth = 240d;
    private const double PackageMinimumWidth = 200d;

    private const double ManagerPreferredWidth = 120d;
    private const double ManagerCompactWidth = 100d;
    private const double ManagerMinimumWidth = 88d;

    private const double AdminPreferredWidth = 80d;
    private const double AdminCompactWidth = 68d;
    private const double AdminMinimumWidth = 60d;

    private const double StatusPreferredWidth = 220d;
    private const double StatusCompactWidth = 180d;
    private const double StatusMinimumWidth = 140d;

    private const double ActionsPreferredWidth = 140d;
    private const double ActionsCompactWidth = 120d;
    private const double ActionsMinimumWidth = 108d;

    public InstallHubPage(InstallHubViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;

        _scrollViewerDefaultMargin = ContentScrollViewer.Margin;
        _secondaryColumnDefaultMargin = SecondaryColumnHost.Margin;
        _primaryColumnDefaultMinWidth = PrimaryColumnDefinition.MinWidth;
        _secondaryColumnDefaultMinWidth = SecondaryColumnDefinition.MinWidth;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        var loadTask = _viewModel.EnsureLoadedAsync();

        EnsureScrollHandlers();

        PackagesList.Loaded -= PackagesList_OnLoaded;
        PackagesList.Loaded += PackagesList_OnLoaded;
        PackagesList.SizeChanged -= PackagesList_OnSizeChanged;
        PackagesList.SizeChanged += PackagesList_OnSizeChanged;

        if (!_sizeHandlerAttached)
        {
            ContentScrollViewer.SizeChanged += ContentScrollViewer_SizeChanged;
            _sizeHandlerAttached = true;
        }

        await loadTask;

        ApplyResponsiveLayout(ContentScrollViewer.ActualWidth);
        UpdatePackageColumnWidths();
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

        PackagesList.Loaded -= PackagesList_OnLoaded;
        PackagesList.SizeChanged -= PackagesList_OnSizeChanged;

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
            UpdatePackageColumnWidths();
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

    private void PackagesList_OnLoaded(object sender, RoutedEventArgs e)
    {
        _packagesListView ??= sender as System.Windows.Controls.ListView;
        if (_packagesListView is null)
        {
            return;
        }

        _packagesScrollViewer ??= FindChildScrollViewer(_packagesListView);
        CachePackageColumns();
        UpdatePackageColumnWidths();
    }

    private void PackagesList_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            UpdatePackageColumnWidths();
        }
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

        var stackLayout = viewportWidth < StackedLayoutBreakpoint;
        var balancedColumns = viewportWidth < WideLayoutBreakpoint;
        var tightMargins = viewportWidth < CompactLayoutBreakpoint;

        if (stackLayout)
        {
            if (!_isStackedLayout)
            {
                Grid.SetRow(SecondaryColumnHost, 1);
                Grid.SetColumn(SecondaryColumnHost, 0);
                SecondaryColumnHost.Margin = _secondaryColumnStackedMargin;
                _isStackedLayout = true;
            }

            PrimaryColumnDefinition.Width = new GridLength(1, GridUnitType.Star);
            SecondaryColumnDefinition.Width = new GridLength(0, GridUnitType.Pixel);
            PrimaryColumnDefinition.MinWidth = 0;
            SecondaryColumnDefinition.MinWidth = 0;
        }
        else
        {
            if (_isStackedLayout)
            {
                Grid.SetRow(SecondaryColumnHost, 0);
                Grid.SetColumn(SecondaryColumnHost, 1);
                SecondaryColumnHost.Margin = _secondaryColumnDefaultMargin;
                _isStackedLayout = false;
            }

            PrimaryColumnDefinition.Width = balancedColumns
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(3, GridUnitType.Star);
            SecondaryColumnDefinition.Width = balancedColumns
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(2, GridUnitType.Star);

            PrimaryColumnDefinition.MinWidth = tightMargins ? CompactPrimaryMinWidth : _primaryColumnDefaultMinWidth;
            SecondaryColumnDefinition.MinWidth = tightMargins ? CompactSecondaryMinWidth : _secondaryColumnDefaultMinWidth;
            SecondaryColumnHost.Margin = tightMargins ? _secondaryColumnCompactMargin : _secondaryColumnDefaultMargin;
        }

        ContentScrollViewer.Margin = stackLayout
            ? _scrollViewerStackedMargin
            : (tightMargins || balancedColumns)
                ? _scrollViewerCompactMargin
                : _scrollViewerDefaultMargin;

        UpdatePackageColumnWidths();
    }

    private void CachePackageColumns()
    {
        if (_packagesListView?.View is not GridView gridView)
        {
            return;
        }

        if (gridView.Columns.Count < 5)
        {
            return;
        }

        _packageColumn = gridView.Columns[0];
        _managerColumn = gridView.Columns[1];
        _adminColumn = gridView.Columns[2];
        _statusColumn = gridView.Columns[3];
        _actionsColumn = gridView.Columns[4];
    }

    private void UpdatePackageColumnWidths()
    {
        if (_packagesListView is null || _packageColumn is null)
        {
            return;
        }

        var availableWidth = _packagesListView.ActualWidth;
        if (double.IsNaN(availableWidth) || availableWidth <= 0)
        {
            return;
        }

        var workingWidth = Math.Max(0d, availableWidth - GridPaddingWidth);

        if (_packagesScrollViewer?.ComputedVerticalScrollBarVisibility == Visibility.Visible)
        {
            workingWidth = Math.Max(0d, workingWidth - SystemParameters.VerticalScrollBarWidth);
        }

        var packageWidth = PackagePreferredWidth;
        var managerWidth = ManagerPreferredWidth;
        var adminWidth = AdminPreferredWidth;
        var statusWidth = StatusPreferredWidth;
        var actionsWidth = ActionsPreferredWidth;

        var preferredTotal = packageWidth + managerWidth + adminWidth + statusWidth + actionsWidth;

        if (workingWidth > preferredTotal)
        {
            packageWidth += workingWidth - preferredTotal;
        }
        else
        {
            var overflow = preferredTotal - workingWidth;

            packageWidth = ReduceWidth(packageWidth, PackageCompactWidth, ref overflow);
            statusWidth = ReduceWidth(statusWidth, StatusCompactWidth, ref overflow);
            actionsWidth = ReduceWidth(actionsWidth, ActionsCompactWidth, ref overflow);
            managerWidth = ReduceWidth(managerWidth, ManagerCompactWidth, ref overflow);
            adminWidth = ReduceWidth(adminWidth, AdminCompactWidth, ref overflow);

            if (overflow > 0)
            {
                packageWidth = ReduceWidth(packageWidth, PackageMinimumWidth, ref overflow);
                statusWidth = ReduceWidth(statusWidth, StatusMinimumWidth, ref overflow);
                actionsWidth = ReduceWidth(actionsWidth, ActionsMinimumWidth, ref overflow);
                managerWidth = ReduceWidth(managerWidth, ManagerMinimumWidth, ref overflow);
                adminWidth = ReduceWidth(adminWidth, AdminMinimumWidth, ref overflow);
            }
        }

        _packageColumn.Width = packageWidth;
        if (_managerColumn is not null)
        {
            _managerColumn.Width = managerWidth;
        }

        if (_adminColumn is not null)
        {
            _adminColumn.Width = adminWidth;
        }

        if (_statusColumn is not null)
        {
            _statusColumn.Width = statusWidth;
        }

        if (_actionsColumn is not null)
        {
            _actionsColumn.Width = actionsWidth;
        }
    }

    private static double ReduceWidth(double current, double target, ref double overflow)
    {
        if (current <= target || overflow <= 0)
        {
            return current;
        }

        var delta = Math.Min(current - target, overflow);
        overflow -= delta;
        return current - delta;
    }
}
