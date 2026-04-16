using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TidyWindow.App.Controls;

/// <summary>
/// ListView that forwards mouse-wheel events to a parent ScrollViewer when it
/// can no longer scroll, preventing nested scroll dead-ends.
/// </summary>
public class BubbleScrollListView : System.Windows.Controls.ListView
{
    private const double WheelScrollPixelsPerLine = 24d;

    public static readonly DependencyProperty ParentScrollViewerNameProperty = DependencyProperty.Register(
        nameof(ParentScrollViewerName),
        typeof(string),
        typeof(BubbleScrollListView),
        new PropertyMetadata(null, OnParentScrollViewerNameChanged));

    private ScrollViewer? _innerScrollViewer;
    private ScrollViewer? _parentScrollViewer;

    public BubbleScrollListView()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public string? ParentScrollViewerName
    {
        get => (string?)GetValue(ParentScrollViewerNameProperty);
        set => SetValue(ParentScrollViewerNameProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _innerScrollViewer = null;
        EnsureInnerScrollViewer();
    }

    protected override void OnPreviewMouseWheel(System.Windows.Input.MouseWheelEventArgs e)
    {
        var inner = EnsureInnerScrollViewer();
        if (inner is not null && !IsDescendantOrSelf(this, inner))
        {
            _innerScrollViewer = null;
            inner = EnsureInnerScrollViewer();
        }

        var canScrollInner = false;

        if (inner is not null)
        {
            canScrollInner = (e.Delta > 0 && inner.VerticalOffset > 0) ||
                             (e.Delta < 0 && inner.VerticalOffset < inner.ScrollableHeight);
        }

        if (canScrollInner)
        {
            base.OnPreviewMouseWheel(e);
            return;
        }

        var parentScrollViewer = ResolveParentScrollViewer();
        if (parentScrollViewer is not null && !IsAncestorOf(this, parentScrollViewer))
        {
            _parentScrollViewer = null;
            parentScrollViewer = ResolveParentScrollViewer();
        }

        if (parentScrollViewer is not null && parentScrollViewer.ScrollableHeight > 0)
        {
            e.Handled = true;
            var wheelLines = SystemParameters.WheelScrollLines > 0 ? SystemParameters.WheelScrollLines : 3;
            var scrollDelta = (e.Delta / Mouse.MouseWheelDeltaForOneLine) * wheelLines * WheelScrollPixelsPerLine;
            var targetOffset = Clamp(parentScrollViewer.VerticalOffset - scrollDelta, 0, parentScrollViewer.ScrollableHeight);
            parentScrollViewer.ScrollToVerticalOffset(targetOffset);
            return;
        }

        base.OnPreviewMouseWheel(e);
    }

    protected override void OnVisualParentChanged(DependencyObject oldParent)
    {
        base.OnVisualParentChanged(oldParent);
        _innerScrollViewer = null;
        _parentScrollViewer = null;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _innerScrollViewer = null;
        _parentScrollViewer = null;
        EnsureInnerScrollViewer();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _innerScrollViewer = null;
        _parentScrollViewer = null;
    }

    private ScrollViewer? EnsureInnerScrollViewer()
    {
        if (_innerScrollViewer is { } cached && cached.IsLoaded)
        {
            return cached;
        }

        _innerScrollViewer = FindDescendant<ScrollViewer>(this);
        return _innerScrollViewer;
    }

    private ScrollViewer? ResolveParentScrollViewer()
    {
        if (_parentScrollViewer is { } cached && cached.IsLoaded)
        {
            return cached;
        }

        var desiredName = ParentScrollViewerName;
        var current = VisualTreeHelper.GetParent(this);
        while (current is not null)
        {
            if (current is ScrollViewer scroll)
            {
                if (string.IsNullOrWhiteSpace(desiredName))
                {
                    _parentScrollViewer = scroll;
                    break;
                }

                if (scroll is FrameworkElement element && string.Equals(element.Name, desiredName, StringComparison.Ordinal))
                {
                    _parentScrollViewer = scroll;
                    break;
                }
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return _parentScrollViewer;
    }

    private static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null)
        {
            return null;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typedChild)
            {
                return typedChild;
            }

            var result = FindDescendant<T>(child);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
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

    private static void OnParentScrollViewerNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is BubbleScrollListView listView)
        {
            listView._parentScrollViewer = null;
        }
    }
}
