using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using System.Windows.Media;
using TidyWindow.App.ViewModels;
using TidyWindow.App.Views.Dialogs;
using TidyWindow.Core.Maintenance;

namespace TidyWindow.App.Views;

public partial class RegistryOptimizerPage : Page
{
    private readonly RegistryOptimizerViewModel _viewModel;
    private bool _isStackedLayout;
    private bool _disposed;
    private bool _scrollHandlersAttached;
    private bool _rollbackPromptOpen;

    private const double WideLayoutBreakpoint = 1240d;
    private const double CompactLayoutBreakpoint = 1080d;
    private const double StackedLayoutBreakpoint = 940d;

    private Thickness _secondaryColumnDefaultMargin;
    private readonly Thickness _secondaryColumnStackedMargin = new(0, 24, 0, 0);
    private Thickness _scrollViewerDefaultMargin;
    private readonly Thickness _scrollViewerCompactMargin = new(24);
    private readonly Thickness _scrollViewerStackedMargin = new(16, 24, 16, 24);
    private double _primaryColumnDefaultMinWidth;
    private double _secondaryColumnDefaultMinWidth;

    public RegistryOptimizerPage(RegistryOptimizerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        viewModel.RestorePointCreated += OnRestorePointCreated;

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
        _viewModel.RestorePointCreated -= OnRestorePointCreated;
        ContentScrollViewer.SizeChanged -= ContentScrollViewer_SizeChanged;
        IsVisibleChanged -= OnIsVisibleChanged;
        DetachScrollHandlers();
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
        if (double.IsNaN(viewportWidth) || viewportWidth <= 0)
        {
            return;
        }

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

            var primary = compactColumns ? new GridLength(1d, GridUnitType.Star) : new GridLength(3d, GridUnitType.Star);
            var secondary = compactColumns ? new GridLength(1d, GridUnitType.Star) : new GridLength(2d, GridUnitType.Star);

            if (!PrimaryColumnDefinition.Width.Equals(primary))
            {
                PrimaryColumnDefinition.Width = primary;
            }

            if (!SecondaryColumnDefinition.Width.Equals(secondary))
            {
                SecondaryColumnDefinition.Width = secondary;
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

        AttachScrollHandler(TweaksListView);
        AttachScrollHandler(PresetListBox);
        _scrollHandlersAttached = true;
    }

    private void DetachScrollHandlers()
    {
        if (!_scrollHandlersAttached)
        {
            return;
        }

        TweaksListView.PreviewMouseWheel -= OnNestedPreviewMouseWheel;
        PresetListBox.PreviewMouseWheel -= OnNestedPreviewMouseWheel;
        _scrollHandlersAttached = false;
    }

    private static void AttachScrollHandler(UIElement? element)
    {
        if (element is null)
        {
            return;
        }

        element.PreviewMouseWheel -= OnNestedPreviewMouseWheel;
        element.PreviewMouseWheel += OnNestedPreviewMouseWheel;
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
        if (e.Handled || sender is not DependencyObject node)
        {
            return;
        }

        if (FindParentPage(node) is RegistryOptimizerPage page)
        {
            page.BubbleScroll(e, node);
        }
    }

    private static RegistryOptimizerPage? FindParentPage(DependencyObject node)
    {
        while (node is not null)
        {
            if (node is RegistryOptimizerPage page)
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

    private void DocumentationLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;

        var target = e.Uri?.ToString();
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        try
        {
            var resolved = target;
            if (!Uri.IsWellFormedUriString(target, UriKind.Absolute))
            {
                var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                var relativePath = target.Replace('/', Path.DirectorySeparatorChar);
                var candidate = Path.GetFullPath(Path.Combine(baseDirectory, relativePath));

                if (File.Exists(candidate))
                {
                    resolved = candidate;
                }
            }

            var startInfo = new ProcessStartInfo(resolved)
            {
                UseShellExecute = true
            };

            Process.Start(startInfo);
        }
        catch
        {
            // Navigation failures are non-fatal; leave silently for now.
        }
    }

    private async void OnRestorePointCreated(object? sender, RegistryRestorePointCreatedEventArgs e)
    {
        if (e is null)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            await Dispatcher.InvokeAsync(() => OnRestorePointCreated(sender, e));
            return;
        }

        await Task.Yield();
        await ShowRollbackDialogAsync(e.RestorePoint).ConfigureAwait(true);
    }

    private async Task ShowRollbackDialogAsync(RegistryRestorePoint restorePoint)
    {
        if (_rollbackPromptOpen || !IsLoaded)
        {
            return;
        }

        _rollbackPromptOpen = true;
        try
        {
            var owner = Window.GetWindow(this) ?? System.Windows.Application.Current.MainWindow;
            var dialog = new RegistryRollbackDialog();
            if (owner is not null)
            {
                dialog.Owner = owner;
            }

            dialog.Topmost = true;

            dialog.ShowDialog();

            if (dialog.ShouldRevert)
            {
                await _viewModel.RevertRestorePointAsync(restorePoint, dialog.WasAutoTriggered).ConfigureAwait(true);
            }
        }
        finally
        {
            _rollbackPromptOpen = false;
        }
    }
}
