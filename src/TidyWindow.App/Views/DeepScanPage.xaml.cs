using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.ViewModels;
using Forms = System.Windows.Forms;
using System.Windows.Media;
using WpfListView = System.Windows.Controls.ListView;

namespace TidyWindow.App.Views;

public partial class DeepScanPage : Page
{
    private readonly DeepScanViewModel _viewModel;
    private WpfListView? _findingsListView;
    private ScrollViewer? _findingsScrollViewer;
    private GridViewColumn? _itemColumn;
    private GridViewColumn? _typeColumn;
    private GridViewColumn? _categoryColumn;
    private GridViewColumn? _sizeColumn;
    private GridViewColumn? _modifiedColumn;
    private GridViewColumn? _pathColumn;
    private GridViewColumn? _actionsColumn;
    private bool _disposed;
    private bool _scrollHandlersAttached;

    private const double ItemPreferredWidth = 320d;
    private const double ItemCompactWidth = 260d;
    private const double ItemMinimumWidth = 210d;

    private const double TypePreferredWidth = 100d;
    private const double TypeCompactWidth = 84d;
    private const double TypeMinimumWidth = 70d;

    private const double CategoryPreferredWidth = 140d;
    private const double CategoryCompactWidth = 120d;
    private const double CategoryMinimumWidth = 100d;

    private const double SizePreferredWidth = 140d;
    private const double SizeCompactWidth = 120d;
    private const double SizeMinimumWidth = 100d;

    private const double ModifiedPreferredWidth = 180d;
    private const double ModifiedCompactWidth = 150d;
    private const double ModifiedMinimumWidth = 130d;

    private const double PathPreferredWidth = 260d;
    private const double PathCompactWidth = 220d;
    private const double PathMinimumWidth = 180d;

    private const double ActionsPreferredWidth = 140d;
    private const double ActionsCompactWidth = 120d;
    private const double ActionsMinimumWidth = 110d;

    private const double LayoutPadding = 56d;

    public DeepScanPage(DeepScanViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        Loaded += Page_OnLoaded;
        Unloaded += OnPageUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private async void Page_OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= Page_OnLoaded;
        EnsureScrollHandlers();

        if (_viewModel.RefreshCommand is IAsyncRelayCommand asyncCommand)
        {
            await asyncCommand.ExecuteAsync(null);
        }
        else
        {
            _viewModel.RefreshCommand.Execute(null);
        }
    }

