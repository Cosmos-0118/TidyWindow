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
