using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlowDirection = System.Windows.FlowDirection;
using TextBox = System.Windows.Controls.TextBox;

namespace TidyWindow.App.Behaviors;

/// <summary>
/// Attached behavior that ensures mouse wheel events are always bubbled up to the parent ScrollViewer,
/// preventing controls like TextBox, ComboBox, Expander, and other focusable elements from swallowing scroll events.
/// </summary>
public static class ScrollBubbleBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(ScrollBubbleBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element)
    {
        return element is not null && (bool)element.GetValue(IsEnabledProperty);
    }

    public static void SetIsEnabled(DependencyObject element, bool value)
    {
        element?.SetValue(IsEnabledProperty, value);
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            element.PreviewMouseWheel += OnPreviewMouseWheel;
        }
        else
        {
            element.PreviewMouseWheel -= OnPreviewMouseWheel;
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0)
        {
            return;
        }

        if (sender is not DependencyObject senderElement)
        {
            return;
        }

        // Find the original source to check if it's inside a nested scrollable control
        if (e.OriginalSource is DependencyObject source)
        {
            // Check if source is inside an editable TextBox with text that overflows
            var textBox = FindParent<TextBox>(source);
            if (textBox is not null && textBox.IsFocused && HasHorizontalOverflow(textBox))
            {
                // Let TextBox handle horizontal scroll when it has overflow and is focused
                return;
            }

            // Check for nested scrollable controls that should handle their own scroll
            var nestedScroller = FindParent<ScrollViewer>(source);
            if (nestedScroller is not null)
            {
                // Check if nested scroller is inside the main page scroller
                var pageScroller = FindParent<ScrollViewer>(nestedScroller);
                if (pageScroller is not null && CanNestedScroll(nestedScroller, e.Delta))
                {
                    // Nested scroller can still scroll in this direction, let it handle it
                    return;
                }
            }
        }

        // Find the parent ScrollViewer
        var parentScrollViewer = FindParent<ScrollViewer>(senderElement);
        if (parentScrollViewer is null)
        {
            return;
        }

        // Create a new mouse wheel event and raise it on the parent ScrollViewer
        var eventArgs = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = sender
        };

        parentScrollViewer.RaiseEvent(eventArgs);
        e.Handled = true;
    }

    private static bool CanNestedScroll(ScrollViewer nested, int wheelDelta)
    {
        // No scrollable content
        if (nested.ScrollableHeight <= 0.5 && nested.ScrollableWidth <= 0.5)
        {
            return false;
        }

        // Check vertical scrollability
        if (nested.ScrollableHeight > 0.5)
        {
            // Scrolling up (positive delta) - can scroll if not at top
            if (wheelDelta > 0 && nested.VerticalOffset > 0.5)
            {
                return true;
            }

            // Scrolling down (negative delta) - can scroll if not at bottom
            if (wheelDelta < 0 && nested.VerticalOffset < nested.ScrollableHeight - 0.5)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasHorizontalOverflow(TextBox textBox)
    {
        if (textBox.TextWrapping != TextWrapping.NoWrap)
        {
            return false;
        }

        // Approximation: if text is longer than the visible width, there's overflow
        var formattedText = new FormattedText(
            textBox.Text ?? string.Empty,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(textBox.FontFamily, textBox.FontStyle, textBox.FontWeight, textBox.FontStretch),
            textBox.FontSize,
            textBox.Foreground,
            VisualTreeHelper.GetDpi(textBox).PixelsPerDip);

        return formattedText.Width > textBox.ActualWidth - textBox.Padding.Left - textBox.Padding.Right;
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T found)
            {
                return found;
            }

            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }
}
