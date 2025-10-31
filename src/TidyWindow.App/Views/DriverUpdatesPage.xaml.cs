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

namespace TidyWindow.App.Views;

public partial class DriverUpdatesPage : Page
{
    private readonly DriverUpdatesViewModel _viewModel;
    private bool _disposed;
    private bool _scrollHandlersAttached;

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
}
