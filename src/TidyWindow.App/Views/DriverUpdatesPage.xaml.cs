using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using TidyWindow.App.ViewModels;
using ListViewItem = System.Windows.Controls.ListViewItem;
using MessageBox = System.Windows.MessageBox;
using System.Windows.Documents;
using System.Windows.Media;
using WpfListView = System.Windows.Controls.ListView;

namespace TidyWindow.App.Views;

public partial class DriverUpdatesPage : Page
{
    private readonly DriverUpdatesViewModel _viewModel;
    private bool _disposed;
    private bool _scrollHandlersAttached;
    private WpfListView? _installedDriversList;
    private ScrollViewer? _installedDriversScrollViewer;
    private GridViewColumn? _installedDeviceColumn;
    private GridViewColumn? _installedManufacturerColumn;
    private GridViewColumn? _installedProviderColumn;
    private GridViewColumn? _installedVersionColumn;
    private GridViewColumn? _installedDriverDateColumn;
    private GridViewColumn? _installedInstalledColumn;
    private GridViewColumn? _installedStatusColumn;
    private GridViewColumn? _installedSignedColumn;

    private const double InstalledLayoutPadding = 64d;

    private const double InstalledDevicePreferredWidth = 260d;
    private const double InstalledDeviceCompactWidth = 220d;
    private const double InstalledDeviceMinimumWidth = 170d;

    private const double InstalledManufacturerPreferredWidth = 170d;
    private const double InstalledManufacturerCompactWidth = 140d;
    private const double InstalledManufacturerMinimumWidth = 110d;

    private const double InstalledProviderPreferredWidth = 170d;
    private const double InstalledProviderCompactWidth = 140d;
    private const double InstalledProviderMinimumWidth = 110d;

    private const double InstalledVersionPreferredWidth = 120d;
    private const double InstalledVersionCompactWidth = 100d;
    private const double InstalledVersionMinimumWidth = 88d;

    private const double InstalledDriverDatePreferredWidth = 120d;
    private const double InstalledDriverDateCompactWidth = 110d;
    private const double InstalledDriverDateMinimumWidth = 92d;

    private const double InstalledInstalledPreferredWidth = 120d;
    private const double InstalledInstalledCompactWidth = 110d;
    private const double InstalledInstalledMinimumWidth = 92d;

    private const double InstalledStatusPreferredWidth = 140d;
    private const double InstalledStatusCompactWidth = 120d;
    private const double InstalledStatusMinimumWidth = 96d;

    private const double InstalledSignedPreferredWidth = 90d;
    private const double InstalledSignedCompactWidth = 80d;
    private const double InstalledSignedMinimumWidth = 68d;

    public DriverUpdatesPage(DriverUpdatesViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        EnsureScrollHandlers();

        if (_viewModel.HasScanned)
        {
            return;
        }

        await _viewModel.RefreshCommand.ExecuteAsync(null);
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        DetachScrollHandlers();
        _disposed = true;
    }

    private void OnHyperlinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        if (e.Uri is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch
        {
            // No-op: navigation failures are not fatal.
        }

        e.Handled = true;
    }

