using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using TidyWindow.Next.Core;
using Windows.System;

namespace TidyWindow.Next.Desktop;

public sealed partial class SettingsView : UserControl
{
    private Storyboard? shimmer;
    private SettingsViewModel? watched;

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Watch(e.NewValue as SettingsViewModel);
        Loaded += (_, _) => StartShimmer();
        Unloaded += (_, _) => { shimmer?.Stop(); shimmer = null; Watch(null); };
    }

    // The download bar is drawn from the view model's smoothed fraction, about thirty times a second while a download runs.
    private void Watch(SettingsViewModel? model)
    {
        if (ReferenceEquals(watched, model)) return;
        if (watched is not null) watched.PropertyChanged -= ModelChanged;
        watched = model;
        if (watched is not null) watched.PropertyChanged += ModelChanged;
        UpdateDownloadBar();
    }

    private void ModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.DownloadFraction) or nameof(SettingsViewModel.DownloadIndeterminate)) UpdateDownloadBar();
    }

    private void DownloadTrackSizeChanged(object sender, SizeChangedEventArgs e) => UpdateDownloadBar();

    private void UpdateDownloadBar()
    {
        var model = watched;
        var indeterminate = model?.DownloadIndeterminate == true;
        DownloadFill.Width = model is null ? 0 : DownloadTrack.ActualWidth * (indeterminate ? 1 : model.DownloadFraction);
        DownloadFill.Opacity = indeterminate ? 0.5 : 1;
    }

    // A highlight slides across the fill, then rests a moment before the next pass.
    private void StartShimmer()
    {
        if (shimmer is not null) return;
        var sweep = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = true, RepeatBehavior = RepeatBehavior.Forever };
        sweep.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = -0.6 });
        sweep.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(1300), Value = 0.6, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        sweep.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(2000), Value = 0.6 });
        Storyboard.SetTarget(sweep, ShimmerShift);
        Storyboard.SetTargetProperty(sweep, "X");
        shimmer = new Storyboard();
        shimmer.Children.Add(sweep);
        shimmer.Begin();
    }

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

    private void ReleasesToggleClick(object sender, RoutedEventArgs e) => Model?.ToggleReleases();

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
}
