using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.ViewModels;
using System.Windows.Media;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using MessageBox = System.Windows.MessageBox;
using WpfApplication = System.Windows.Application;
using WpfListView = System.Windows.Controls.ListView;

namespace TidyWindow.App.Views;

public partial class PackageMaintenancePage : Page
{
    private readonly PackageMaintenanceViewModel _viewModel;
    private WpfListView? _packagesListView;
    private ScrollViewer? _packagesScrollViewer;
    private GridViewColumn? _packageColumn;
    private GridViewColumn? _managerColumn;
    private GridViewColumn? _sourceColumn;
    private GridViewColumn? _versionColumn;
    private GridViewColumn? _statusColumn;
    private GridViewColumn? _targetVersionColumn;
    private GridViewColumn? _actionsColumn;
    private bool _disposed;

    // Column width breakpoints keep the maintenance table legible across window sizes.
    private const double PackagePreferredWidth = 260d;
    private const double PackageCompactWidth = 220d;
    private const double PackageMinimumWidth = 190d;

    private const double ManagerPreferredWidth = 80d;
    private const double ManagerCompactWidth = 70d;

    private const double SourcePreferredWidth = 80d;
    private const double SourceCompactWidth = 70d;

    private const double VersionPreferredWidth = 86d;
    private const double VersionCompactWidth = 74d;

    private const double StatusPreferredWidth = 140d;
    private const double StatusCompactWidth = 110d;

    private const double TargetPreferredWidth = 104d;
    private const double TargetCompactWidth = 90d;

    private const double ActionsPreferredWidth = 160d;
    private const double ActionsCompactWidth = 140d;

    private const double CompactPrimaryMinWidth = 320d;
    private const double CompactSecondaryMinWidth = 280d;

    private const double LayoutPadding = 56d;

    private const double WideLayoutBreakpoint = 1280d;
    private const double CompactLayoutBreakpoint = 1120d;
    private const double StackedLayoutBreakpoint = 960d;

    private bool _isStackedLayout;
    private Thickness _secondaryColumnDefaultMargin;
    private readonly Thickness _secondaryColumnCompactMargin = new(12, 0, 0, 0);
    private readonly Thickness _secondaryColumnStackedMargin = new Thickness(0, 24, 0, 0);
    private Thickness _scrollViewerDefaultMargin;
    private readonly Thickness _scrollViewerCompactMargin = new Thickness(24);
    private readonly Thickness _scrollViewerStackedMargin = new Thickness(16, 24, 16, 24);
    private double _primaryColumnDefaultMinWidth;
    private double _secondaryColumnDefaultMinWidth;
    private bool _isHeaderStacked;
    private GridLength _headerSecondaryDefaultWidth;
    private readonly Thickness _headerActionStackedMargin = new Thickness(0, 16, 0, 0);
    private Thickness _headerActionDefaultMargin;
    private HorizontalAlignment _headerActionDefaultAlignment;
    private VerticalAlignment _headerActionDefaultVerticalAlignment;
    private HorizontalAlignment _headerActionPanelDefaultAlignment;
    private HorizontalAlignment _headerLastRefreshedDefaultAlignment;

    public PackageMaintenancePage(PackageMaintenanceViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;

        _secondaryColumnDefaultMargin = SecondaryColumnHost.Margin;
        _scrollViewerDefaultMargin = ContentScrollViewer.Margin;
        _primaryColumnDefaultMinWidth = PrimaryColumnDefinition.MinWidth;
        _secondaryColumnDefaultMinWidth = SecondaryColumnDefinition.MinWidth;
        _headerSecondaryDefaultWidth = HeaderSecondaryColumn.Width;
        _headerActionDefaultMargin = HeaderActionHost.Margin;
        _headerActionDefaultAlignment = HeaderActionHost.HorizontalAlignment;
        _headerActionDefaultVerticalAlignment = HeaderActionHost.VerticalAlignment;
        _headerActionPanelDefaultAlignment = HeaderActionPanel.HorizontalAlignment;
        _headerLastRefreshedDefaultAlignment = HeaderLastRefreshedText.HorizontalAlignment;

        _viewModel.ConfirmElevation = ConfirmElevation;
        _viewModel.AdministratorRestartRequested += OnAdministratorRestartRequested;
        Unloaded += OnPageUnloaded;
        Loaded += OnPageLoaded;
        IsVisibleChanged += OnIsVisibleChanged;
        ContentScrollViewer.SizeChanged -= ContentScrollViewer_SizeChanged;
        ContentScrollViewer.SizeChanged += ContentScrollViewer_SizeChanged;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnPageLoaded;
        EnsureScrollHandlers();
        UpdateResponsiveLayout(ContentScrollViewer.ActualWidth);
        if (_viewModel.HasLoadedInitialData)
            return;
        if (_viewModel.RefreshCommand is IAsyncRelayCommand asyncCommand)
        {
            await asyncCommand.ExecuteAsync(null);
        }
        else
        {
            _viewModel.RefreshCommand.Execute(null);
        }
    }