    private void OnUpdateItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListViewItem item || item.DataContext is not DriverUpdateItemViewModel driver)
        {
            return;
        }

        e.Handled = true;

        if (IsHyperlinkSource(e.OriginalSource))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(driver.InstalledInfPath))
        {
            ShowDriverLocationUnavailable(driver.DeviceName);
            return;
        }

        if (TryOpenDriverLocation(driver.InstalledInfPath))
        {
            return;
        }

        ShowDriverLocationUnavailable(driver.DeviceName);
    }

    private void OnInstalledDriverDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListViewItem item || item.DataContext is not InstalledDriverItemViewModel driver)
        {
            return;
        }

        e.Handled = true;

        if (IsHyperlinkSource(e.OriginalSource))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(driver.InfName))
        {
            ShowDriverLocationUnavailable(driver.DeviceName);
            return;
        }

        if (TryOpenDriverLocation(driver.InfName))
        {
            return;
        }

        ShowDriverLocationUnavailable(driver.DeviceName);
    }

    private void InstalledDriversList_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfListView listView)
        {
            return;
        }

        _installedDriversList = listView;
        _installedDriversScrollViewer ??= FindDescendant<ScrollViewer>(listView);
        CacheInstalledColumns();
        UpdateInstalledColumnWidths();
    }

    private void InstalledDriversList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            UpdateInstalledColumnWidths();
        }
    }

    private static bool TryOpenDriverLocation(string? driverReference)
    {
        var resolvedPath = ResolveDriverPath(driverReference);
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            return false;
        }

        try
        {
            if (Directory.Exists(resolvedPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{resolvedPath}\"",
                    UseShellExecute = true
                });
                return true;
            }

            if (!File.Exists(resolvedPath))
            {
                return false;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{resolvedPath}\"",
                UseShellExecute = true
            });

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolveDriverPath(string? driverReference)
    {
        if (string.IsNullOrWhiteSpace(driverReference))
        {
            return null;
        }

        var trimmed = driverReference.Trim();
        if (string.Equals(trimmed, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (Path.IsPathRooted(trimmed))
        {
            return trimmed;
        }

        var windowsRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windowsRoot))
        {
            var candidate = Path.Combine(windowsRoot, "INF", trimmed);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var fileName = trimmed;
        if (!Path.HasExtension(fileName))
        {
            fileName += ".inf";
        }

        if (!string.IsNullOrEmpty(windowsRoot))
        {
            var driverStore = Path.Combine(windowsRoot, "System32", "DriverStore");
            if (Directory.Exists(driverStore))
            {
                try
                {
                    var match = Directory
                        .EnumerateFiles(driverStore, fileName, SearchOption.AllDirectories)
                        .FirstOrDefault();

                    if (!string.IsNullOrEmpty(match))
                    {
                        return match;
                    }
                }
                catch
                {
                    // Driver store enumeration can fail due to access restrictions; ignore and fall through.
                }
            }
        }

        return null;
    }

    private static void ShowDriverLocationUnavailable(string deviceName)
    {
        var message = string.IsNullOrWhiteSpace(deviceName)
            ? "We couldn't locate a driver package for the selected entry."
            : $"We couldn't locate a driver package for \"{deviceName}\".";

        MessageBox.Show(
            message + "\nTry refreshing the scan or manage the device from Device Manager instead.",
            "Driver location unavailable",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
    }

    private static bool IsHyperlinkSource(object? source)
    {
        if (source is not DependencyObject node)
        {
            return false;
        }

        while (node is not null)
        {
            if (node is Hyperlink)
            {
                return true;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return false;
    }

    private void EnsureScrollHandlers()
    {
        if (_scrollHandlersAttached)
        {
            return;
        }

        AttachScrollHandler(UpdatesList);
        AttachScrollHandler(InstalledDriversList);
        _scrollHandlersAttached = true;

        UpdateInstalledColumnWidths();
    }

    private void DetachScrollHandlers()
    {
        if (!_scrollHandlersAttached)
        {
            return;
        }

        DetachScrollHandler(UpdatesList);
        DetachScrollHandler(InstalledDriversList);
        _scrollHandlersAttached = false;
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

        var targetOffset = RootScrollViewer.VerticalOffset - (e.Delta * 0.4);
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

    private static void AttachScrollHandler(ItemsControl? control)
    {
        if (control is null)
        {
            return;
        }

        control.PreviewMouseWheel -= OnNestedPreviewMouseWheel;
        control.PreviewMouseWheel += OnNestedPreviewMouseWheel;
    }

    private static void DetachScrollHandler(ItemsControl? control)
    {
        if (control is null)
        {
            return;
        }

        control.PreviewMouseWheel -= OnNestedPreviewMouseWheel;
    }

    private static void OnNestedPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject dependencyObject)
        {
            return;
        }

        if (FindParentPage(dependencyObject) is DriverUpdatesPage page)
        {
            page.BubbleScroll(e, dependencyObject);
        }
    }

    private static DriverUpdatesPage? FindParentPage(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is DriverUpdatesPage page)
            {
                return page;
            }

            node = VisualTreeHelper.GetParent(node) ?? (node as FrameworkElement)?.Parent;
        }

        return null;
    }

    private static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null)
        {
            return null;
        }

        if (root is T match)
        {
            return match;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var result = FindDescendant<T>(child);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    private void CacheInstalledColumns()
    {
        if (_installedDriversList?.View is not GridView gridView)
        {
            return;
        }

        if (gridView.Columns.Count < 8)
        {
            return;
        }

        _installedDeviceColumn = gridView.Columns[0];
        _installedManufacturerColumn = gridView.Columns[1];
        _installedProviderColumn = gridView.Columns[2];
        _installedVersionColumn = gridView.Columns[3];
        _installedDriverDateColumn = gridView.Columns[4];
        _installedInstalledColumn = gridView.Columns[5];
        _installedStatusColumn = gridView.Columns[6];
        _installedSignedColumn = gridView.Columns[7];
    }

    private void UpdateInstalledColumnWidths()
    {
        if (_installedDriversList is null || _installedDeviceColumn is null)
        {
            return;
        }

        var availableWidth = _installedDriversList.ActualWidth;
        if (double.IsNaN(availableWidth) || availableWidth <= 0)
        {
            return;
        }

        var workingWidth = Math.Max(0d, availableWidth - InstalledLayoutPadding);

        if (_installedDriversScrollViewer?.ComputedVerticalScrollBarVisibility == Visibility.Visible)
        {
            workingWidth = Math.Max(0d, workingWidth - SystemParameters.VerticalScrollBarWidth);
        }

        var deviceWidth = InstalledDevicePreferredWidth;
        var manufacturerWidth = InstalledManufacturerPreferredWidth;
        var providerWidth = InstalledProviderPreferredWidth;
        var versionWidth = InstalledVersionPreferredWidth;
        var driverDateWidth = InstalledDriverDatePreferredWidth;
        var installedWidth = InstalledInstalledPreferredWidth;
        var statusWidth = InstalledStatusPreferredWidth;
        var signedWidth = InstalledSignedPreferredWidth;

        var preferredTotal = deviceWidth + manufacturerWidth + providerWidth + versionWidth + driverDateWidth + installedWidth + statusWidth + signedWidth;

        if (workingWidth > preferredTotal)
        {
            var extra = workingWidth - preferredTotal;
            deviceWidth += extra * 0.53;
            manufacturerWidth += extra * 0.12;
            providerWidth += extra * 0.1;
            statusWidth += extra * 0.1;
            versionWidth += extra * 0.06;
            driverDateWidth += extra * 0.04;
            installedWidth += extra * 0.03;
            signedWidth += extra * 0.02;
        }
        else
        {
            var overflow = preferredTotal - workingWidth;

            deviceWidth = ReduceWidth(deviceWidth, InstalledDeviceCompactWidth, ref overflow);
            manufacturerWidth = ReduceWidth(manufacturerWidth, InstalledManufacturerCompactWidth, ref overflow);
            providerWidth = ReduceWidth(providerWidth, InstalledProviderCompactWidth, ref overflow);
            statusWidth = ReduceWidth(statusWidth, InstalledStatusCompactWidth, ref overflow);
            versionWidth = ReduceWidth(versionWidth, InstalledVersionCompactWidth, ref overflow);
            driverDateWidth = ReduceWidth(driverDateWidth, InstalledDriverDateCompactWidth, ref overflow);
            installedWidth = ReduceWidth(installedWidth, InstalledInstalledCompactWidth, ref overflow);
            signedWidth = ReduceWidth(signedWidth, InstalledSignedCompactWidth, ref overflow);

            if (overflow > 0)
            {
                deviceWidth = ReduceWidth(deviceWidth, InstalledDeviceMinimumWidth, ref overflow);
                manufacturerWidth = ReduceWidth(manufacturerWidth, InstalledManufacturerMinimumWidth, ref overflow);
                providerWidth = ReduceWidth(providerWidth, InstalledProviderMinimumWidth, ref overflow);
                statusWidth = ReduceWidth(statusWidth, InstalledStatusMinimumWidth, ref overflow);
                versionWidth = ReduceWidth(versionWidth, InstalledVersionMinimumWidth, ref overflow);
                driverDateWidth = ReduceWidth(driverDateWidth, InstalledDriverDateMinimumWidth, ref overflow);
                installedWidth = ReduceWidth(installedWidth, InstalledInstalledMinimumWidth, ref overflow);
                signedWidth = ReduceWidth(signedWidth, InstalledSignedMinimumWidth, ref overflow);
            }
        }

        SetColumnWidth(_installedDeviceColumn, deviceWidth);
        SetColumnWidth(_installedManufacturerColumn, manufacturerWidth);
        SetColumnWidth(_installedProviderColumn, providerWidth);
        SetColumnWidth(_installedVersionColumn, versionWidth);
        SetColumnWidth(_installedDriverDateColumn, driverDateWidth);
        SetColumnWidth(_installedInstalledColumn, installedWidth);
        SetColumnWidth(_installedStatusColumn, statusWidth);
        SetColumnWidth(_installedSignedColumn, signedWidth);
    }

    private static double ReduceWidth(double current, double minimum, ref double overflow)
    {
        if (overflow <= 0)
        {
            return current;
        }

        var reducible = current - minimum;
        if (reducible <= 0)
        {
            return current;
        }

        var reduction = Math.Min(reducible, overflow);
        overflow -= reduction;
        return current - reduction;
    }

    private static void SetColumnWidth(GridViewColumn? column, double width)
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
