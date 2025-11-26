using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TidyWindow.App.Infrastructure;

public static class ScrollHost
{
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
        }
        else
        {
            element.PreviewMouseWheel -= OnElementPreviewMouseWheel;
        }
    }

    private static void OnElementPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject source)
        {
            return;
        }

        var innerScrollViewer = FindDescendantScrollViewer(source);
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

        var parentScrollViewer = FindAncestorScrollViewer(source);
        if (parentScrollViewer == null)
        {
            return;
        }

        parentScrollViewer.ScrollToVerticalOffset(parentScrollViewer.VerticalOffset - e.Delta);
        e.Handled = true;
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