    private void BrowseButton_OnClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            SelectedPath = _viewModel.TargetPath,
            ShowNewFolderButton = false,
            Description = "Select a folder to scan"
        };

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            _viewModel.TargetPath = dialog.SelectedPath;
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (_findingsListView is not null)
        {
            _findingsListView.Loaded -= FindingsListView_Loaded;
            _findingsListView.SizeChanged -= FindingsListView_SizeChanged;
        }

        DetachScrollHandlers();

        Unloaded -= OnPageUnloaded;
        _disposed = true;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible && _disposed)
        {
            RestorePageBindings();
        }
    }

    private void RestorePageBindings()
    {
        if (_findingsListView is not null)
        {
            _findingsListView.Loaded += FindingsListView_Loaded;
            _findingsListView.SizeChanged += FindingsListView_SizeChanged;
        }

        Unloaded += OnPageUnloaded;
        _disposed = false;
        EnsureScrollHandlers();
        UpdateColumnWidths();
    }

    private void FindingsListView_Loaded(object sender, RoutedEventArgs e)
    {
        _findingsListView ??= sender as WpfListView;
        if (_findingsListView is null)
        {
            return;
        }

        _findingsScrollViewer ??= FindDescendant<ScrollViewer>(_findingsListView);
        CacheColumns();
        UpdateColumnWidths();
        EnsureScrollHandlers();
    }

    private void FindingsListView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            UpdateColumnWidths();
        }
    }

    private void CacheColumns()
    {
        if (_findingsListView?.View is not GridView gridView)
        {
            return;
        }

        if (gridView.Columns.Count >= 7)
        {
            _itemColumn = gridView.Columns[0];
            _typeColumn = gridView.Columns[1];
            _categoryColumn = gridView.Columns[2];
            _sizeColumn = gridView.Columns[3];
            _modifiedColumn = gridView.Columns[4];
            _pathColumn = gridView.Columns[5];
            _actionsColumn = gridView.Columns[6];
        }
    }

    private void UpdateColumnWidths()
    {
        if (_findingsListView is null || _itemColumn is null)
        {
            return;
        }

        var availableWidth = _findingsListView.ActualWidth;
        if (double.IsNaN(availableWidth) || availableWidth <= 0)
        {
            return;
        }

        var workingWidth = Math.Max(0d, availableWidth - LayoutPadding);

        if (_findingsScrollViewer?.ComputedVerticalScrollBarVisibility == Visibility.Visible)
        {
            workingWidth = Math.Max(0d, workingWidth - SystemParameters.VerticalScrollBarWidth);
        }

        var itemWidth = ItemPreferredWidth;
        var typeWidth = TypePreferredWidth;
        var categoryWidth = CategoryPreferredWidth;
        var sizeWidth = SizePreferredWidth;
        var modifiedWidth = ModifiedPreferredWidth;
        var pathWidth = PathPreferredWidth;
        var actionsWidth = ActionsPreferredWidth;

        var preferredTotal = itemWidth + typeWidth + categoryWidth + sizeWidth + modifiedWidth + pathWidth + actionsWidth;

        if (workingWidth > preferredTotal)
        {
            itemWidth += workingWidth - preferredTotal;
        }
        else
        {
            var overflow = preferredTotal - workingWidth;

            itemWidth = ReduceWidth(itemWidth, ItemCompactWidth, ref overflow);
            pathWidth = ReduceWidth(pathWidth, PathCompactWidth, ref overflow);
            modifiedWidth = ReduceWidth(modifiedWidth, ModifiedCompactWidth, ref overflow);
            categoryWidth = ReduceWidth(categoryWidth, CategoryCompactWidth, ref overflow);
            sizeWidth = ReduceWidth(sizeWidth, SizeCompactWidth, ref overflow);
            actionsWidth = ReduceWidth(actionsWidth, ActionsCompactWidth, ref overflow);
            typeWidth = ReduceWidth(typeWidth, TypeCompactWidth, ref overflow);

            if (overflow > 0)
            {
                itemWidth = ReduceWidth(itemWidth, ItemMinimumWidth, ref overflow);
                pathWidth = ReduceWidth(pathWidth, PathMinimumWidth, ref overflow);
                modifiedWidth = ReduceWidth(modifiedWidth, ModifiedMinimumWidth, ref overflow);
                categoryWidth = ReduceWidth(categoryWidth, CategoryMinimumWidth, ref overflow);
                sizeWidth = ReduceWidth(sizeWidth, SizeMinimumWidth, ref overflow);
                actionsWidth = ReduceWidth(actionsWidth, ActionsMinimumWidth, ref overflow);
                typeWidth = ReduceWidth(typeWidth, TypeMinimumWidth, ref overflow);
            }
        }

        _itemColumn.Width = itemWidth;
        _typeColumn?.SetWidth(typeWidth);
        _categoryColumn?.SetWidth(categoryWidth);
        _sizeColumn?.SetWidth(sizeWidth);
        _modifiedColumn?.SetWidth(modifiedWidth);
        _pathColumn?.SetWidth(pathWidth);
        _actionsColumn?.SetWidth(actionsWidth);
    }

    private void EnsureScrollHandlers()
    {
        if (_scrollHandlersAttached)
        {
            return;
        }

        AttachScrollHandler(_findingsListView ?? FindingsListView);
        _scrollHandlersAttached = true;
    }

    private static void AttachScrollHandler(ItemsControl? control)
    {
        if (control is null)
        {
            return;
        }

        control.PreviewMouseWheel -= OnNestedPreviewMouseWheel;
        control.PreviewMouseWheel += OnNestedPreviewMouseWheel;
    }

    private void DetachScrollHandlers()
    {
        if (!_scrollHandlersAttached)
        {
            return;
        }

        DetachScrollHandler(_findingsListView ?? FindingsListView);
        _scrollHandlersAttached = false;
    }

    private static void DetachScrollHandler(ItemsControl? control)
    {
        if (control is null)
        {
            return;
        }

        control.PreviewMouseWheel -= OnNestedPreviewMouseWheel;
    }

    private void BubbleScroll(MouseWheelEventArgs e, DependencyObject source)
    {
        if (RootScrollViewer is null || RootScrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        var nestedScrollViewer = FindDescendant<ScrollViewer>(source);
        if (nestedScrollViewer is null)
        {
            return;
        }

        var canScrollUp = e.Delta > 0 && nestedScrollViewer.VerticalOffset > 0;
        var canScrollDown = e.Delta < 0 && nestedScrollViewer.VerticalOffset < nestedScrollViewer.ScrollableHeight;

        if (canScrollUp || canScrollDown)
        {
            return;
        }

        e.Handled = true;

        var targetOffset = RootScrollViewer.VerticalOffset - e.Delta;
        if (targetOffset < 0)
        {
            targetOffset = 0;
        }
        else if (targetOffset > RootScrollViewer.ScrollableHeight)
        {
            targetOffset = RootScrollViewer.ScrollableHeight;
        }

        RootScrollViewer.ScrollToVerticalOffset(targetOffset);
    }

    private static void OnNestedPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject dependencyObject)
        {
            return;
        }

        if (FindParentPage(dependencyObject) is DeepScanPage page)
        {
            page.BubbleScroll(e, dependencyObject);
        }
    }

    private static DeepScanPage? FindParentPage(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is DeepScanPage page)
            {
                return page;
            }

            node = VisualTreeHelper.GetParent(node) ?? (node as FrameworkElement)?.Parent;
        }

        return null;
    }

    private static double ReduceWidth(double current, double minimum, ref double overflow)
    {
        if (overflow <= 0d)
        {
            return current;
        }

        var reducible = current - minimum;
        if (reducible <= 0d)
        {
            return current;
        }

        var reduction = Math.Min(reducible, overflow);
        overflow -= reduction;
        return current - reduction;
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
            if (child is T match)
            {
                return match;
            }

            var descendant = FindDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }
}

internal static class GridViewColumnExtensions
{
    public static void SetWidth(this GridViewColumn column, double width)
    {
        if (Math.Abs(column.Width - width) > 0.1)
        {
            column.Width = width;
        }
    }
}
