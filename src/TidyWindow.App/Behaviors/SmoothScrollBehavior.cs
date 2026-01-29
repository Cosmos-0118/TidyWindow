using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace TidyWindow.App.Behaviors;

/// <summary>
/// Introduces pixel-based mouse-wheel scrolling with a gentle easing factor for every ScrollViewer.
/// </summary>
public static class SmoothScrollBehavior
{
    private sealed class ScrollState
    {
        private WeakReference<ScrollViewer>? _viewerRef;

        public MouseWheelEventHandler? Handler { get; set; }
        public EventHandler? RenderingHandler { get; set; }
        public double TargetVertical { get; set; }
        public double TargetHorizontal { get; set; }
        public bool IsRendering { get; set; }
        public bool IsAnimating { get; set; }
        public int FramesSinceLastWheel { get; set; }

        public ScrollViewer? Viewer
        {
            get => _viewerRef?.TryGetTarget(out var v) == true ? v : null;
            set => _viewerRef = value is not null ? new WeakReference<ScrollViewer>(value) : null;
        }
    }

    private static readonly ConditionalWeakTable<ScrollViewer, ScrollState> States = new();
    private const int MaxIdleFrames = 3;

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(SmoothScrollBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty MultiplierProperty = DependencyProperty.RegisterAttached(
        "Multiplier",
        typeof(double),
        typeof(SmoothScrollBehavior),
        new PropertyMetadata(1.3));

    public static bool GetIsEnabled(DependencyObject element)
    {
        return element is not null && (bool)element.GetValue(IsEnabledProperty);
    }

    public static void SetIsEnabled(DependencyObject element, bool value)
    {
        element?.SetValue(IsEnabledProperty, value);
    }

    public static double GetMultiplier(DependencyObject element)
    {
        return element is not null ? (double)element.GetValue(MultiplierProperty) : 0.25d;
    }

    public static void SetMultiplier(DependencyObject element, double value)
    {
        element?.SetValue(MultiplierProperty, value);
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer)
        {
            return;
        }

        var enable = (bool)e.NewValue;

        if (enable)
        {
            var state = States.GetValue(viewer, static _ => new ScrollState());
            if (state.Handler is not null)
            {
                return;
            }

            state.Viewer = viewer;
            state.TargetVertical = viewer.VerticalOffset;
            state.TargetHorizontal = viewer.HorizontalOffset;

            MouseWheelEventHandler handler = (sender, args) => HandleMouseWheel(viewer, args);
            state.Handler = handler;
            viewer.PreviewMouseWheel += handler;

            // Subscribe to Unloaded to clean up when the ScrollViewer is removed
            viewer.Unloaded += OnScrollViewerUnloaded;
        }
        else if (States.TryGetValue(viewer, out var state) && state.Handler is not null)
        {
            viewer.Unloaded -= OnScrollViewerUnloaded;
            viewer.PreviewMouseWheel -= state.Handler;
            state.Handler = null;
            state.Viewer = null;
            StopRendering(state);
            States.Remove(viewer);
        }
    }

