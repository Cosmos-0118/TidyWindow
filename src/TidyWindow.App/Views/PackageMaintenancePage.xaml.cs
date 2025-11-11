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
    private bool _disposed;

    private const double CompactPrimaryMinWidth = 320d;
    private const double CompactSecondaryMinWidth = 280d;

    private const double WideLayoutBreakpoint = 1280d;
    private const double CompactLayoutBreakpoint = 1120d;
    private const double StackedLayoutBreakpoint = 960d;
    private const double MarginTighteningBuffer = 160d;
    private const double ScreenMaxWidthRatio = 0.92d;
    private const double ColumnShareBias = 0.55d;

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
    private double _columnSpacing;
    private readonly double _pageContentDefaultMaxWidth;

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
        _columnSpacing = SecondaryColumnHost.Margin.Left > 0
            ? SecondaryColumnHost.Margin.Left
            : PrimaryColumnHost.Margin.Right > 0 ? PrimaryColumnHost.Margin.Right : 24d;
        _pageContentDefaultMaxWidth = double.IsNaN(PageContentGrid.MaxWidth) || PageContentGrid.MaxWidth <= 0
            ? double.PositiveInfinity
            : PageContentGrid.MaxWidth;

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
        EnsureScrollHandlers();
    }

    private void ContentScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            UpdateResponsiveLayout(e.NewSize.Width);
        }
    }

    private void UpdateResponsiveLayout(double viewportWidth)
    {
        if (double.IsNaN(viewportWidth) || viewportWidth <= 0d)
        {
            return;
        }

        var screenWidth = SystemParameters.WorkArea.Width;
        if (double.IsNaN(screenWidth) || screenWidth <= 0)
        {
            screenWidth = viewportWidth;
        }

        var defaultMaxWidth = double.IsPositiveInfinity(_pageContentDefaultMaxWidth)
            ? screenWidth * ScreenMaxWidthRatio
            : Math.Min(_pageContentDefaultMaxWidth, screenWidth * ScreenMaxWidthRatio);

        var targetWidth = Math.Min(viewportWidth, defaultMaxWidth);
        if (targetWidth <= 0)
        {
            targetWidth = viewportWidth;
        }

        var compactColumns = viewportWidth < WideLayoutBreakpoint
                              || targetWidth < (_primaryColumnDefaultMinWidth + _secondaryColumnDefaultMinWidth + _columnSpacing + MarginTighteningBuffer);

        var desiredPrimaryMin = compactColumns ? CompactPrimaryMinWidth : _primaryColumnDefaultMinWidth;
        var desiredSecondaryMin = compactColumns ? CompactSecondaryMinWidth : _secondaryColumnDefaultMinWidth;

        var totalMinimum = desiredPrimaryMin + desiredSecondaryMin + _columnSpacing;
        var stackLayout = viewportWidth < StackedLayoutBreakpoint || targetWidth < totalMinimum;
        var tightMargins = stackLayout
                           || viewportWidth < CompactLayoutBreakpoint
                           || targetWidth < totalMinimum + MarginTighteningBuffer;

        UpdateHeaderLayout(stackLayout, compactColumns);

        var desiredSecondaryMargin = stackLayout
            ? _secondaryColumnStackedMargin
            : tightMargins
                ? _secondaryColumnCompactMargin
                : _secondaryColumnDefaultMargin;

        if (!SecondaryColumnHost.Margin.Equals(desiredSecondaryMargin))
        {
            SecondaryColumnHost.Margin = desiredSecondaryMargin;
        }

        var desiredScrollMargin = stackLayout
            ? _scrollViewerStackedMargin
            : (tightMargins || compactColumns)
                ? _scrollViewerCompactMargin
                : _scrollViewerDefaultMargin;

        if (!ContentScrollViewer.Margin.Equals(desiredScrollMargin))
        {
            ContentScrollViewer.Margin = desiredScrollMargin;
        }

        var maxWidth = Math.Max(totalMinimum, defaultMaxWidth);

        if (stackLayout)
        {
            if (!_isStackedLayout)
            {
                Grid.SetRow(SecondaryColumnHost, 1);
                Grid.SetColumn(SecondaryColumnHost, 0);
                PrimaryColumnDefinition.MinWidth = 0d;
                SecondaryColumnDefinition.MinWidth = 0d;
                _isStackedLayout = true;
            }

            SecondaryColumnDefinition.Width = new GridLength(0d, GridUnitType.Pixel);
            PrimaryColumnDefinition.Width = new GridLength(1d, GridUnitType.Star);

            PageContentGrid.Width = targetWidth;
            PageContentGrid.MaxWidth = maxWidth;
            return;
        }

        if (_isStackedLayout)
        {
            Grid.SetRow(SecondaryColumnHost, 0);
            Grid.SetColumn(SecondaryColumnHost, 1);
            _isStackedLayout = false;
        }

        if (!PrimaryColumnDefinition.MinWidth.Equals(desiredPrimaryMin))
        {
            PrimaryColumnDefinition.MinWidth = desiredPrimaryMin;
        }

        if (!SecondaryColumnDefinition.MinWidth.Equals(desiredSecondaryMin))
        {
            SecondaryColumnDefinition.MinWidth = desiredSecondaryMin;
        }

        var frameWidth = Math.Max(targetWidth, totalMinimum);
        PageContentGrid.Width = frameWidth;
        PageContentGrid.MaxWidth = maxWidth;

        var availableForColumns = frameWidth - _columnSpacing;
        if (availableForColumns <= 0)
        {
            availableForColumns = totalMinimum - _columnSpacing;
        }

        PrimaryColumnHost.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        SecondaryColumnHost.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));

        var desiredPrimary = Math.Max(desiredPrimaryMin, PrimaryColumnHost.DesiredSize.Width);
        var desiredSecondary = Math.Max(desiredSecondaryMin, SecondaryColumnHost.DesiredSize.Width);
        var desiredTotal = desiredPrimary + desiredSecondary;

        double primaryWidth;
        double secondaryWidth;

        if (desiredTotal <= availableForColumns)
        {
            primaryWidth = desiredPrimary;
            secondaryWidth = desiredSecondary;
        }
        else
        {
            var scale = availableForColumns / desiredTotal;
            primaryWidth = Math.Max(desiredPrimaryMin, desiredPrimary * scale);
            secondaryWidth = Math.Max(desiredSecondaryMin, availableForColumns - primaryWidth);

            var combined = primaryWidth + secondaryWidth;
            if (combined > availableForColumns)
            {
                var overflow = combined - availableForColumns;
                var maxPrimaryReduction = Math.Max(0d, primaryWidth - desiredPrimaryMin);
                var primaryReduction = Math.Min(overflow * ColumnShareBias, maxPrimaryReduction);
                primaryWidth -= primaryReduction;
                overflow -= primaryReduction;

                if (overflow > 0)
                {
                    var maxSecondaryReduction = Math.Max(0d, secondaryWidth - desiredSecondaryMin);
                    var secondaryReduction = Math.Min(overflow, maxSecondaryReduction);
                    secondaryWidth -= secondaryReduction;
                    overflow -= secondaryReduction;

                    if (overflow > 0)
                    {
                        primaryWidth = Math.Max(desiredPrimaryMin, primaryWidth - overflow);
                    }
                }
            }
        }

        var remaining = availableForColumns - (primaryWidth + secondaryWidth);
        if (remaining > 0)
        {
            primaryWidth += remaining * ColumnShareBias;
            secondaryWidth += remaining * (1d - ColumnShareBias);
        }

        primaryWidth = Math.Max(desiredPrimaryMin, primaryWidth);
        secondaryWidth = Math.Max(desiredSecondaryMin, secondaryWidth);

        PrimaryColumnDefinition.Width = new GridLength(primaryWidth, GridUnitType.Pixel);
        SecondaryColumnDefinition.Width = new GridLength(secondaryWidth, GridUnitType.Pixel);
    }

    private void UpdateHeaderLayout(bool stackLayout, bool compactColumns)
    {
        var pageGrid = PageContentGrid;
        var contentScrollViewer = ContentScrollViewer;
        var actionHost = HeaderActionHost;
        var actionPanel = HeaderActionPanel;
        var lastRefreshed = HeaderLastRefreshedText;
        var headerColumn = HeaderSecondaryColumn;
        var summaryPanel = HeaderSummaryPanel;

        if (pageGrid is null || contentScrollViewer is null || actionHost is null || actionPanel is null || lastRefreshed is null || headerColumn is null)
        {
            return;
        }

        double headerAvailableWidth = pageGrid.ActualWidth;
        if (double.IsNaN(headerAvailableWidth) || headerAvailableWidth <= 0)
        {
            headerAvailableWidth = contentScrollViewer.ViewportWidth;
            if (double.IsNaN(headerAvailableWidth) || headerAvailableWidth <= 0)
            {
                headerAvailableWidth = contentScrollViewer.ActualWidth;
            }
        }

        double headerRequiredWidth = 0d;

        if (summaryPanel is not null)
        {
            summaryPanel.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            headerRequiredWidth += summaryPanel.DesiredSize.Width;
        }

        actionHost.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        headerRequiredWidth += actionHost.DesiredSize.Width;

        if (headerRequiredWidth > 0)
        {
            headerRequiredWidth += _columnSpacing;
        }

        var exceedsWidth = headerAvailableWidth > 0 && headerRequiredWidth > headerAvailableWidth;
        var shouldStack = stackLayout || compactColumns || exceedsWidth;

        if (shouldStack)
        {
            if (_isHeaderStacked)
            {
                return;
            }

            Grid.SetRow(actionHost, 1);
            Grid.SetColumn(actionHost, 0);
            actionHost.Margin = _headerActionStackedMargin;
            actionHost.HorizontalAlignment = HorizontalAlignment.Stretch;
            actionHost.VerticalAlignment = VerticalAlignment.Top;
            actionPanel.HorizontalAlignment = HorizontalAlignment.Left;
            lastRefreshed.HorizontalAlignment = HorizontalAlignment.Left;
            headerColumn.Width = new GridLength(0d, GridUnitType.Pixel);
            _isHeaderStacked = true;
            return;
        }

        if (!_isHeaderStacked)
        {
            return;
        }

        Grid.SetRow(actionHost, 0);
        Grid.SetColumn(actionHost, 1);
        actionHost.Margin = _headerActionDefaultMargin;
        actionHost.HorizontalAlignment = _headerActionDefaultAlignment;
        actionHost.VerticalAlignment = _headerActionDefaultVerticalAlignment;
        actionPanel.HorizontalAlignment = _headerActionPanelDefaultAlignment;
        lastRefreshed.HorizontalAlignment = _headerLastRefreshedDefaultAlignment;
        headerColumn.Width = _headerSecondaryDefaultWidth;
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
