using System;
using System.Windows;
using System.Windows.Controls;
using TidyWindow.App.ViewModels;
using System.Windows.Media;
using WpfListView = System.Windows.Controls.ListView;

namespace TidyWindow.App.Views;

public partial class LogsPage : Page
{
    private readonly LogsViewModel _viewModel;
    private WpfListView? _logsListView;
    private ScrollViewer? _logsScrollViewer;
    private GridViewColumn? _timestampColumn;
    private GridViewColumn? _levelColumn;
    private GridViewColumn? _sourceColumn;
    private GridViewColumn? _messageColumn;
    private GridViewColumn? _actionsColumn;
    private bool _isDisposed;

    private const double TimestampPreferredWidth = 120d;
    private const double TimestampCompactWidth = 100d;
    private const double TimestampMinimumWidth = 90d;

    private const double LevelPreferredWidth = 110d;
    private const double LevelCompactWidth = 96d;
    private const double LevelMinimumWidth = 84d;

    private const double SourcePreferredWidth = 180d;
    private const double SourceCompactWidth = 150d;
    private const double SourceMinimumWidth = 120d;

    private const double MessagePreferredWidth = 360d;
    private const double MessageCompactWidth = 300d;
    private const double MessageMinimumWidth = 220d;

    private const double ActionsPreferredWidth = 120d;
    private const double ActionsCompactWidth = 110d;
    private const double ActionsMinimumWidth = 100d;

    private const double LayoutPadding = 56d;

    public LogsPage(LogsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        Unloaded -= OnUnloaded;
        if (_logsListView is not null)
        {
            _logsListView.Loaded -= LogsListView_Loaded;
            _logsListView.SizeChanged -= LogsListView_SizeChanged;
        }
        _viewModel.Dispose();
        _isDisposed = true;
    }

    private void LogsListView_Loaded(object sender, RoutedEventArgs e)
    {
        _logsListView ??= sender as WpfListView;
        if (_logsListView is null)
        {
            return;
        }

        _logsScrollViewer ??= FindDescendant<ScrollViewer>(_logsListView);
        CacheColumns();
        UpdateColumnWidths();
    }

    private void LogsListView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            UpdateColumnWidths();
        }
    }

    private void CacheColumns()
    {
        if (_logsListView?.View is not GridView gridView)
        {
            return;
        }

        if (gridView.Columns.Count >= 5)
        {
            _timestampColumn = gridView.Columns[0];
            _levelColumn = gridView.Columns[1];
            _sourceColumn = gridView.Columns[2];
            _messageColumn = gridView.Columns[3];
            _actionsColumn = gridView.Columns[4];
        }
    }

    private void UpdateColumnWidths()
    {
        if (_logsListView is null || _timestampColumn is null)
        {
            return;
        }

        var availableWidth = _logsListView.ActualWidth;
        if (double.IsNaN(availableWidth) || availableWidth <= 0)
        {
            return;
        }

        var workingWidth = Math.Max(0d, availableWidth - LayoutPadding);

        if (_logsScrollViewer?.ComputedVerticalScrollBarVisibility == Visibility.Visible)
        {
            workingWidth = Math.Max(0d, workingWidth - SystemParameters.VerticalScrollBarWidth);
        }

        var timestampWidth = TimestampPreferredWidth;
        var levelWidth = LevelPreferredWidth;
        var sourceWidth = SourcePreferredWidth;
        var messageWidth = MessagePreferredWidth;
        var actionsWidth = ActionsPreferredWidth;

        var preferredTotal = timestampWidth + levelWidth + sourceWidth + messageWidth + actionsWidth;

        if (workingWidth > preferredTotal)
        {
            messageWidth += workingWidth - preferredTotal;
        }
        else
        {
            var overflow = preferredTotal - workingWidth;

            messageWidth = ReduceWidth(messageWidth, MessageCompactWidth, ref overflow);
            sourceWidth = ReduceWidth(sourceWidth, SourceCompactWidth, ref overflow);
            actionsWidth = ReduceWidth(actionsWidth, ActionsCompactWidth, ref overflow);
            timestampWidth = ReduceWidth(timestampWidth, TimestampCompactWidth, ref overflow);
            levelWidth = ReduceWidth(levelWidth, LevelCompactWidth, ref overflow);

            if (overflow > 0)
            {
                messageWidth = ReduceWidth(messageWidth, MessageMinimumWidth, ref overflow);
                sourceWidth = ReduceWidth(sourceWidth, SourceMinimumWidth, ref overflow);
                actionsWidth = ReduceWidth(actionsWidth, ActionsMinimumWidth, ref overflow);
                timestampWidth = ReduceWidth(timestampWidth, TimestampMinimumWidth, ref overflow);
                levelWidth = ReduceWidth(levelWidth, LevelMinimumWidth, ref overflow);
            }
        }

        _timestampColumn.Width = timestampWidth;
        AssignWidth(_levelColumn, levelWidth);
        AssignWidth(_sourceColumn, sourceWidth);
        AssignWidth(_messageColumn, messageWidth);
        AssignWidth(_actionsColumn, actionsWidth);
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

    private static void AssignWidth(GridViewColumn? column, double width)
    {
        if (column is null)
        {
            return;
        }

        if (Math.Abs(column.Width - width) > 0.1)
        {
            column.Width = width;
        }
    }
}
