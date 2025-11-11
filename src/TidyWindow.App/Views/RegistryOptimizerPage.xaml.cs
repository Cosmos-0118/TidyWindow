using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    private bool _rootScrollWheelAttached;
    private double _scrollAnimationTarget = double.NaN;

    private const double MarginTighteningBuffer = 160d;
    private const double ScreenMaxWidthRatio = 0.92d;
    private const double CompactCardPrimaryWidthThreshold = 780d;

    private Thickness _secondaryColumnDefaultMargin;
    private readonly Thickness _secondaryColumnStackedMargin = new(0, 24, 0, 0);
    private Thickness _scrollViewerDefaultMargin;
    private readonly Thickness _scrollViewerCompactMargin = new(24);
    private readonly Thickness _scrollViewerStackedMargin = new(16, 24, 16, 24);
    private double _primaryColumnDefaultMinWidth;
    private double _secondaryColumnDefaultMinWidth;
    private readonly double _columnSpacing;
    private readonly double _pageContentDefaultMaxWidth;

    public static readonly DependencyProperty IsCompactCardsProperty = DependencyProperty.Register(
        nameof(IsCompactCards),
        typeof(bool),
        typeof(RegistryOptimizerPage),
        new PropertyMetadata(false));

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

        _secondaryColumnDefaultMargin = SecondaryColumnHost.Margin;
        _scrollViewerDefaultMargin = ContentScrollViewer.Margin;
        _primaryColumnDefaultMinWidth = PrimaryColumnDefinition.MinWidth;
        _secondaryColumnDefaultMinWidth = SecondaryColumnDefinition.MinWidth;
        _columnSpacing = PrimaryColumnHost.Margin.Right <= 0 ? 24d : PrimaryColumnHost.Margin.Right;
        _pageContentDefaultMaxWidth = double.IsNaN(PageContentGrid.MaxWidth) || PageContentGrid.MaxWidth <= 0
            ? double.PositiveInfinity
            : PageContentGrid.MaxWidth;

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
        ContentScrollViewer.SizeChanged -= ContentScrollViewer_SizeChanged;
        ContentScrollViewer.SizeChanged += ContentScrollViewer_SizeChanged;
    }

    public bool IsCompactCards
    {
        get => (bool)GetValue(IsCompactCardsProperty);
        set => SetValue(IsCompactCardsProperty, value);
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
        UpdateResponsiveLayout(ContentScrollViewer.ActualWidth);
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

        _viewModel.RestorePointCreated -= OnRestorePointCreated;
        ContentScrollViewer.SizeChanged -= ContentScrollViewer_SizeChanged;
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

        var totalMinimum = _primaryColumnDefaultMinWidth + _secondaryColumnDefaultMinWidth + _columnSpacing;
        var stackLayout = targetWidth < totalMinimum;

        var tightMargins = viewportWidth < totalMinimum + MarginTighteningBuffer;
        var desiredMargin = stackLayout
            ? _scrollViewerStackedMargin
            : tightMargins
                ? _scrollViewerCompactMargin
                : _scrollViewerDefaultMargin;

        if (!ContentScrollViewer.Margin.Equals(desiredMargin))
        {
            ContentScrollViewer.Margin = desiredMargin;
        }

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

            PageContentGrid.Width = double.IsNaN(targetWidth) ? double.NaN : targetWidth;
            PageContentGrid.MaxWidth = Math.Max(totalMinimum, defaultMaxWidth);
            if (!IsCompactCards)
            {
                IsCompactCards = true;
            }
            return;
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
        }

        var frameWidth = Math.Max(targetWidth, totalMinimum);
        PageContentGrid.Width = frameWidth;
        PageContentGrid.MaxWidth = Math.Max(totalMinimum, defaultMaxWidth);

        var availableForColumns = frameWidth - _columnSpacing;
        if (availableForColumns <= 0)
        {
            availableForColumns = totalMinimum - _columnSpacing;
        }

        // Measure child columns so we can scale based on their desired width instead of fixed breakpoints.
        PrimaryColumnHost.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        SecondaryColumnHost.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));

        var desiredPrimary = Math.Max(_primaryColumnDefaultMinWidth, PrimaryColumnHost.DesiredSize.Width);
        var desiredSecondary = Math.Max(_secondaryColumnDefaultMinWidth, SecondaryColumnHost.DesiredSize.Width);
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
            primaryWidth = Math.Max(_primaryColumnDefaultMinWidth, desiredPrimary * scale);
            secondaryWidth = Math.Max(_secondaryColumnDefaultMinWidth, availableForColumns - primaryWidth);

            if (primaryWidth + secondaryWidth > availableForColumns)
            {
                var overflow = primaryWidth + secondaryWidth - availableForColumns;
                var maxPrimaryReduction = Math.Max(0d, primaryWidth - _primaryColumnDefaultMinWidth);
                var primaryReduction = Math.Min(overflow * 0.55, maxPrimaryReduction);
                primaryWidth -= primaryReduction;
                overflow -= primaryReduction;

                if (overflow > 0)
                {
                    var maxSecondaryReduction = Math.Max(0d, secondaryWidth - _secondaryColumnDefaultMinWidth);
                    var secondaryReduction = Math.Min(overflow, maxSecondaryReduction);
                    secondaryWidth -= secondaryReduction;
                    overflow -= secondaryReduction;

                    if (overflow > 0)
                    {
                        primaryWidth = Math.Max(_primaryColumnDefaultMinWidth, primaryWidth - overflow);
                    }
                }
            }
        }

        var remaining = availableForColumns - (primaryWidth + secondaryWidth);
        if (remaining > 0)
        {
            primaryWidth += remaining * 0.55;
            secondaryWidth += remaining * 0.45;
        }

        primaryWidth = Math.Max(_primaryColumnDefaultMinWidth, primaryWidth);
        secondaryWidth = Math.Max(_secondaryColumnDefaultMinWidth, secondaryWidth);

        PrimaryColumnDefinition.Width = new GridLength(primaryWidth, GridUnitType.Pixel);
        SecondaryColumnDefinition.Width = new GridLength(secondaryWidth, GridUnitType.Pixel);

        var shouldCompactCards = viewportWidth < totalMinimum + MarginTighteningBuffer
                                  || primaryWidth < CompactCardPrimaryWidthThreshold;
        if (IsCompactCards != shouldCompactCards)
        {
            IsCompactCards = shouldCompactCards;
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
        AttachScrollHandler(PresetListBox);
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
        PresetListBox.PreviewMouseWheel -= OnNestedPreviewMouseWheel;
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
        var duration = TimeSpan.FromMilliseconds(Math.Max(90d, Math.Min(260d, distance * 1.15)));
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
        var magnitude = Math.Max(72d, viewportHeight * 0.32);
        var wheelIntensity = Math.Max(1d, Math.Abs(wheelDelta) / 120d);

        if (accelerate)
        {
            magnitude *= 1.65;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !accelerate)
        {
            magnitude *= 0.7;
        }

        var lineMultiplier = SystemParameters.WheelScrollLines;
        if (lineMultiplier > 0)
        {
            magnitude *= Math.Max(0.6, Math.Min(2.2, lineMultiplier / 3d));
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
