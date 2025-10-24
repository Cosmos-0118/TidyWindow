using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TidyWindow.App.ViewModels;

namespace TidyWindow.App.Views;

public partial class EssentialsPage : Page
{
    private readonly EssentialsViewModel _viewModel;
    private bool _disposed;
    private bool _scrollHandlersAttached;

    private const double WideLayoutBreakpoint = 1280d;
    private const double CompactLayoutBreakpoint = 1120d;
    private const double StackedLayoutBreakpoint = 960d;

    private bool _isStackedLayout;
    private Thickness _secondaryColumnDefaultMargin;
    private readonly Thickness _secondaryColumnStackedMargin = new(0, 24, 0, 0);
    private Thickness _scrollViewerDefaultMargin;
    private readonly Thickness _scrollViewerCompactMargin = new(24);
    private readonly Thickness _scrollViewerStackedMargin = new(16, 24, 16, 24);
    private double _primaryColumnDefaultMinWidth;
    private double _secondaryColumnDefaultMinWidth;

    public EssentialsPage(EssentialsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;

        _secondaryColumnDefaultMargin = SecondaryColumnHost.Margin;
        _scrollViewerDefaultMargin = ContentScrollViewer.Margin;
        _primaryColumnDefaultMinWidth = PrimaryColumnDefinition.MinWidth;
        _secondaryColumnDefaultMinWidth = SecondaryColumnDefinition.MinWidth;

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
        ContentScrollViewer.SizeChanged -= ContentScrollViewer_SizeChanged;
        ContentScrollViewer.SizeChanged += ContentScrollViewer_SizeChanged;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnPageLoaded;
        EnsureScrollHandlers();
        UpdateResponsiveLayout(ContentScrollViewer.ActualWidth);
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            ContentScrollViewer.SizeChanged -= ContentScrollViewer_SizeChanged;
            ContentScrollViewer.SizeChanged += ContentScrollViewer_SizeChanged;
            EnsureScrollHandlers();
            UpdateResponsiveLayout(ContentScrollViewer.ActualWidth);
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        Unloaded -= OnPageUnloaded;
        ContentScrollViewer.SizeChanged -= ContentScrollViewer_SizeChanged;
        IsVisibleChanged -= OnIsVisibleChanged;
        DetachScrollHandlers();
        _viewModel.Dispose();
        _disposed = true;
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

        // Shift between wide, compact, and stacked layouts so queue controls stay accessible on smaller displays.
        var stackLayout = viewportWidth < StackedLayoutBreakpoint;
        var compactColumns = viewportWidth < WideLayoutBreakpoint;
        var tightMargins = viewportWidth < CompactLayoutBreakpoint;

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
        }

        ContentScrollViewer.Margin = stackLayout
            ? _scrollViewerStackedMargin
            : tightMargins || compactColumns
                ? _scrollViewerCompactMargin
                : _scrollViewerDefaultMargin;
    }

    private void EnsureScrollHandlers()
    {
        if (_scrollHandlersAttached)
        {
            return;
        }

        AttachScrollHandler(TasksListBox);
        AttachScrollHandler(OperationsListView);
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

        TasksListBox.PreviewMouseWheel -= OnNestedPreviewMouseWheel;
        OperationsListView.PreviewMouseWheel -= OnNestedPreviewMouseWheel;
        _scrollHandlersAttached = false;
    }

    private void BubbleScroll(MouseWheelEventArgs e, DependencyObject source)
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

    private static void OnNestedPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject dependencyObject)
        {
            return;
        }

        if (FindParentPage(dependencyObject) is EssentialsPage page)
        {
            page.BubbleScroll(e, dependencyObject);
        }
    }

    private static EssentialsPage? FindParentPage(DependencyObject node)
    {
        while (node is not null)
        {
            if (node is EssentialsPage page)
            {
                return page;
            }

            var parent = VisualTreeHelper.GetParent(node) ?? (node as FrameworkElement)?.Parent;
            if (parent is null)
            {
                return null;
            }

            node = parent;
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
