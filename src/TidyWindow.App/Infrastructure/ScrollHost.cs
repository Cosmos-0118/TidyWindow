using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TidyWindow.App.Infrastructure;

public static class ScrollHost
{
    private static readonly DependencyProperty CachedDescendantScrollViewerProperty = DependencyProperty.RegisterAttached(
        "CachedDescendantScrollViewer",
        typeof(ScrollViewer),
        typeof(ScrollHost),
        new PropertyMetadata(null));

    private static readonly DependencyProperty CachedAncestorScrollViewerProperty = DependencyProperty.RegisterAttached(
        "CachedAncestorScrollViewer",
        typeof(ScrollViewer),
        typeof(ScrollHost),
        new PropertyMetadata(null));

    public static readonly DependencyProperty BubbleParentScrollProperty = DependencyProperty.RegisterAttached(
        "BubbleParentScroll",
        typeof(bool),
        typeof(ScrollHost),
        new PropertyMetadata(false, OnBubbleParentScrollChanged));

    public static bool GetBubbleParentScroll(DependencyObject element)
    {
        return (bool)element.GetValue(BubbleParentScrollProperty);
    }

    public static void SetBubbleParentScroll(DependencyObject element, bool value)
    {
        element.SetValue(BubbleParentScrollProperty, value);
    }

    private static void OnBubbleParentScrollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        if (e.NewValue is true)
        {
            element.PreviewMouseWheel += OnElementPreviewMouseWheel;
            if (element is FrameworkElement frameworkElement)
            {
                frameworkElement.Loaded += OnElementLoaded;
                frameworkElement.Unloaded += OnElementUnloaded;
            }
            ClearCachedScrollViewers(element);
        }
        else
        {
            element.PreviewMouseWheel -= OnElementPreviewMouseWheel;
            if (element is FrameworkElement frameworkElement)
            {
                frameworkElement.Loaded -= OnElementLoaded;
                frameworkElement.Unloaded -= OnElementUnloaded;
            }
            ClearCachedScrollViewers(element);
        }
    }

    private static void OnElementLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject dependencyObject)
        {
            ClearCachedScrollViewers(dependencyObject);
        }
    }

    private static void OnElementUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject dependencyObject)
        {
            ClearCachedScrollViewers(dependencyObject);
        }
    }

    private static void OnElementPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject source)
        {
            return;
        }

        var innerScrollViewer = GetCachedDescendantScrollViewer(source);
        if (innerScrollViewer is null || !IsDescendantOrSelf(source, innerScrollViewer))
        {
            innerScrollViewer = FindDescendantScrollViewer(source);
            SetCachedDescendantScrollViewer(source, innerScrollViewer);
        }

        if (innerScrollViewer != null && innerScrollViewer.ScrollableHeight > 0)
        {
            if (e.Delta > 0 && innerScrollViewer.VerticalOffset > 0)
            {
                return;
            }

            if (e.Delta < 0 && innerScrollViewer.VerticalOffset < innerScrollViewer.ScrollableHeight)
            {
                return;
            }
        }

        var parentScrollViewer = GetCachedAncestorScrollViewer(source);
        if (parentScrollViewer is null || !IsAncestorOf(source, parentScrollViewer))
        {
            parentScrollViewer = FindAncestorScrollViewer(source);
            SetCachedAncestorScrollViewer(source, parentScrollViewer);
        }

        if (parentScrollViewer == null)
        {
            return;
        }

        var wheelLines = SystemParameters.WheelScrollLines > 0 ? SystemParameters.WheelScrollLines : 3;
        var scrollDelta = (e.Delta / Mouse.MouseWheelDeltaForOneLine) * wheelLines * 24d;
        parentScrollViewer.ScrollToVerticalOffset(parentScrollViewer.VerticalOffset - scrollDelta);
        e.Handled = true;
    }

    private static bool IsDescendantOrSelf(DependencyObject source, DependencyObject target)
    {
        var current = target;
        while (current is not null)
        {
            if (ReferenceEquals(current, source))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static bool IsAncestorOf(DependencyObject source, DependencyObject ancestor)
    {
        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static ScrollViewer? GetCachedDescendantScrollViewer(DependencyObject source)
    {
        return source.GetValue(CachedDescendantScrollViewerProperty) as ScrollViewer;
    }

    private static void SetCachedDescendantScrollViewer(DependencyObject source, ScrollViewer? scrollViewer)
    {
        source.SetValue(CachedDescendantScrollViewerProperty, scrollViewer);
    }

    private static ScrollViewer? GetCachedAncestorScrollViewer(DependencyObject source)
    {
        return source.GetValue(CachedAncestorScrollViewerProperty) as ScrollViewer;
    }

    private static void SetCachedAncestorScrollViewer(DependencyObject source, ScrollViewer? scrollViewer)
    {
        source.SetValue(CachedAncestorScrollViewerProperty, scrollViewer);
    }

    private static void ClearCachedScrollViewers(DependencyObject source)
    {
        source.ClearValue(CachedDescendantScrollViewerProperty);
        source.ClearValue(CachedAncestorScrollViewerProperty);
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
        {
            return viewer;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var result = FindDescendantScrollViewer(child);
            if (result != null)
            {
                return result;
            }
        }

        return null;
    }

    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject source)
    {
        var current = VisualTreeHelper.GetParent(source);
        while (current != null)
        {
            if (current is ScrollViewer viewer)
            {
                return viewer;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
