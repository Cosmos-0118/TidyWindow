using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using TidyWindow.App.ViewModels;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace TidyWindow.App.Views.DriverUpdates;

public partial class DriverUpdatesListView : UserControl
{
    public DriverUpdatesListView()
    {
        InitializeComponent();
    }

    private void OnUpdateCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2 || e.Handled)
        {
            return;
        }

        if (IsHyperlinkSource(e.OriginalSource))
        {
            return;
        }

        if (FindDataContext<DriverUpdateItemViewModel>(sender) is not { } driver)
        {
            return;
        }

        e.Handled = true;

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

    private void OnInstalledCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2 || e.Handled)
        {
            return;
        }

        if (IsHyperlinkSource(e.OriginalSource))
        {
            return;
        }

        if (FindDataContext<InstalledDriverItemViewModel>(sender) is not { } driver)
        {
            return;
        }

        e.Handled = true;

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
            // ignore navigation failures
        }

        e.Handled = true;
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
                    // ignore access issues
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
            MessageBoxButton.OK,
            MessageBoxImage.Information);
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

    private static T? FindDataContext<T>(object? source) where T : class
    {
        if (source is not DependencyObject node)
        {
            return null;
        }

        while (node is not null)
        {
            if (node is FrameworkElement { DataContext: T match })
            {
                return match;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

}
