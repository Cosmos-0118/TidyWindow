using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TidyWindow.App.ViewModels;

namespace TidyWindow.App.Views;

public partial class InstallHubPage : Page
{
    private readonly InstallHubViewModel _viewModel;
    private bool _disposed;
    private bool _scrollHandlersAttached;
    private bool _responsiveLayoutApplied;
    private bool _isCompactLayout;
    private bool _sizeHandlerAttached;

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
        if (!_scrollHandlersAttached)
        {
            AttachScrollHandler(BundlesList);
            AttachScrollHandler(PackagesList);
            AttachScrollHandler(OperationsList);
            _scrollHandlersAttached = true;
        }

        if (!_sizeHandlerAttached)
        {
            SizeChanged += OnPageSizeChanged;
            _sizeHandlerAttached = true;
        }

        ApplyResponsiveLayout(ActualWidth);
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
        if (_sizeHandlerAttached)
        {
            SizeChanged -= OnPageSizeChanged;
            _sizeHandlerAttached = false;
        }
        _viewModel.Dispose();
        _disposed = true;
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout(e.NewSize.Width);
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

    private void ApplyResponsiveLayout(double width)
    {
        if (LayoutGrid is null || LeftColumn is null || QueueCard is null)
        {
            return;
        }

        if (width <= 0 || double.IsNaN(width))
        {
            width = ActualWidth;
        }

        const double breakpoint = 1240;
        var shouldBeCompact = width <= breakpoint;

        if (_responsiveLayoutApplied && shouldBeCompact == _isCompactLayout)
        {
            return;
        }

        _isCompactLayout = shouldBeCompact;

        var animate = _responsiveLayoutApplied;

        if (shouldBeCompact)
        {
            LayoutGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            LayoutGrid.ColumnDefinitions[1].Width = new GridLength(0);

            Grid.SetRow(LeftColumn, 0);
            Grid.SetColumn(LeftColumn, 0);
            Grid.SetRowSpan(LeftColumn, 1);
            Grid.SetColumnSpan(LeftColumn, 2);
            AnimateMargin(LeftColumn, new Thickness(0), animate);

            Grid.SetRow(QueueCard, 1);
            Grid.SetColumn(QueueCard, 0);
            Grid.SetRowSpan(QueueCard, 1);
            Grid.SetColumnSpan(QueueCard, 2);
            AnimateMargin(QueueCard, new Thickness(0, 24, 0, 0), animate);
        }
        else
        {
            LayoutGrid.ColumnDefinitions[0].Width = new GridLength(3, GridUnitType.Star);
            LayoutGrid.ColumnDefinitions[1].Width = new GridLength(2, GridUnitType.Star);

            Grid.SetRow(LeftColumn, 0);
            Grid.SetColumn(LeftColumn, 0);
            Grid.SetRowSpan(LeftColumn, 2);
            Grid.SetColumnSpan(LeftColumn, 1);
            AnimateMargin(LeftColumn, new Thickness(0, 0, 24, 0), animate);

            Grid.SetRow(QueueCard, 0);
            Grid.SetColumn(QueueCard, 1);
            Grid.SetRowSpan(QueueCard, 2);
            Grid.SetColumnSpan(QueueCard, 1);
            AnimateMargin(QueueCard, new Thickness(16, 0, 0, 0), animate);
        }

        AnimateMargin(RootScrollViewer, shouldBeCompact ? new Thickness(20) : new Thickness(32), animate);

        _responsiveLayoutApplied = true;
    }

    private static void AnimateMargin(FrameworkElement element, Thickness target, bool animate)
    {
        if (element is null)
        {
            return;
        }

        if (!animate)
        {
            element.BeginAnimation(FrameworkElement.MarginProperty, null);
            element.Margin = target;
            return;
        }

        var animation = new ThicknessAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        element.BeginAnimation(FrameworkElement.MarginProperty, animation);
    }
}
