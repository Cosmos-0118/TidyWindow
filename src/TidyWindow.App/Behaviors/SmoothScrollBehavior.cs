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
        public MouseWheelEventHandler? Handler { get; set; }
        public ScrollChangedEventHandler? ScrollChangedHandler { get; set; }
        public EventHandler? RenderingHandler { get; set; }
        public double TargetVertical { get; set; }
        public double TargetHorizontal { get; set; }
        public bool IsRendering { get; set; }
    }

    private static readonly ConditionalWeakTable<ScrollViewer, ScrollState> States = new();

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

            MouseWheelEventHandler handler = (sender, args) => HandleMouseWheel(viewer, args);
            state.Handler = handler;
            viewer.PreviewMouseWheel += handler;

            ScrollChangedEventHandler scrollChanged = (_, _) =>
            {
                state.TargetVertical = viewer.VerticalOffset;
                state.TargetHorizontal = viewer.HorizontalOffset;
            };
            state.ScrollChangedHandler = scrollChanged;
            viewer.ScrollChanged += scrollChanged;

            state.TargetVertical = viewer.VerticalOffset;
            state.TargetHorizontal = viewer.HorizontalOffset;
        }
        else if (States.TryGetValue(viewer, out var state) && state.Handler is not null)
        {
            viewer.PreviewMouseWheel -= state.Handler;
            state.Handler = null;

            if (state.ScrollChangedHandler is not null)
            {
                viewer.ScrollChanged -= state.ScrollChangedHandler;
                state.ScrollChangedHandler = null;
            }

            StopRendering(state);
            States.Remove(viewer);
        }
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

        var hasVertical = viewer.ScrollableHeight > 0.0;
        var hasHorizontal = viewer.ScrollableWidth > 0.0;
        if (!hasVertical && !hasHorizontal)
        {
            return;
        }

        var state = States.GetValue(viewer, static _ => new ScrollState());

        var multiplier = Math.Max(0.16, GetMultiplier(viewer));
        var boost = 1.0 + Math.Min(1.5, Math.Abs(e.Delta) / 480d); // faster when device reports larger deltas
        var delta = e.Delta * multiplier * boost;

        if ((Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && hasHorizontal) || (!hasVertical && hasHorizontal))
        {
            var start = state.TargetHorizontal;
            if (double.IsNaN(start) || double.IsInfinity(start))
            {
                start = viewer.HorizontalOffset;
            }

            var target = Clamp(start - delta, 0, viewer.ScrollableWidth);
            if (Math.Abs(target - viewer.HorizontalOffset) > 0.05)
            {
                state.TargetHorizontal = target;
                StartRendering(viewer, state);
                e.Handled = true;
            }
        }
        else if (hasVertical)
        {
            var start = state.TargetVertical;
            if (double.IsNaN(start) || double.IsInfinity(start))
            {
                start = viewer.VerticalOffset;
            }

            var target = Clamp(start - delta, 0, viewer.ScrollableHeight);
            if (Math.Abs(target - viewer.VerticalOffset) > 0.05)
            {
                state.TargetVertical = target;
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

        EventHandler tick = (_, _) => Tick(viewer, state);
        state.RenderingHandler = tick;
        CompositionTarget.Rendering += tick;
        state.IsRendering = true;
    }

    private static void StopRendering(ScrollState state)
    {
        if (state.RenderingHandler is null)
        {
            return;
        }

        CompositionTarget.Rendering -= state.RenderingHandler;
        state.RenderingHandler = null;
        state.IsRendering = false;
    }

    private static void Tick(ScrollViewer viewer, ScrollState state)
    {
        var vDiff = state.TargetVertical - viewer.VerticalOffset;
        var hDiff = state.TargetHorizontal - viewer.HorizontalOffset;

        var vDone = Math.Abs(vDiff) < 0.1 || viewer.ScrollableHeight <= 0.0;
        var hDone = Math.Abs(hDiff) < 0.1 || viewer.ScrollableWidth <= 0.0;

        if (!vDone)
        {
            var step = vDiff * 0.46; // quicker easing toward target
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset + step);
        }
        else
        {
            if (viewer.ScrollableHeight > 0.0)
            {
                viewer.ScrollToVerticalOffset(state.TargetVertical);
            }
        }

        if (!hDone)
        {
            var step = hDiff * 0.46;
            viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset + step);
        }
        else
        {
            if (viewer.ScrollableWidth > 0.0)
            {
                viewer.ScrollToHorizontalOffset(state.TargetHorizontal);
            }
        }

        if ((vDone || viewer.ScrollableHeight <= 0.0) && (hDone || viewer.ScrollableWidth <= 0.0))
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
