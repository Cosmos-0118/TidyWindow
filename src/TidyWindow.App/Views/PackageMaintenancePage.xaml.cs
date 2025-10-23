using System;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.ViewModels;
using System.Windows.Media;
using MessageBox = System.Windows.MessageBox;
using WpfApplication = System.Windows.Application;
using WpfListView = System.Windows.Controls.ListView;

namespace TidyWindow.App.Views;

public partial class PackageMaintenancePage : Page
{
    private readonly PackageMaintenanceViewModel _viewModel;
    private WpfListView? _packagesListView;
    private ScrollViewer? _packagesScrollViewer;
    private GridViewColumn? _packageColumn;
    private GridViewColumn? _managerColumn;
    private GridViewColumn? _sourceColumn;
    private GridViewColumn? _versionColumn;
    private GridViewColumn? _statusColumn;
    private GridViewColumn? _targetVersionColumn;
    private GridViewColumn? _actionsColumn;
    private bool _disposed;

    // Column width breakpoints keep the maintenance table legible across window sizes.
    private const double PackagePreferredWidth = 260d;
    private const double PackageCompactWidth = 220d;
    private const double PackageMinimumWidth = 190d;

    private const double ManagerPreferredWidth = 80d;
    private const double ManagerCompactWidth = 70d;

    private const double SourcePreferredWidth = 80d;
    private const double SourceCompactWidth = 70d;

    private const double VersionPreferredWidth = 86d;
    private const double VersionCompactWidth = 74d;

    private const double StatusPreferredWidth = 140d;
    private const double StatusCompactWidth = 110d;

    private const double TargetPreferredWidth = 104d;
    private const double TargetCompactWidth = 90d;

    private const double ActionsPreferredWidth = 160d;
    private const double ActionsCompactWidth = 140d;

    private const double LayoutPadding = 56d;

    public PackageMaintenancePage(PackageMaintenanceViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;

        _viewModel.ConfirmElevation = ConfirmElevation;
        _viewModel.AdministratorRestartRequested += OnAdministratorRestartRequested;
        Unloaded += OnPageUnloaded;
        Loaded += OnPageLoaded;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnPageLoaded;
        if (_viewModel.HasLoadedInitialData)
            return;
        if (_viewModel.RefreshCommand is IAsyncRelayCommand asyncCommand)
        {
            await asyncCommand.ExecuteAsync(null);
        }
        else
        {
            _viewModel.RefreshCommand.Execute(null);
        }
    }

    private bool ConfirmElevation(string message)
    {
        var prompt = string.IsNullOrWhiteSpace(message)
            ? "This operation requires administrator privileges. Restart as administrator to continue?"
            : message;

        var result = MessageBox.Show(prompt,
            "Administrator privileges needed",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.Yes);

        return result == MessageBoxResult.Yes;
    }

    private void OnAdministratorRestartRequested(object? sender, EventArgs e)
    {
        var app = WpfApplication.Current;
        if (app is null)
        {
            return;
        }

        app.Dispatcher.BeginInvoke(new Action(app.Shutdown));
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _viewModel.AdministratorRestartRequested -= OnAdministratorRestartRequested;
        _viewModel.ConfirmElevation = null;
        if (_packagesListView is not null)
        {
            _packagesListView.Loaded -= PackagesListView_Loaded;
            _packagesListView.SizeChanged -= PackagesListView_SizeChanged;
        }
        // Do not dispose the viewmodel to preserve state between navigations
        Unloaded -= OnPageUnloaded;
        _disposed = true;
    }

    private void PackagesListView_Loaded(object sender, RoutedEventArgs e)
    {
        _packagesListView ??= sender as WpfListView;
        if (_packagesListView is null)
        {
            return;
        }

        _packagesScrollViewer ??= FindDescendant<ScrollViewer>(_packagesListView);
        CacheColumns();
        UpdateColumnWidths();
    }

    private void PackagesListView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            UpdateColumnWidths();
        }
    }

    private void UpdateColumnWidths()
    {
        if (_packagesListView is null)
        {
            return;
        }

        if (_packageColumn is null)
        {
            return;
        }

        var availableWidth = _packagesListView.ActualWidth;
        if (double.IsNaN(availableWidth) || availableWidth <= 0)
        {
            return;
        }

        var workingWidth = Math.Max(0d, availableWidth - LayoutPadding);

        if (_packagesScrollViewer?.ComputedVerticalScrollBarVisibility == Visibility.Visible)
        {
            workingWidth = Math.Max(0d, workingWidth - SystemParameters.VerticalScrollBarWidth);
        }

        var packageWidth = PackagePreferredWidth;
        var managerWidth = ManagerPreferredWidth;
        var sourceWidth = SourcePreferredWidth;
        var versionWidth = VersionPreferredWidth;
        var statusWidth = StatusPreferredWidth;
        var targetWidth = TargetPreferredWidth;
        var actionsWidth = ActionsPreferredWidth;

        var preferredTotal = packageWidth + managerWidth + sourceWidth + versionWidth + statusWidth + targetWidth + actionsWidth;

        if (workingWidth > preferredTotal)
        {
            packageWidth += workingWidth - preferredTotal;
        }
        else
        {
            var overflow = preferredTotal - workingWidth;

            packageWidth = ReduceWidth(packageWidth, PackageCompactWidth, ref overflow);
            actionsWidth = ReduceWidth(actionsWidth, ActionsCompactWidth, ref overflow);
            statusWidth = ReduceWidth(statusWidth, StatusCompactWidth, ref overflow);
            managerWidth = ReduceWidth(managerWidth, ManagerCompactWidth, ref overflow);
            sourceWidth = ReduceWidth(sourceWidth, SourceCompactWidth, ref overflow);
            versionWidth = ReduceWidth(versionWidth, VersionCompactWidth, ref overflow);
            targetWidth = ReduceWidth(targetWidth, TargetCompactWidth, ref overflow);

            if (overflow > 0)
            {
                packageWidth = ReduceWidth(packageWidth, PackageMinimumWidth, ref overflow);
            }
        }

        _packageColumn.Width = packageWidth;
        if (_managerColumn is not null)
        {
            _managerColumn.Width = managerWidth;
        }

        if (_sourceColumn is not null)
        {
            _sourceColumn.Width = sourceWidth;
        }

        if (_versionColumn is not null)
        {
            _versionColumn.Width = versionWidth;
        }

        if (_statusColumn is not null)
        {
            _statusColumn.Width = statusWidth;
        }

        if (_targetVersionColumn is not null)
        {
            _targetVersionColumn.Width = targetWidth;
        }

        if (_actionsColumn is not null)
        {
            _actionsColumn.Width = actionsWidth;
        }
    }

    private void CacheColumns()
    {
        if (_packagesListView?.View is not GridView gridView)
        {
            return;
        }

        // Column ordering is fixed in XAML; caching avoids walking the visual tree every resize.
        if (gridView.Columns.Count >= 7)
        {
            _packageColumn = gridView.Columns[0];
            _managerColumn = gridView.Columns[1];
            _sourceColumn = gridView.Columns[2];
            _versionColumn = gridView.Columns[3];
            _statusColumn = gridView.Columns[4];
            _targetVersionColumn = gridView.Columns[5];
            _actionsColumn = gridView.Columns[6];
        }
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
