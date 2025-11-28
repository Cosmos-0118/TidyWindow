using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfPanel = System.Windows.Controls.Panel;
using WpfSize = System.Windows.Size;

namespace TidyWindow.App.Controls;

public sealed class AdaptiveTilePanel : WpfPanel
{
    private const double MinimumTileWidth = 60d;

    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth),
        typeof(double),
        typeof(AdaptiveTilePanel),
        new FrameworkPropertyMetadata(280d, FrameworkPropertyMetadataOptions.AffectsMeasure, null, CoerceMinColumnWidth));

    public static readonly DependencyProperty MaxColumnWidthProperty = DependencyProperty.Register(
        nameof(MaxColumnWidth),
        typeof(double),
        typeof(AdaptiveTilePanel),
        new FrameworkPropertyMetadata(420d, FrameworkPropertyMetadataOptions.AffectsMeasure, null, CoerceMaxColumnWidth));

    public static readonly DependencyProperty MinColumnsProperty = DependencyProperty.Register(
        nameof(MinColumns),
        typeof(int),
        typeof(AdaptiveTilePanel),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsMeasure, null, CoerceMinColumns));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns),
        typeof(int),
        typeof(AdaptiveTilePanel),
        new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsMeasure, null, CoerceMaxColumns));

    public static readonly DependencyProperty ColumnSpacingProperty = DependencyProperty.Register(
        nameof(ColumnSpacing),
        typeof(double),
        typeof(AdaptiveTilePanel),
        new FrameworkPropertyMetadata(24d, FrameworkPropertyMetadataOptions.AffectsMeasure, null, CoerceNonNegativeDouble));

    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(
        nameof(RowSpacing),
        typeof(double),
        typeof(AdaptiveTilePanel),
        new FrameworkPropertyMetadata(24d, FrameworkPropertyMetadataOptions.AffectsMeasure, null, CoerceNonNegativeDouble));

    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(
        nameof(Padding),
        typeof(Thickness),
        typeof(AdaptiveTilePanel),
        new FrameworkPropertyMetadata(default(Thickness), FrameworkPropertyMetadataOptions.AffectsMeasure, null, CoercePadding));

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

    public Thickness Padding
    {
        get => (Thickness)GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    private readonly List<double> _rowHeights = new();
    private ItemsControl? _itemsOwner;

    protected override WpfSize MeasureOverride(WpfSize availableSize)
    {
        EnsureOwnerSubscription();
        var viewportWidth = ResolveViewportWidth(availableSize);
        var padding = Padding;
        var horizontalPadding = padding.Left + padding.Right;
        var verticalPadding = padding.Top + padding.Bottom;

        var layout = CalculateLayout(Math.Max(0, viewportWidth - horizontalPadding));
        if (layout.Columns == 0)
        {
            return WpfSize.Empty;
        }

        _rowHeights.Clear();
        var childConstraint = new WpfSize(layout.TileWidth, double.PositiveInfinity);
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

            if (columnIndex == layout.Columns)
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

        var totalHeight = verticalPadding + SumHeights(_rowHeights, RowSpacing);
        var desiredWidth = horizontalPadding + layout.TotalWidth;
        double widthToReport;
        if (double.IsInfinity(availableSize.Width))
        {
            widthToReport = Math.Max(desiredWidth, viewportWidth);
        }
        else
        {
            widthToReport = Math.Min(desiredWidth, availableSize.Width);
        }

        return new WpfSize(widthToReport, totalHeight);
    }

    protected override WpfSize ArrangeOverride(WpfSize finalSize)
    {
        var viewportWidth = ResolveViewportWidth(finalSize);
        var padding = Padding;
        var horizontalPadding = padding.Left + padding.Right;
        var verticalPadding = padding.Top + padding.Bottom;
        var layout = CalculateLayout(Math.Max(0, viewportWidth - horizontalPadding));
        if (layout.Columns == 0)
        {
            return finalSize;
        }

        if (_rowHeights.Count == 0)
        {
            MeasureOverride(finalSize);
        }

        var columnIndex = 0;
        var rowIndex = 0;
        var y = padding.Top;
        var rowHeight = 0d;
        var increment = layout.TileWidth + layout.Spacing;
        var inset = layout.Inset;
        var isRightToLeft = FlowDirection == System.Windows.FlowDirection.RightToLeft;
        var rowOriginX = isRightToLeft
            ? padding.Left + inset + Math.Max(0, layout.Columns - 1) * increment
            : padding.Left + inset;
        var x = rowOriginX;

        foreach (UIElement child in InternalChildren)
        {
            if (child == null)
            {
                continue;
            }

            rowHeight = rowIndex < _rowHeights.Count ? _rowHeights[rowIndex] : child.DesiredSize.Height;
            child.Arrange(new Rect(x, y, layout.TileWidth, rowHeight));

            columnIndex++;
            if (columnIndex == layout.Columns)
            {
                columnIndex = 0;
                rowIndex++;
                x = rowOriginX;
                y += rowHeight + RowSpacing;
            }
            else
            {
                var delta = layout.TileWidth + layout.Spacing;
                x += isRightToLeft ? -delta : delta;
            }
        }

        return finalSize;
    }

    private LayoutResult CalculateLayout(double availableWidth)
    {
        var width = double.IsNaN(availableWidth) || availableWidth <= 0 ? MinColumnWidth : availableWidth;
        var minWidth = Math.Max(MinimumTileWidth, MinColumnWidth);
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

        var spacing = ColumnSpacing;
        var inset = 0d;
        var usedWidth = columns * tileWidth + Math.Max(0, columns - 1) * spacing;
        var leftover = Math.Max(0, width - usedWidth);
        if (columns > 0 && leftover > 0)
        {
            var smartGap = leftover / (columns + 1);
            inset = smartGap;
            spacing += smartGap;
            usedWidth = columns * tileWidth + Math.Max(0, columns - 1) * spacing;
        }

        var totalWidth = usedWidth + inset * 2;

        return new LayoutResult(columns, tileWidth, spacing, inset, totalWidth);
    }

    private readonly struct LayoutResult
    {
        public LayoutResult(int columns, double tileWidth, double spacing, double inset, double totalWidth)
        {
            Columns = columns;
            TileWidth = tileWidth;
            Spacing = spacing;
            Inset = inset;
            TotalWidth = totalWidth;
        }

        public int Columns { get; }

        public double TileWidth { get; }

        public double Spacing { get; }

        public double Inset { get; }

        public double TotalWidth { get; }
    }

    private double ComputeTileWidth(double width, int columns)
    {
        var spacing = ColumnSpacing * Math.Max(0, columns - 1);
        var usableWidth = Math.Max(width - spacing, MinColumnWidth);
        return usableWidth / Math.Max(1, columns);
    }

    private double ResolveViewportWidth(WpfSize size)
    {
        if (!double.IsNaN(size.Width) && !double.IsInfinity(size.Width) && size.Width > 0)
        {
            return size.Width;
        }

        var scrollViewerWidth = FindScrollViewerWidth(this);
        if (scrollViewerWidth > 0)
        {
            return scrollViewerWidth;
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

    private static double FindScrollViewerWidth(DependencyObject source)
    {
        var current = source;
        while (current != null)
        {
            if (current is ScrollViewer scrollViewer)
            {
                if (scrollViewer.ViewportWidth > 0)
                {
                    return scrollViewer.ViewportWidth;
                }

                if (scrollViewer.ActualWidth > 0)
                {
                    return scrollViewer.ActualWidth;
                }
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return double.NaN;
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

    private static object CoerceNonNegativeDouble(DependencyObject d, object baseValue)
    {
        if (baseValue is double value && !double.IsNaN(value) && !double.IsInfinity(value))
        {
            return Math.Max(0d, value);
        }

        return 0d;
    }

    private static object CoerceMinColumnWidth(DependencyObject d, object baseValue)
    {
        var value = (double)CoerceNonNegativeDouble(d, baseValue);
        return Math.Max(MinimumTileWidth, value);
    }

    private static object CoerceMaxColumnWidth(DependencyObject d, object baseValue)
    {
        var panel = (AdaptiveTilePanel)d;
        var value = (double)CoerceNonNegativeDouble(d, baseValue);
        return Math.Max(panel.MinColumnWidth, value);
    }

    private static object CoerceMinColumns(DependencyObject d, object baseValue)
    {
        if (baseValue is int value)
        {
            return Math.Max(1, value);
        }

        return 1;
    }

    private static object CoerceMaxColumns(DependencyObject d, object baseValue)
    {
        if (baseValue is not int value)
        {
            return ((AdaptiveTilePanel)d).MinColumns;
        }

        var panel = (AdaptiveTilePanel)d;
        return Math.Max(panel.MinColumns, value);
    }

    private static object CoercePadding(DependencyObject d, object baseValue)
    {
        if (baseValue is Thickness thickness)
        {
            return new Thickness(
                Math.Max(0, DoubleOrZero(thickness.Left)),
                Math.Max(0, DoubleOrZero(thickness.Top)),
                Math.Max(0, DoubleOrZero(thickness.Right)),
                Math.Max(0, DoubleOrZero(thickness.Bottom)));
        }

        return new Thickness();
    }

    private static double DoubleOrZero(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value) ? 0d : value;
    }

    private static double SumHeights(IReadOnlyList<double> heights, double spacing)
    {
        if (heights.Count == 0)
        {
            return 0d;
        }

        var total = 0d;
        for (var i = 0; i < heights.Count; i++)
        {
            total += heights[i];
            if (i < heights.Count - 1)
            {
                total += spacing;
            }
        }

        return total;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.Property == MinColumnWidthProperty)
        {
            CoerceValue(MaxColumnWidthProperty);
        }
        else if (e.Property == MinColumnsProperty)
        {
            CoerceValue(MaxColumnsProperty);
        }
        else if (e.Property == FlowDirectionProperty)
        {
            InvalidateArrange();
        }
    }

    protected override void OnVisualParentChanged(DependencyObject oldParent)
    {
        base.OnVisualParentChanged(oldParent);
        EnsureOwnerSubscription();
    }
}