    private bool ConfirmElevation(string message)
    {
        var prompt = string.IsNullOrWhiteSpace(message)
            ? "This operation requires administrator privileges. Restart as administrator to continue?"
            : message;

        var result = MessageBox.Show(prompt,
            "Administrator privileges needed",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.Yes);

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

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _viewModel.AdministratorRestartRequested -= OnAdministratorRestartRequested;
        _viewModel.ConfirmElevation = null;
        DetachScrollHandlers();
        if (_packagesListView is not null)
        {
            _packagesListView.Loaded -= PackagesListView_Loaded;
            _packagesListView.SizeChanged -= PackagesListView_SizeChanged;
        }
        ContentScrollViewer.SizeChanged -= ContentScrollViewer_SizeChanged;
        // Do not dispose the viewmodel to preserve state between navigations
        Unloaded -= OnPageUnloaded;
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
        if (_packagesListView is not null)
        {
            _packagesListView.Loaded += PackagesListView_Loaded;
            _packagesListView.SizeChanged += PackagesListView_SizeChanged;
        }
        ContentScrollViewer.SizeChanged += ContentScrollViewer_SizeChanged;

        Unloaded += OnPageUnloaded;
        _disposed = false;
        EnsureScrollHandlers();
        UpdateResponsiveLayout(ContentScrollViewer.ActualWidth);
    }

    private void PackagesListView_Loaded(object sender, RoutedEventArgs e)
    {
        _packagesListView ??= sender as WpfListView;
        if (_packagesListView is null)
        {
            return;
        }

        _packagesScrollViewer ??= FindDescendant<ScrollViewer>(_packagesListView);
        CacheColumns();
        UpdateColumnWidths();
        EnsureScrollHandlers();
    }

