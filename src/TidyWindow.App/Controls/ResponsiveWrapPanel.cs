using System;
using System.Windows;
using System.Windows.Controls;
using PanelOrientation = System.Windows.Controls.Orientation;

namespace TidyWindow.App.Controls;

public sealed class ResponsiveWrapPanel : WrapPanel
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth),
        typeof(double),
        typeof(ResponsiveWrapPanel),
        new PropertyMetadata(280d, OnSizingPropertyChanged));

    public static readonly DependencyProperty MaxItemWidthProperty = DependencyProperty.Register(
        nameof(MaxItemWidth),
        typeof(double),
        typeof(ResponsiveWrapPanel),
        new PropertyMetadata(420d, OnSizingPropertyChanged));

    public static readonly DependencyProperty HorizontalSpacingProperty = DependencyProperty.Register(
        nameof(HorizontalSpacing),
        typeof(double),
        typeof(ResponsiveWrapPanel),
        new PropertyMetadata(24d, OnSizingPropertyChanged));

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double MaxItemWidth
    {
        get => (double)GetValue(MaxItemWidthProperty);
        set => SetValue(MaxItemWidthProperty, value);
    }

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize)
    {
        UpdateItemWidth(availableSize);
        return base.MeasureOverride(availableSize);
    }

    protected override System.Windows.Size ArrangeOverride(System.Windows.Size finalSize)
    {
        UpdateItemWidth(finalSize);
        return base.ArrangeOverride(finalSize);
    }

    private static void OnSizingPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ResponsiveWrapPanel panel)
        {
            panel.InvalidateMeasure();
        }
    }

    private void UpdateItemWidth(System.Windows.Size size)
    {
        if (Orientation == PanelOrientation.Vertical)
        {
            return;
        }

        var viewportWidth = ResolveViewportWidth(size);
        if (viewportWidth <= 0)
        {
            return;
        }

        var columns = ResolveColumnCount(viewportWidth);
        var spacing = Math.Max(0, HorizontalSpacing);
        var widthForCards = viewportWidth - Math.Max(0, columns - 1) * spacing;
        var targetWidth = widthForCards / columns;

        targetWidth = Math.Max(MinItemWidth, Math.Min(MaxItemWidth, targetWidth));
        if (double.IsNaN(targetWidth) || double.IsInfinity(targetWidth))
        {
            return;
        }

        ItemWidth = targetWidth;
    }

    private double ResolveViewportWidth(System.Windows.Size size)
    {
        var width = size.Width;
        if (!double.IsNaN(width) && !double.IsInfinity(width) && width > 0)
        {
            return width;
        }

        if (Parent is FrameworkElement parent && parent.ActualWidth > 0)
        {
            return parent.ActualWidth;
        }

        return ActualWidth > 0 ? ActualWidth : MinItemWidth;
    }

    private static int ResolveColumnCount(double width)
    {
        if (width >= 1480)
        {
            return 4;
        }

        if (width >= 1024)
        {
            return 3;
        }

        if (width >= 640)
        {
            return 2;
        }

        return 1;
    }
}
