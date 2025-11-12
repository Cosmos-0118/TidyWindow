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
        new PropertyMetadata(0.25));

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
        }
        else if (States.TryGetValue(viewer, out var state) && state.Handler is not null)
        {
            viewer.PreviewMouseWheel -= state.Handler;
            state.Handler = null;
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
            if (nested is not null && (nested.ScrollableHeight > 0.0 || nested.ScrollableWidth > 0.0))
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

        var multiplier = Math.Max(0.01, GetMultiplier(viewer));
        var delta = e.Delta * multiplier;

        if ((Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && hasHorizontal) || (!hasVertical && hasHorizontal))
        {
            var target = viewer.HorizontalOffset - delta;
            var clamped = Clamp(target, 0, viewer.ScrollableWidth);
            if (Math.Abs(clamped - viewer.HorizontalOffset) > 0.1)
            {
                viewer.ScrollToHorizontalOffset(clamped);
                e.Handled = true;
            }
        }
        else if (hasVertical)
        {
            var target = viewer.VerticalOffset - delta;
            var clamped = Clamp(target, 0, viewer.ScrollableHeight);
            if (Math.Abs(clamped - viewer.VerticalOffset) > 0.1)
            {
                viewer.ScrollToVerticalOffset(clamped);
                e.Handled = true;
            }
        }
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