    private static void OnScrollViewerUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ScrollViewer viewer)
        {
            return;
        }

        // Clean up when the ScrollViewer is unloaded to prevent memory leaks
        if (States.TryGetValue(viewer, out var state))
        {
            if (state.Handler is not null)
            {
                viewer.PreviewMouseWheel -= state.Handler;
                state.Handler = null;
            }

            state.Viewer = null;
            StopRendering(state);
            States.Remove(viewer);
        }

        viewer.Unloaded -= OnScrollViewerUnloaded;
    }

    private static void HandleMouseWheel(ScrollViewer viewer, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source)
        {
            var nested = FindInnerScrollViewer(source, viewer);
            if (nested is not null && CanNestedScroll(nested, e.Delta))
            {
                return;
            }
        }

        var hasVertical = viewer.ScrollableHeight > 0.5;
        var hasHorizontal = viewer.ScrollableWidth > 0.5;
        if (!hasVertical && !hasHorizontal)
        {
            return;
        }

        var state = States.GetValue(viewer, static _ => new ScrollState());
        state.FramesSinceLastWheel = 0;

        // Sync target with current offset if not animating to prevent drift
        if (!state.IsAnimating)
        {
            state.TargetVertical = viewer.VerticalOffset;
            state.TargetHorizontal = viewer.HorizontalOffset;
        }

        var multiplier = Math.Max(0.16, GetMultiplier(viewer));
        var boost = 1.0 + Math.Min(1.5, Math.Abs(e.Delta) / 480d);
        var delta = e.Delta * multiplier * boost;

        if ((Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && hasHorizontal) || (!hasVertical && hasHorizontal))
        {
            var target = Clamp(state.TargetHorizontal - delta, 0, viewer.ScrollableWidth);
            if (Math.Abs(target - viewer.HorizontalOffset) > 0.5)
            {
                state.TargetHorizontal = target;
                state.IsAnimating = true;
                StartRendering(viewer, state);
                e.Handled = true;
            }
        }
        else if (hasVertical)
        {
            var target = Clamp(state.TargetVertical - delta, 0, viewer.ScrollableHeight);
            if (Math.Abs(target - viewer.VerticalOffset) > 0.5)
            {
                state.TargetVertical = target;
                state.IsAnimating = true;
                StartRendering(viewer, state);
                e.Handled = true;
            }
        }
    }

    private static void StartRendering(ScrollViewer viewer, ScrollState state)
    {
        if (state.IsRendering)
        {
            return;
        }

        state.Viewer = viewer;
        EventHandler tick = (_, _) => Tick(state);
        state.RenderingHandler = tick;
        CompositionTarget.Rendering += tick;
        state.IsRendering = true;
    }

    private static void StopRendering(ScrollState state)
    {
        if (!state.IsRendering || state.RenderingHandler is null)
        {
            return;
        }

        CompositionTarget.Rendering -= state.RenderingHandler;
        state.RenderingHandler = null;
        state.IsRendering = false;
        state.IsAnimating = false;
    }

    private static void Tick(ScrollState state)
    {
        var viewer = state.Viewer;
        if (viewer is null || !viewer.IsLoaded)
        {
            StopRendering(state);
            return;
        }

        state.FramesSinceLastWheel++;

        var vDiff = state.TargetVertical - viewer.VerticalOffset;
        var hDiff = state.TargetHorizontal - viewer.HorizontalOffset;

        var vDone = Math.Abs(vDiff) < 0.5 || viewer.ScrollableHeight <= 0.5;
        var hDone = Math.Abs(hDiff) < 0.5 || viewer.ScrollableWidth <= 0.5;

        // Use a faster easing factor for more responsive feel
        const double easingFactor = 0.38;

        if (!vDone)
        {
            var step = vDiff * easingFactor;
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset + step);
        }
        else if (viewer.ScrollableHeight > 0.5)
        {
            viewer.ScrollToVerticalOffset(state.TargetVertical);
        }

        if (!hDone)
        {
            var step = hDiff * easingFactor;
            viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset + step);
        }
        else if (viewer.ScrollableWidth > 0.5)
        {
            viewer.ScrollToHorizontalOffset(state.TargetHorizontal);
        }

        // Stop rendering when animation is complete or after idle frames without input
        var animationComplete = (vDone || viewer.ScrollableHeight <= 0.5) && (hDone || viewer.ScrollableWidth <= 0.5);
        if (animationComplete || state.FramesSinceLastWheel > MaxIdleFrames)
        {
            StopRendering(state);
        }
    }

    private static bool CanNestedScroll(ScrollViewer nested, int wheelDelta)
    {
        if (nested.ScrollableWidth <= 0.0 && nested.ScrollableHeight <= 0.0)
        {
            return false;
        }

        var horizontalIntent = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (horizontalIntent && nested.ScrollableWidth > 0.0)
        {
            return wheelDelta > 0 ? nested.HorizontalOffset > 0.0 : nested.HorizontalOffset < nested.ScrollableWidth;
        }

        if (nested.ScrollableHeight > 0.0)
        {
            return wheelDelta > 0 ? nested.VerticalOffset > 0.0 : nested.VerticalOffset < nested.ScrollableHeight;
        }

        if (nested.ScrollableWidth > 0.0)
        {
            return wheelDelta > 0 ? nested.HorizontalOffset > 0.0 : nested.HorizontalOffset < nested.ScrollableWidth;
        }

        return false;
    }

    private static double Clamp(double value, double min, double max)
    {
        if (value < min)
        {
            return min;
        }

        if (value > max)
        {
            return max;
        }

        return value;
    }

    private static ScrollViewer? FindInnerScrollViewer(DependencyObject source, ScrollViewer outer)
    {
        var current = source;
        while (current is not null && !ReferenceEquals(current, outer))
        {
            if (current is ScrollViewer scrollViewer && !ReferenceEquals(scrollViewer, outer))
            {
                return scrollViewer;
            }

            current = GetParent(current);
        }

        return null;
    }

    private static DependencyObject? GetParent(DependencyObject current)
    {
        if (current is Visual || current is Visual3D)
        {
            return VisualTreeHelper.GetParent(current);
        }

        return current switch
        {
            FrameworkElement element => element.Parent,
            FrameworkContentElement contentElement => contentElement.Parent,
            _ => null
        };
    }
}