    private void PackagesListView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            UpdateColumnWidths();
        }
    }

    private void ContentScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            UpdateResponsiveLayout(e.NewSize.Width);
        }
    }

    private void UpdateColumnWidths()
    {
        if (_packagesListView is null)
        {
            return;
        }

        if (_packageColumn is null)
        {
            return;
        }

        var availableWidth = _packagesListView.ActualWidth;
        if (double.IsNaN(availableWidth) || availableWidth <= 0)
        {
            return;
        }

        var workingWidth = Math.Max(0d, availableWidth - LayoutPadding);

        if (_packagesScrollViewer?.ComputedVerticalScrollBarVisibility == Visibility.Visible)
        {
            workingWidth = Math.Max(0d, workingWidth - SystemParameters.VerticalScrollBarWidth);
        }

        var packageWidth = PackagePreferredWidth;
        var managerWidth = ManagerPreferredWidth;
        var sourceWidth = SourcePreferredWidth;
        var versionWidth = VersionPreferredWidth;
        var statusWidth = StatusPreferredWidth;
        var targetWidth = TargetPreferredWidth;
        var actionsWidth = ActionsPreferredWidth;

        var preferredTotal = packageWidth + managerWidth + sourceWidth + versionWidth + statusWidth + targetWidth + actionsWidth;

        if (workingWidth > preferredTotal)
        {
            packageWidth += workingWidth - preferredTotal;
        }
        else
        {
            var overflow = preferredTotal - workingWidth;

            packageWidth = ReduceWidth(packageWidth, PackageCompactWidth, ref overflow);
            actionsWidth = ReduceWidth(actionsWidth, ActionsCompactWidth, ref overflow);
            statusWidth = ReduceWidth(statusWidth, StatusCompactWidth, ref overflow);
            managerWidth = ReduceWidth(managerWidth, ManagerCompactWidth, ref overflow);
            sourceWidth = ReduceWidth(sourceWidth, SourceCompactWidth, ref overflow);
            versionWidth = ReduceWidth(versionWidth, VersionCompactWidth, ref overflow);
            targetWidth = ReduceWidth(targetWidth, TargetCompactWidth, ref overflow);

            if (overflow > 0)
            {
                packageWidth = ReduceWidth(packageWidth, PackageMinimumWidth, ref overflow);
            }
        }

        _packageColumn.Width = packageWidth;
        if (_managerColumn is not null)
        {
            _managerColumn.Width = managerWidth;
        }

        if (_sourceColumn is not null)
        {
            _sourceColumn.Width = sourceWidth;
        }

        if (_versionColumn is not null)
        {
            _versionColumn.Width = versionWidth;
        }

        if (_statusColumn is not null)
        {
            _statusColumn.Width = statusWidth;
        }

        if (_targetVersionColumn is not null)
        {
            _targetVersionColumn.Width = targetWidth;
        }

        if (_actionsColumn is not null)
        {
            _actionsColumn.Width = actionsWidth;
        }
    }

    private void UpdateResponsiveLayout(double viewportWidth)
    {
        if (double.IsNaN(viewportWidth) || viewportWidth <= 0d)
        {
            return;
        }

        // Shift between wide, compact, and stacked layouts so the page stays usable on smaller screens.
        var stackLayout = viewportWidth < StackedLayoutBreakpoint;
        var compactColumns = viewportWidth < WideLayoutBreakpoint;
        var tightMargins = viewportWidth < CompactLayoutBreakpoint;

        UpdateHeaderLayout(stackLayout, compactColumns);

        if (stackLayout)
        {
            if (!_isStackedLayout)
            {
                Grid.SetRow(SecondaryColumnHost, 1);
                Grid.SetColumn(SecondaryColumnHost, 0);
                SecondaryColumnHost.Margin = _secondaryColumnStackedMargin;
                PrimaryColumnDefinition.MinWidth = 0d;
                SecondaryColumnDefinition.MinWidth = 0d;
                _isStackedLayout = true;
            }

            SecondaryColumnDefinition.Width = new GridLength(0d, GridUnitType.Pixel);
            PrimaryColumnDefinition.Width = new GridLength(1d, GridUnitType.Star);
        }
        else
        {
            if (_isStackedLayout)
            {
                Grid.SetRow(SecondaryColumnHost, 0);
                Grid.SetColumn(SecondaryColumnHost, 1);
                SecondaryColumnHost.Margin = _secondaryColumnDefaultMargin;
                PrimaryColumnDefinition.MinWidth = _primaryColumnDefaultMinWidth;
                SecondaryColumnDefinition.MinWidth = _secondaryColumnDefaultMinWidth;
                _isStackedLayout = false;
            }

            var targetPrimary = compactColumns
                ? new GridLength(1d, GridUnitType.Star)
                : new GridLength(3d, GridUnitType.Star);
            var targetSecondary = compactColumns
                ? new GridLength(1d, GridUnitType.Star)
                : new GridLength(2d, GridUnitType.Star);

            if (!PrimaryColumnDefinition.Width.Equals(targetPrimary))
            {
                PrimaryColumnDefinition.Width = targetPrimary;
            }

            if (!SecondaryColumnDefinition.Width.Equals(targetSecondary))
            {
                SecondaryColumnDefinition.Width = targetSecondary;
            }

            var desiredPrimaryMin = tightMargins ? CompactPrimaryMinWidth : _primaryColumnDefaultMinWidth;
            if (!PrimaryColumnDefinition.MinWidth.Equals(desiredPrimaryMin))
            {
                PrimaryColumnDefinition.MinWidth = desiredPrimaryMin;
            }

            var desiredSecondaryMin = tightMargins ? CompactSecondaryMinWidth : _secondaryColumnDefaultMinWidth;
            if (!SecondaryColumnDefinition.MinWidth.Equals(desiredSecondaryMin))
            {
                SecondaryColumnDefinition.MinWidth = desiredSecondaryMin;
            }

            var desiredSecondaryMargin = tightMargins ? _secondaryColumnCompactMargin : _secondaryColumnDefaultMargin;
            if (!SecondaryColumnHost.Margin.Equals(desiredSecondaryMargin))
            {
                SecondaryColumnHost.Margin = desiredSecondaryMargin;
            }

        }

        ContentScrollViewer.Margin = stackLayout
            ? _scrollViewerStackedMargin
            : tightMargins || compactColumns
                ? _scrollViewerCompactMargin
                : _scrollViewerDefaultMargin;

        UpdateColumnWidths();
    }

    private void UpdateHeaderLayout(bool stackLayout, bool compactColumns)
    {
        var shouldStack = stackLayout || compactColumns;

        if (shouldStack)
        {
            if (_isHeaderStacked)
            {
                return;
            }

            Grid.SetRow(HeaderActionHost, 1);
            Grid.SetColumn(HeaderActionHost, 0);
            HeaderActionHost.Margin = _headerActionStackedMargin;
            HeaderActionHost.HorizontalAlignment = HorizontalAlignment.Stretch;
            HeaderActionHost.VerticalAlignment = VerticalAlignment.Top;
            HeaderActionPanel.HorizontalAlignment = HorizontalAlignment.Left;
            HeaderLastRefreshedText.HorizontalAlignment = HorizontalAlignment.Left;
            HeaderSecondaryColumn.Width = new GridLength(0d, GridUnitType.Pixel);
            _isHeaderStacked = true;
            return;
        }

        if (!_isHeaderStacked)
        {
            return;
        }

        Grid.SetRow(HeaderActionHost, 0);
        Grid.SetColumn(HeaderActionHost, 1);
        HeaderActionHost.Margin = _headerActionDefaultMargin;
        HeaderActionHost.HorizontalAlignment = _headerActionDefaultAlignment;
        HeaderActionHost.VerticalAlignment = _headerActionDefaultVerticalAlignment;
        HeaderActionPanel.HorizontalAlignment = _headerActionPanelDefaultAlignment;
        HeaderLastRefreshedText.HorizontalAlignment = _headerLastRefreshedDefaultAlignment;
        HeaderSecondaryColumn.Width = _headerSecondaryDefaultWidth;
        _isHeaderStacked = false;
    }

    private void EnsureScrollHandlers()
    {
        AttachScrollHandler(_packagesListView ?? PackagesListView);
        AttachScrollHandler(OperationsListView);
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
        DetachScrollHandler(_packagesListView ?? PackagesListView);
        DetachScrollHandler(OperationsListView);
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
        if (ContentScrollViewer.ScrollableHeight <= 0)
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

    private static void OnNestedPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject dependencyObject)
        {
            return;
        }

        if (FindParentPage(dependencyObject) is PackageMaintenancePage page)
        {
            page.BubbleScroll(e, dependencyObject);
        }
    }

    private static PackageMaintenancePage? FindParentPage(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is PackageMaintenancePage page)
            {
                return page;
            }

            node = VisualTreeHelper.GetParent(node) ?? (node as FrameworkElement)?.Parent;
        }

        return null;
    }

    private void CacheColumns()
    {
        if (_packagesListView?.View is not GridView gridView)
        {
            return;
        }

        // Column ordering is fixed in XAML; caching avoids walking the visual tree every resize.
        if (gridView.Columns.Count >= 7)
        {
            _packageColumn = gridView.Columns[0];
            _managerColumn = gridView.Columns[1];
            _sourceColumn = gridView.Columns[2];
            _versionColumn = gridView.Columns[3];
            _statusColumn = gridView.Columns[4];
            _targetVersionColumn = gridView.Columns[5];
            _actionsColumn = gridView.Columns[6];
        }
    }

    private static double ReduceWidth(double current, double minimum, ref double overflow)
    {
        if (overflow <= 0d)
        {
            return current;
        }

        var reducible = current - minimum;
        if (reducible <= 0d)
        {
            return current;
        }

        var reduction = Math.Min(reducible, overflow);
        overflow -= reduction;
        return current - reduction;
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
}
