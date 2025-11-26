using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TidyWindow.App.Controls;

public sealed class AdaptiveTilePanel : System.Windows.Controls.Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth),
        typeof(double),
        typeof(AdaptiveTilePanel),
        new FrameworkPropertyMetadata(280d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnWidthProperty = DependencyProperty.Register(
        nameof(MaxColumnWidth),
        typeof(double),
        typeof(AdaptiveTilePanel),
        new FrameworkPropertyMetadata(420d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MinColumnsProperty = DependencyProperty.Register(
        nameof(MinColumns),
        typeof(int),
        typeof(AdaptiveTilePanel),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns),
        typeof(int),
        typeof(AdaptiveTilePanel),
        new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ColumnSpacingProperty = DependencyProperty.Register(
        nameof(ColumnSpacing),
        typeof(double),
        typeof(AdaptiveTilePanel),
        new FrameworkPropertyMetadata(24d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(
        nameof(RowSpacing),
        typeof(double),
        typeof(AdaptiveTilePanel),
        new FrameworkPropertyMetadata(24d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public double MaxColumnWidth
    {
        get => (double)GetValue(MaxColumnWidthProperty);
        set => SetValue(MaxColumnWidthProperty, value);
    }

    public int MinColumns
    {
        get => (int)GetValue(MinColumnsProperty);
        set => SetValue(MinColumnsProperty, value);
    }

    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    public double ColumnSpacing
    {
        get => (double)GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    private readonly List<double> _rowHeights = new();
    private ItemsControl? _itemsOwner;

    protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize)
    {
        EnsureOwnerSubscription();
        var viewportWidth = ResolveViewportWidth(availableSize);
        var (columns, tileWidth) = CalculateLayout(viewportWidth);
        if (columns == 0)
        {
            return System.Windows.Size.Empty;
        }

        _rowHeights.Clear();
        var childConstraint = new System.Windows.Size(tileWidth, double.PositiveInfinity);
        var columnIndex = 0;
        var currentRowHeight = 0d;

        foreach (UIElement child in InternalChildren)
        {
            if (child == null)
            {
                continue;
            }

            child.Measure(childConstraint);
            currentRowHeight = Math.Max(currentRowHeight, child.DesiredSize.Height);
            columnIndex++;

            if (columnIndex == columns)
            {
                _rowHeights.Add(currentRowHeight);
                columnIndex = 0;
                currentRowHeight = 0;
            }
        }

        if (columnIndex > 0)
        {
            _rowHeights.Add(currentRowHeight);
        }

        var totalHeight = 0d;
        for (var i = 0; i < _rowHeights.Count; i++)
        {
            totalHeight += _rowHeights[i];
            if (i < _rowHeights.Count - 1)
            {
                totalHeight += RowSpacing;
            }
        }

        return new System.Windows.Size(viewportWidth, totalHeight);
    }

    protected override System.Windows.Size ArrangeOverride(System.Windows.Size finalSize)
    {
        var viewportWidth = ResolveViewportWidth(finalSize);
        var (columns, tileWidth) = CalculateLayout(viewportWidth);
        if (columns == 0)
        {
            return finalSize;
        }

        if (_rowHeights.Count == 0)
        {
            MeasureOverride(finalSize);
        }

        var columnIndex = 0;
        var rowIndex = 0;
        var x = 0d;
        var y = 0d;

        foreach (UIElement child in InternalChildren)
        {
            if (child == null)
            {
                continue;
            }

            var rowHeight = rowIndex < _rowHeights.Count ? _rowHeights[rowIndex] : child.DesiredSize.Height;
            child.Arrange(new Rect(x, y, tileWidth, rowHeight));

            columnIndex++;
            if (columnIndex == columns)
            {
                columnIndex = 0;
                rowIndex++;
                x = 0;
                y += rowHeight + RowSpacing;
            }
            else
            {
                x += tileWidth + ColumnSpacing;
            }
        }

        return finalSize;
    }

    private (int columns, double tileWidth) CalculateLayout(double availableWidth)
    {
        var width = double.IsNaN(availableWidth) || availableWidth <= 0 ? MinColumnWidth : availableWidth;
        var minWidth = Math.Max(60d, MinColumnWidth);
        var maxWidth = Math.Max(minWidth, MaxColumnWidth);

        var minColumns = Math.Max(1, MinColumns);
        var maxColumns = Math.Max(minColumns, MaxColumns);

        var estimatedColumns = Math.Max(minColumns, (int)Math.Floor((width + ColumnSpacing) / (minWidth + ColumnSpacing)));
        var columns = Math.Min(maxColumns, Math.Max(minColumns, estimatedColumns));
        columns = Math.Max(1, columns);

        double tileWidth;
        while (true)
        {
            tileWidth = ComputeTileWidth(width, columns);
            if (tileWidth >= minWidth || columns <= 1)
            {
                break;
            }

            columns = Math.Max(1, columns - 1);
        }

        while (tileWidth > maxWidth && columns < maxColumns)
        {
            columns++;
            tileWidth = ComputeTileWidth(width, columns);
        }

        tileWidth = Math.Max(minWidth, Math.Min(maxWidth, tileWidth));
        return (columns, tileWidth);
    }

    private double ComputeTileWidth(double width, int columns)
    {
        var spacing = ColumnSpacing * Math.Max(0, columns - 1);
        var usableWidth = Math.Max(width - spacing, MinColumnWidth);
        return usableWidth / Math.Max(1, columns);
    }

    private double ResolveViewportWidth(System.Windows.Size size)
    {
        if (!double.IsNaN(size.Width) && !double.IsInfinity(size.Width) && size.Width > 0)
        {
            return size.Width;
        }

        if (Parent is FrameworkElement parent && parent.ActualWidth > 0)
        {
            return parent.ActualWidth;
        }

        if (_itemsOwner?.ActualWidth > 0)
        {
            return _itemsOwner.ActualWidth;
        }

        var ancestorWidth = FindAncestorWidth(this);
        if (ancestorWidth > 0)
        {
            return ancestorWidth;
        }

        if (ActualWidth > 0)
        {
            return ActualWidth;
        }

        return MinColumnWidth;
    }

    private static double FindAncestorWidth(DependencyObject source)
    {
        var current = source;
        while (current != null)
        {
            if (current is FrameworkElement element && element.ActualWidth > 0)
            {
                return element.ActualWidth;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return double.NaN;
    }

    private void EnsureOwnerSubscription()
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (ReferenceEquals(owner, _itemsOwner))
        {
            return;
        }

        if (_itemsOwner != null)
        {
            _itemsOwner.SizeChanged -= OnOwnerSizeChanged;
        }

        _itemsOwner = owner;
        if (_itemsOwner != null)
        {
            _itemsOwner.SizeChanged += OnOwnerSizeChanged;
        }
    }

    private void OnOwnerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            InvalidateMeasure();
        }
    }
}
