using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;

namespace TidyWindow.App.Controls;

public sealed class VirtualizingAdaptiveTilePanel : VirtualizingPanel, IScrollInfo
{
    private const double MinimumTileWidth = 60d;
    private const int CacheRowCount = 2;
    private const double DefaultRowHeight = 260d;
    private const double MinRowHeight = 120d;
    private const double ScrollLineDelta = 48d;

    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth),
        typeof(double),
        typeof(VirtualizingAdaptiveTilePanel),
        new FrameworkPropertyMetadata(280d, FrameworkPropertyMetadataOptions.AffectsMeasure, null, CoerceMinColumnWidth));

    public static readonly DependencyProperty MaxColumnWidthProperty = DependencyProperty.Register(
        nameof(MaxColumnWidth),
        typeof(double),
        typeof(VirtualizingAdaptiveTilePanel),
        new FrameworkPropertyMetadata(420d, FrameworkPropertyMetadataOptions.AffectsMeasure, null, CoerceMaxColumnWidth));

    public static readonly DependencyProperty MinColumnsProperty = DependencyProperty.Register(
        nameof(MinColumns),
        typeof(int),
        typeof(VirtualizingAdaptiveTilePanel),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsMeasure, null, CoerceMinColumns));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns),
        typeof(int),
        typeof(VirtualizingAdaptiveTilePanel),
        new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsMeasure, null, CoerceMaxColumns));

    public static readonly DependencyProperty ColumnSpacingProperty = DependencyProperty.Register(
        nameof(ColumnSpacing),
        typeof(double),
        typeof(VirtualizingAdaptiveTilePanel),
        new FrameworkPropertyMetadata(24d, FrameworkPropertyMetadataOptions.AffectsMeasure, null, CoerceNonNegativeDouble));

    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(
        nameof(RowSpacing),
        typeof(double),
        typeof(VirtualizingAdaptiveTilePanel),
        new FrameworkPropertyMetadata(24d, FrameworkPropertyMetadataOptions.AffectsMeasure, null, CoerceNonNegativeDouble));

    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(
        nameof(Padding),
        typeof(Thickness),
        typeof(VirtualizingAdaptiveTilePanel),
        new FrameworkPropertyMetadata(default(Thickness), FrameworkPropertyMetadataOptions.AffectsMeasure, null, CoercePadding));

    private readonly Dictionary<UIElement, int> _childIndexLookup = new();
    private readonly Dictionary<int, double> _rowHeightCache = new();
    private ItemsControl? _itemsOwner;
    private WpfSize _extent;
    private WpfSize _viewport;
    private WpfPoint _offset;
    private bool _canHorizontallyScroll;
    private bool _canVerticallyScroll = true;
    private double _averageRowHeight = DefaultRowHeight;
    private LayoutResult _lastLayout;
    private bool _hasLayout;

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

    public bool CanHorizontallyScroll
    {
        get => _canHorizontallyScroll;
        set => _canHorizontallyScroll = value;
    }

    public bool CanVerticallyScroll
    {
        get => _canVerticallyScroll;
        set => _canVerticallyScroll = value;
    }

    public double ExtentHeight => _extent.Height;

    public double ExtentWidth => _extent.Width;

    public double ViewportHeight => _viewport.Height;

    public double ViewportWidth => _viewport.Width;

    public double HorizontalOffset => _offset.X;

    public double VerticalOffset => _offset.Y;

    public ScrollViewer? ScrollOwner { get; set; }

    protected override WpfSize MeasureOverride(WpfSize availableSize)
    {
        _itemsOwner ??= ItemsControl.GetItemsOwner(this);
        if (_itemsOwner is null)
        {
            return base.MeasureOverride(availableSize);
        }

        var viewportWidth = ResolveViewportWidth(availableSize);
        var padding = Padding;
        var horizontalPadding = padding.Left + padding.Right;
        var layout = CalculateLayout(Math.Max(0, viewportWidth - horizontalPadding));
        _lastLayout = layout;
        _hasLayout = layout.Columns > 0;

        var viewportHeight = ResolveViewportHeight(availableSize);
        _viewport = new WpfSize(viewportWidth, viewportHeight);

        var itemCount = _itemsOwner.HasItems ? _itemsOwner.Items.Count : 0;
        var range = GetRealizationRange(layout, itemCount);
        CleanUpItems(range.FirstIndex, range.LastIndex);
        if (!range.IsEmpty)
        {
            RealizeRange(range.FirstIndex, range.LastIndex, layout);
        }

        UpdateExtent(layout, itemCount);
        ScrollOwner?.InvalidateScrollInfo();

        return new WpfSize(viewportWidth, Math.Min(_extent.Height, viewportHeight));
    }

    protected override WpfSize ArrangeOverride(WpfSize finalSize)
    {
        if (!_hasLayout)
        {
            return base.ArrangeOverride(finalSize);
        }

        var padding = Padding;
        var layout = _lastLayout;
        var increment = layout.TileWidth + layout.Spacing;
        var isRightToLeft = FlowDirection == System.Windows.FlowDirection.RightToLeft;
        var rowOrigin = isRightToLeft
            ? padding.Left + layout.Inset + Math.Max(0, layout.Columns - 1) * increment
            : padding.Left + layout.Inset;

        foreach (UIElement child in InternalChildren)
        {
            if (!_childIndexLookup.TryGetValue(child, out var itemIndex))
            {
                continue;
            }

            var row = layout.Columns == 0 ? 0 : itemIndex / layout.Columns;
            var column = layout.Columns == 0 ? 0 : itemIndex % layout.Columns;
            var rowHeight = GetRowHeight(row);
            var y = padding.Top + GetRowOffset(row) - _offset.Y;
            var x = isRightToLeft
                ? rowOrigin - column * increment
                : rowOrigin + column * increment;

            child.Arrange(new Rect(x, y, layout.TileWidth, rowHeight));
        }

        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        ResetLayoutState();
        InvalidateMeasure();
    }

    public void LineUp() => SetVerticalOffset(VerticalOffset - GetEstimatedRowHeight() * 0.5);

    public void LineDown() => SetVerticalOffset(VerticalOffset + GetEstimatedRowHeight() * 0.5);

    public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight);

    public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight);

    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - ScrollLineDelta);

    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + ScrollLineDelta);

    public void LineLeft()
    {
    }

    public void LineRight()
    {
    }

    public void PageLeft()
    {
    }

    public void PageRight()
    {
    }

    public void MouseWheelLeft()
    {
    }

    public void MouseWheelRight()
    {
    }

    public void SetHorizontalOffset(double offset)
    {
        if (!_canHorizontallyScroll)
        {
            return;
        }

        var target = Math.Max(0, Math.Min(offset, Math.Max(0, ExtentWidth - ViewportWidth)));
        if (!AreClose(target, _offset.X))
        {
            _offset.X = target;
            InvalidateMeasure();
            ScrollOwner?.InvalidateScrollInfo();
        }
    }

    public void SetVerticalOffset(double offset)
    {
        if (!_canVerticallyScroll)
        {
            return;
        }

        var maxOffset = Math.Max(0, ExtentHeight - ViewportHeight);
        var target = Math.Max(0, Math.Min(offset, maxOffset));
        if (!AreClose(target, _offset.Y))
        {
            _offset.Y = target;
            InvalidateMeasure();
            ScrollOwner?.InvalidateScrollInfo();
        }
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (visual is not UIElement element)
        {
            return rectangle;
        }

        if (!_childIndexLookup.TryGetValue(element, out var index))
        {
            return rectangle;
        }

        var row = _lastLayout.Columns == 0 ? 0 : index / _lastLayout.Columns;
        EnsureRowInView(row);
        return rectangle;
    }

    private void EnsureRowInView(int row)
    {
        var rowStart = Padding.Top + GetRowOffset(row);
        var rowHeight = GetRowHeight(row);

        if (rowStart < VerticalOffset)
        {
            SetVerticalOffset(rowStart);
        }
        else if (rowStart + rowHeight > VerticalOffset + ViewportHeight)
        {
            SetVerticalOffset(rowStart + rowHeight - ViewportHeight);
        }
    }

    private void ResetLayoutState()
    {
        _childIndexLookup.Clear();
        _rowHeightCache.Clear();
        _offset = default;
        _averageRowHeight = DefaultRowHeight;
        _extent = default;
        _viewport = default;
        _hasLayout = false;
    }

    private (int FirstIndex, int LastIndex, bool IsEmpty) GetRealizationRange(LayoutResult layout, int itemCount)
    {
        if (itemCount == 0 || layout.Columns == 0 || ViewportHeight <= 0)
        {
            return (0, -1, true);
        }

        var rowCount = (int)Math.Ceiling(itemCount / (double)layout.Columns);
        var stride = Math.Max(GetEstimatedRowHeight() + RowSpacing, 1d);
        var firstRow = Math.Max(0, (int)Math.Floor(VerticalOffset / stride));
        var lastRow = Math.Min(rowCount - 1, (int)Math.Ceiling((VerticalOffset + ViewportHeight) / stride));

        firstRow = Math.Max(0, firstRow - CacheRowCount);
        lastRow = Math.Min(rowCount - 1, lastRow + CacheRowCount);

        var firstIndex = firstRow * layout.Columns;
        var lastIndex = Math.Min(itemCount - 1, ((lastRow + 1) * layout.Columns) - 1);
        if (lastIndex < firstIndex)
        {
            return (0, -1, true);
        }

        return (firstIndex, lastIndex, false);
    }

    private void CleanUpItems(int minIndex, int maxIndex)
    {
        var generator = ItemContainerGenerator;
        if (maxIndex < minIndex)
        {
            RemoveAllChildren(generator);
            return;
        }

        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var child = InternalChildren[i];
            var hasIndex = _childIndexLookup.TryGetValue(child, out var itemIndex);
            if (!hasIndex || itemIndex < minIndex || itemIndex > maxIndex)
            {
                _childIndexLookup.Remove(child);
                if (hasIndex && itemIndex >= 0)
                {
                    var position = generator.GeneratorPositionFromIndex(itemIndex);
                    if (position.Index >= 0 || position.Offset != 0)
                    {
                        generator.Remove(position, 1);
                    }
                }
                RemoveInternalChildRange(i, 1);
            }
        }
    }

    private void RemoveAllChildren(IItemContainerGenerator generator)
    {
        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var child = InternalChildren[i];
            var hasIndex = _childIndexLookup.TryGetValue(child, out var itemIndex);
            _childIndexLookup.Remove(child);
            if (hasIndex)
            {
                var position = generator.GeneratorPositionFromIndex(itemIndex);
                if (position.Index >= 0 || position.Offset != 0)
                {
                    generator.Remove(position, 1);
                }
            }
            RemoveInternalChildRange(i, 1);
        }
    }

    private void RealizeRange(int firstIndex, int lastIndex, LayoutResult layout)
    {
        if (firstIndex > lastIndex)
        {
            return;
        }

        var generator = ItemContainerGenerator;
        var startPos = generator.GeneratorPositionFromIndex(firstIndex);
        var childIndex = startPos.Offset == 0 ? startPos.Index : startPos.Index + 1;
        var column = firstIndex % layout.Columns;
        var row = firstIndex / layout.Columns;
        var childConstraint = new WpfSize(layout.TileWidth, double.PositiveInfinity);

        using (generator.StartAt(startPos, GeneratorDirection.Forward, true))
        {
            for (var itemIndex = firstIndex; itemIndex <= lastIndex; itemIndex++)
            {
                var child = generator.GenerateNext(out var newlyRealized) as UIElement;
                if (child == null)
                {
                    continue;
                }

                if (newlyRealized)
                {
                    if (childIndex >= InternalChildren.Count)
                    {
                        AddInternalChild(child);
                    }
                    else
                    {
                        InsertInternalChild(childIndex, child);
                    }

                    generator.PrepareItemContainer(child);
                }
                else
                {
                    var currentIndex = InternalChildren.IndexOf(child);
                    if (currentIndex != childIndex)
                    {
                        if (currentIndex >= 0)
                        {
                            RemoveInternalChildRange(currentIndex, 1);
                            if (currentIndex < childIndex)
                            {
                                childIndex--;
                            }
                        }

                        if (childIndex >= InternalChildren.Count)
                        {
                            AddInternalChild(child);
                        }
                        else
                        {
                            InsertInternalChild(childIndex, child);
                        }
                    }
                }

                child.Measure(childConstraint);
                _childIndexLookup[child] = itemIndex;
                UpdateRowHeight(row, child.DesiredSize.Height);

                childIndex++;
                column++;
                if (column == layout.Columns)
                {
                    column = 0;
                    row++;
                }
            }
        }
    }

    private void UpdateRowHeight(int row, double height)
    {
        var sanitizedHeight = Math.Max(MinRowHeight, height);
        if (_rowHeightCache.TryGetValue(row, out var existing))
        {
            sanitizedHeight = Math.Max(existing, sanitizedHeight);
        }

        _rowHeightCache[row] = sanitizedHeight;

        var sum = 0d;
        foreach (var value in _rowHeightCache.Values)
        {
            sum += value;
        }

        if (_rowHeightCache.Count > 0)
        {
            _averageRowHeight = Math.Max(MinRowHeight, sum / _rowHeightCache.Count);
        }
    }

    private double GetRowHeight(int row)
    {
        return _rowHeightCache.TryGetValue(row, out var height) ? height : GetEstimatedRowHeight();
    }

    private double GetRowOffset(int row)
    {
        if (row <= 0)
        {
            return 0;
        }

        var offset = 0d;
        for (var i = 0; i < row; i++)
        {
            offset += GetRowHeight(i) + RowSpacing;
        }

        return offset;
    }

    private void UpdateExtent(LayoutResult layout, int itemCount)
    {
        var padding = Padding;
        var horizontalPadding = padding.Left + padding.Right;
        var verticalPadding = padding.Top + padding.Bottom;
        var columns = Math.Max(1, layout.Columns);
        var rowCount = columns == 0 ? 0 : (int)Math.Ceiling(itemCount / (double)columns);
        var rowHeight = GetEstimatedRowHeight();
        var totalHeight = rowCount == 0
            ? verticalPadding
            : verticalPadding + rowCount * rowHeight + Math.Max(0, rowCount - 1) * RowSpacing;
        var totalWidth = horizontalPadding + layout.TotalWidth;
        var newExtent = new WpfSize(Math.Max(totalWidth, _viewport.Width), Math.Max(totalHeight, _viewport.Height));

        if (!AreClose(newExtent.Width, _extent.Width) || !AreClose(newExtent.Height, _extent.Height))
        {
            _extent = newExtent;
            ScrollOwner?.InvalidateScrollInfo();
        }
    }

    private double GetEstimatedRowHeight()
    {
        return Math.Max(MinRowHeight, _averageRowHeight);
    }

    private double ResolveViewportWidth(WpfSize availableSize)
    {
        if (!double.IsNaN(availableSize.Width) && !double.IsInfinity(availableSize.Width) && availableSize.Width > 0)
        {
            return availableSize.Width;
        }

        if (ScrollOwner?.ViewportWidth > 0)
        {
            return ScrollOwner.ViewportWidth;
        }

        if (ActualWidth > 0)
        {
            return ActualWidth;
        }

        if (Parent is FrameworkElement parent && parent.ActualWidth > 0)
        {
            return parent.ActualWidth;
        }

        return MinColumnWidth * Math.Max(1, MinColumns);
    }

    private double ResolveViewportHeight(WpfSize availableSize)
    {
        if (!double.IsNaN(availableSize.Height) && !double.IsInfinity(availableSize.Height) && availableSize.Height > 0)
        {
            return availableSize.Height;
        }

        if (ScrollOwner?.ViewportHeight > 0)
        {
            return ScrollOwner.ViewportHeight;
        }

        if (ActualHeight > 0)
        {
            return ActualHeight;
        }

        return DefaultRowHeight * 3;
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

        if (columns > 0 && leftover > 0 && tileWidth < maxWidth)
        {
            var growthPerTile = leftover / columns;
            var maxGrowth = Math.Max(0, maxWidth - tileWidth);
            var appliedGrowth = Math.Min(growthPerTile, maxGrowth);
            if (appliedGrowth > 0)
            {
                tileWidth += appliedGrowth;
                usedWidth = columns * tileWidth + Math.Max(0, columns - 1) * spacing;
                leftover = Math.Max(0, width - usedWidth);
            }
        }

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

    private double ComputeTileWidth(double width, int columns)
    {
        var spacing = ColumnSpacing * Math.Max(0, columns - 1);
        var usableWidth = Math.Max(width - spacing, MinColumnWidth);
        return usableWidth / Math.Max(1, columns);
    }

    private static object CoerceMinColumnWidth(DependencyObject d, object baseValue)
    {
        var value = (double)baseValue;
        return double.IsNaN(value) || value < MinimumTileWidth ? MinimumTileWidth : value;
    }

    private static object CoerceMaxColumnWidth(DependencyObject d, object baseValue)
    {
        var panel = (VirtualizingAdaptiveTilePanel)d;
        var minWidth = panel.MinColumnWidth;
        var value = (double)baseValue;
        if (double.IsNaN(value) || value < minWidth)
        {
            return minWidth;
        }

        return value;
    }

    private static object CoerceMinColumns(DependencyObject d, object baseValue)
    {
        var value = (int)baseValue;
        return Math.Max(1, value);
    }

    private static object CoerceMaxColumns(DependencyObject d, object baseValue)
    {
        var panel = (VirtualizingAdaptiveTilePanel)d;
        var minColumns = panel.MinColumns;
        var value = (int)baseValue;
        return Math.Max(minColumns, value);
    }

    private static object CoerceNonNegativeDouble(DependencyObject d, object baseValue)
    {
        var value = (double)baseValue;
        if (double.IsNaN(value) || value < 0)
        {
            return 0d;
        }

        return value;
    }

    private static object CoercePadding(DependencyObject d, object baseValue)
    {
        return baseValue is Thickness thickness ? thickness : default(Thickness);
    }

    private static bool AreClose(double left, double right)
    {
        return Math.Abs(left - right) < 0.1;
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
}
