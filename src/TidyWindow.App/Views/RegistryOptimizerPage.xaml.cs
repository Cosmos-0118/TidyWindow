using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels;
using TidyWindow.App.Views.Dialogs;
using TidyWindow.Core.Maintenance;

namespace TidyWindow.App.Views;

public partial class RegistryOptimizerPage : Page, INavigationAware
{
    private readonly RegistryOptimizerViewModel _viewModel;
    private bool _disposed;
    private bool _scrollHandlersAttached;
    private bool _rollbackPromptOpen;
    private bool _rootScrollWheelAttached;
    private double _scrollAnimationTarget = double.NaN;

    private static readonly DependencyProperty AnimatedVerticalOffsetProperty = DependencyProperty.Register(
        nameof(AnimatedVerticalOffset),
        typeof(double),
        typeof(RegistryOptimizerPage),
        new PropertyMetadata(0d, OnAnimatedVerticalOffsetChanged));

    public RegistryOptimizerPage(RegistryOptimizerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        viewModel.RestorePointCreated += OnRestorePointCreated;

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private double AnimatedVerticalOffset
    {
        get => (double)GetValue(AnimatedVerticalOffsetProperty);
        set => SetValue(AnimatedVerticalOffsetProperty, value);
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnPageLoaded;
        EnsureScrollHandlers();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            if (_disposed)
            {
                _viewModel.RestorePointCreated += OnRestorePointCreated;
                _disposed = false;
            }
            EnsureScrollHandlers();
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _viewModel.RestorePointCreated -= OnRestorePointCreated;
        _viewModel.IsPresetDialogVisible = false;
        _viewModel.IsRestorePointsDialogVisible = false;
        _viewModel.IsTweakDetailsDialogVisible = false;

        if (_rootScrollWheelAttached)
        {
            ContentScrollViewer.PreviewMouseWheel -= OnContentScrollViewerPreviewMouseWheel;
            _rootScrollWheelAttached = false;
        }

        BeginAnimation(AnimatedVerticalOffsetProperty, null);
        _scrollAnimationTarget = double.NaN;
        DetachScrollHandlers();
        _disposed = true;
    }

    private void OnDialogOverlayClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Grid grid && grid.Background != null)
        {
            _viewModel.IsPresetDialogVisible = false;
            _viewModel.IsRestorePointsDialogVisible = false;
            _viewModel.IsTweakDetailsDialogVisible = false;
            e.Handled = true;
        }
    }

    private void OnRestorePointBannerClick(object sender, MouseButtonEventArgs e)
    {
        _viewModel.ShowRestorePointsDialogCommand.Execute(null);
        e.Handled = true;
    }

    private void OnPresetItemClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is RegistryPresetViewModel preset)
        {
            _viewModel.SelectPresetCommand.Execute(preset);
            e.Handled = true;
        }
    }

    private void EnsureScrollHandlers()
    {
        if (_scrollHandlersAttached)
        {
            if (!_rootScrollWheelAttached)
            {
                ContentScrollViewer.PreviewMouseWheel -= OnContentScrollViewerPreviewMouseWheel;
                ContentScrollViewer.PreviewMouseWheel += OnContentScrollViewerPreviewMouseWheel;
                _rootScrollWheelAttached = true;
            }
            return;
        }

        AttachScrollHandler(TweaksListView);
        _scrollHandlersAttached = true;

        ContentScrollViewer.PreviewMouseWheel -= OnContentScrollViewerPreviewMouseWheel;
        ContentScrollViewer.PreviewMouseWheel += OnContentScrollViewerPreviewMouseWheel;
        _rootScrollWheelAttached = true;
    }

    private void DetachScrollHandlers()
    {
        if (!_scrollHandlersAttached)
        {
            return;
        }

        TweaksListView.PreviewMouseWheel -= OnNestedPreviewMouseWheel;
        _scrollHandlersAttached = false;

        if (_rootScrollWheelAttached)
        {
            ContentScrollViewer.PreviewMouseWheel -= OnContentScrollViewerPreviewMouseWheel;
            _rootScrollWheelAttached = false;
        }
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
        var targetOffset = ContentScrollViewer.VerticalOffset + CalculateWheelStep(e.Delta, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        BeginSmoothScroll(targetOffset);
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

    private void OnContentScrollViewerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_viewModel.IsPresetDialogVisible)
        {
            e.Handled = true;
            return;
        }

        if (ContentScrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        e.Handled = true;

        var delta = CalculateWheelStep(e.Delta, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        BeginSmoothScroll(ContentScrollViewer.VerticalOffset + delta);
    }

    private void BeginSmoothScroll(double targetOffset)
    {
        if (ContentScrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        var clampedTarget = Math.Max(0d, Math.Min(ContentScrollViewer.ScrollableHeight, targetOffset));
        var currentAnimatedOffset = AnimatedVerticalOffset;
        if (double.IsNaN(currentAnimatedOffset) || double.IsInfinity(currentAnimatedOffset))
        {
            currentAnimatedOffset = ContentScrollViewer.VerticalOffset;
        }

        var start = double.IsNaN(_scrollAnimationTarget)
            ? ContentScrollViewer.VerticalOffset
            : currentAnimatedOffset;

        _scrollAnimationTarget = clampedTarget;

        if (Math.Abs(clampedTarget - start) < 0.25)
        {
            ContentScrollViewer.ScrollToVerticalOffset(clampedTarget);
            _scrollAnimationTarget = double.NaN;
            return;
        }

        var distance = Math.Abs(clampedTarget - start);
        var duration = TimeSpan.FromMilliseconds(Math.Max(80d, Math.Min(210d, distance * 0.92)));
        var easing = new CubicEase
        {
            EasingMode = EasingMode.EaseOut
        };

        BeginAnimation(AnimatedVerticalOffsetProperty, null);
        AnimatedVerticalOffset = start;

        var animation = new DoubleAnimation(start, clampedTarget, new Duration(duration))
        {
            EasingFunction = easing
        };

        animation.Completed += (_, _) => _scrollAnimationTarget = double.NaN;

        BeginAnimation(AnimatedVerticalOffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static void OnAnimatedVerticalOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RegistryOptimizerPage page)
        {
            return;
        }

        var newOffset = (double)e.NewValue;
        if (double.IsNaN(newOffset) || double.IsInfinity(newOffset))
        {
            return;
        }

        page.ContentScrollViewer.ScrollToVerticalOffset(newOffset);
    }

    private double CalculateWheelStep(int wheelDelta, bool accelerate)
    {
        if (wheelDelta == 0)
        {
            return 0d;
        }

        var viewportHeight = ContentScrollViewer.ViewportHeight;
        if (double.IsNaN(viewportHeight) || viewportHeight <= 0)
        {
            viewportHeight = 600d;
        }

        var direction = wheelDelta > 0 ? -1d : 1d;
        var magnitude = Math.Max(56d, Math.Min(320d, viewportHeight * 0.22));
        var wheelIntensity = Math.Max(1d, Math.Abs(wheelDelta) / 120d);

        if (accelerate)
        {
            magnitude *= 1.35;
        }
        else
        {
            magnitude *= 0.9;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !accelerate)
        {
            magnitude *= 0.7;
        }

        var lineMultiplier = SystemParameters.WheelScrollLines;
        if (lineMultiplier > 0)
        {
            magnitude *= Math.Max(0.65, Math.Min(1.8, lineMultiplier / 3d));
        }

        return direction * magnitude * wheelIntensity;
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

    /// <inheritdoc />
    public void OnNavigatedTo()
    {
        if (_disposed)
        {
            _viewModel.RestorePointCreated += OnRestorePointCreated;
            _disposed = false;
        }

        EnsureScrollHandlers();
        ContentScrollViewer.ScrollToTop();
    }

    /// <inheritdoc />
    public void OnNavigatingFrom()
    {
        _viewModel.IsPresetDialogVisible = false;
        _viewModel.IsRestorePointsDialogVisible = false;
        _viewModel.IsTweakDetailsDialogVisible = false;
        DetachScrollHandlers();
    }
}
