using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TidyWindow.Next.Core;
using Windows.System;

namespace TidyWindow.Next.Desktop;

public sealed partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private SettingsViewModel? Model => DataContext as SettingsViewModel;

    private async void CheckClick(object sender, RoutedEventArgs e)
    {
        if (Model is { } model) await model.CheckAsync();
    }

    private async void InstallClick(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        if (model.QueueBusy)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
                Title = "Work is still running",
                Content = new TextBlock
                {
                    Text = "An install, update, or uninstall is still in the queue. The installer closes TidyWindow, which stops that work and can leave an app incomplete.\n\nFinish the queue first unless you mean to stop it.",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 440
                },
                PrimaryButtonText = "Update anyway",
                CloseButtonText = "Not now",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowGatedAsync() != ContentDialogResult.Primary) return;
        }
        await model.DownloadAndLaunchAsync();
    }

    private void CancelClick(object sender, RoutedEventArgs e) => Model?.CancelDownload();

    private async void PortableClick(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        var path = await model.DownloadPortableAsync();
        if (string.IsNullOrWhiteSpace(path)) return;
        var folder = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(folder)) return;
        try
        {
            if (!await Launcher.LaunchFolderPathAsync(folder)) throw new IOException("File Explorer could not open the download folder.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ArgumentException)
        {
            model.Report("The download folder could not be opened", ex.Message);
        }
    }

    private void ReleasePageClick(object sender, RoutedEventArgs e)
    {
        if (Model is { } model) _ = OpenAsync(model.ReleasePageUrl);
    }

    private void ReleaseToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ReleaseNoteRow row }) row.Toggle();
    }

    private void OpenReleaseClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ReleaseNoteRow row } && row.Url.Length > 0) _ = OpenAsync(row.Url);
    }

    private void ReleasesClick(object sender, RoutedEventArgs e) => _ = OpenAsync(SettingsLinks.ReleasesUrl);
    private void CoffeeClick(object sender, RoutedEventArgs e) => _ = OpenAsync(SettingsLinks.BuyMeACoffeeUrl);

    private void LinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key }) return;
        var url = key switch
        {
            "repository" => SettingsLinks.RepositoryUrl,
            "issues" => SettingsLinks.IssuesUrl,
            "releases" => SettingsLinks.ReleasesUrl,
            "license" => SettingsLinks.LicenseUrl,
            _ => null
        };
        if (url is not null) _ = OpenAsync(url);
    }

    private async void OpenLogsClick(object sender, RoutedEventArgs e)
    {
        if (Model?.LogPath is not { } folder) return;
        try
        {
            Directory.CreateDirectory(folder);
            if (!await Launcher.LaunchFolderPathAsync(folder)) throw new IOException("File Explorer could not open this folder.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            Model.Report("The log folder could not be opened", ex.Message);
        }
    }

    private async void CopyPathClick(object sender, RoutedEventArgs e)
    {
        if (Model?.LogPath is not { } folder || !TryCopy(folder)) return;
        CopyPathLabel.Text = "Copied";
        await Task.Delay(1500);
        CopyPathLabel.Text = "Copy path";
    }

    private void ViewLogsClick(object sender, RoutedEventArgs e) => Model?.OpenLogs();

    private async Task OpenAsync(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw new UriFormatException(url);
            if (!await Launcher.LaunchUriAsync(uri)) throw new IOException("The browser could not open this link.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or UriFormatException or ArgumentException)
        {
            Model?.Report("The link could not be opened", ex.Message);
        }
    }

    private static bool TryCopy(string text)
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            return true;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException) { return false; }
    }
}
