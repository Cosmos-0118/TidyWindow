using System.Net.Http;
using System.Text.Json;
using Microsoft.UI.Xaml;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

internal static class SettingsLinks
{
    public const string RepositoryUrl = "https://github.com/Cosmos-0118/TidyWindow";
    public const string IssuesUrl = "https://github.com/Cosmos-0118/TidyWindow/issues";
    public const string ReleasesUrl = "https://github.com/Cosmos-0118/TidyWindow/releases";
    public const string LicenseUrl = "https://github.com/Cosmos-0118/TidyWindow/blob/main/LICENSE";
    public const string BuyMeACoffeeUrl = "https://buymeacoffee.com/cosmos0118";
}

public sealed class ReleaseNoteRow : ObservableState
{
    private bool expanded;

    public ReleaseNoteRow(AppRelease release, bool current, bool latest, bool expanded)
    {
        Release = release;
        this.expanded = expanded;
        CurrentVisibility = current ? Visibility.Visible : Visibility.Collapsed;
        LatestVisibility = latest ? Visibility.Visible : Visibility.Collapsed;
    }

    public AppRelease Release { get; }
    public string Tag => Release.TagName;
    public string Title => string.IsNullOrWhiteSpace(Release.Title) || Release.Title == Release.TagName ? "" : Release.Title;
    public Visibility TitleVisibility => Title.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public string When => Release.PublishedAt == default ? "" : Release.PublishedAt.ToLocalTime().ToString("d MMM yyyy");
    public string Notes => string.IsNullOrWhiteSpace(Release.CleanNotes) ? "No notes were published with this release." : Release.CleanNotes;
    public string Url => Release.HtmlUrl;
    public bool IsExpanded => expanded;
    public Visibility NotesVisibility => expanded ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CurrentVisibility { get; }
    public Visibility LatestVisibility { get; }
    public string Chevron => expanded ? "\uE70E" : "\uE70D";

    public void Toggle()
    {
        expanded = !expanded;
        Changed(nameof(IsExpanded));
        Changed(nameof(NotesVisibility));
        Changed(nameof(Chevron));
    }
}

public sealed class SettingsViewModel : ObservableState, IDisposable
{
    private enum Phase { Idle, Checking, Downloading }

    private readonly ActivityLog log;
    private readonly ToastCenter toasts;
    private readonly Func<bool> queueHasWork;
    private readonly Action<Action> post;
    private readonly AppUpdateService updates;
    private readonly bool installed;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? download;
    private UpdateCheckResult? result;
    private Phase phase;
    private bool attempted;
    private bool disposed;
    private bool downloadingZip;
    private string error = "";
    private string note = "";
    private DateTimeOffset? checkedAt;
    private readonly DownloadMeter meter = new();
    private int tickPending;
    private bool releasesOpen;

    public SettingsViewModel(ActivityLog log, ToastCenter toasts, Func<bool> queueHasWork, Action<Action> post, AppUpdateService? updates = null)
    {
        this.log = log;
        this.toasts = toasts;
        this.queueHasWork = queueHasWork;
        this.post = post;
        this.updates = updates ?? new AppUpdateService();
        installed = this.updates.IsInstalledBuild();
    }

    public BulkObservableCollection<ReleaseNoteRow> Releases { get; } = [];
    public bool QueueBusy => queueHasWork();
    public string CurrentVersionText => "v" + updates.CurrentVersion;
    public string InstallKind => installed ? "Installed with Setup" : "Portable copy";
    public string LatestVersionText => result?.LatestRelease is { } latest ? "v" + latest.Version : "—";
    public string StatusTag => result?.UpdateAvailable == true ? "Update available"
        : result is { LatestRelease: { } found } && found.Version == updates.CurrentVersion && error.Length == 0 ? "Up to date"
        : error.Length > 0 ? "Couldn't check"
        : phase == Phase.Checking ? "Checking"
        : "Not checked";
    public string StatusDetail => phase switch
    {
        Phase.Checking => "Checking GitHub for a newer release…",
        Phase.Downloading => downloadingZip ? "Downloading the portable ZIP…" : "Downloading the installer…",
        _ when error.Length > 0 => error,
        _ when note.Length > 0 => note,
        _ when result?.LatestRelease is null && result is not null => "No TidyWindow 5 release was found on GitHub.",
        _ when result is { UpdateAvailable: true } => installed
            ? "A newer release is ready. Setup replaces this copy and keeps your settings."
            : "A newer release is ready. Download the ZIP, or run Setup if you want a Start menu entry.",
        _ when result is { LatestRelease: { } found } && found.Version == updates.CurrentVersion => checkedAt is DateTimeOffset stamp
            ? $"This is the latest release. Checked {stamp.ToLocalTime():t}."
            : "This is the latest release.",
        _ when result?.LatestRelease is { } older => $"This build is newer than {older.TagName}, the latest release on GitHub.",
        _ => "Not checked yet."
    };
    public Visibility StatusGoodVisibility => result is { UpdateAvailable: false, LatestRelease: { } found } && found.Version == updates.CurrentVersion && error.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility StatusWarnVisibility => result?.UpdateAvailable == true ? Visibility.Visible : Visibility.Collapsed;
    public Visibility StatusMutedVisibility => StatusGoodVisibility == Visibility.Collapsed && StatusWarnVisibility == Visibility.Collapsed ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NormalDetailVisibility => phase == Phase.Idle && error.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ErrorVisibility => error.Length > 0 && phase == Phase.Idle ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CheckingVisibility => phase == Phase.Checking ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CheckVisibility => phase == Phase.Idle ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CancelVisibility => phase == Phase.Downloading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProgressVisibility => phase == Phase.Downloading ? Visibility.Visible : Visibility.Collapsed;
    public string DownloadText => result?.LatestRelease is { } latest ? $"Update to v{latest.Version}" : "Update";
    public Visibility InstallerPrimaryVisibility => CanInstall && installed ? Visibility.Visible : Visibility.Collapsed;
    public Visibility InstallerSecondaryVisibility => CanInstall && !installed ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ZipVisibility => phase == Phase.Idle && !installed && result is { UpdateAvailable: true, LatestRelease.PortableAsset: not null } ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ReleasePageVisibility => phase == Phase.Idle && result?.UpdateAvailable == true ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ActionsVisibility => phase == Phase.Downloading || result?.UpdateAvailable == true ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ActionButtonsVisibility => phase == Phase.Idle && result?.UpdateAvailable == true ? Visibility.Visible : Visibility.Collapsed;
    public string ReleasePageUrl => result?.LatestRelease?.HtmlUrl is { Length: > 0 } url ? url : SettingsLinks.ReleasesUrl;
    public string ProgressTitle => downloadingZip ? "Downloading the portable ZIP…" : "Downloading the installer…";
    public double DownloadFraction => meter.Fraction;
    public bool DownloadIndeterminate => meter.Indeterminate;
    public string DownloadProgressText => meter.Progress();
    public string DownloadPaceText => meter.Pace();
    public string DownloadPercentText => meter.Percent();
    public string ReleasesChevron => releasesOpen ? "" : "";
    public Visibility ReleasesBodyVisibility => releasesOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ReleasesLoadingVisibility => phase == Phase.Checking && Releases.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ReleasesEmptyVisibility => phase != Phase.Checking && Releases.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ReleasesListVisibility => Releases.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    public string ReleasesEmptyTitle => error.Length > 0 ? "Release notes could not be loaded" : "No release notes yet";
    public string ReleasesEmptyDetail => error.Length > 0 ? "Check your connection and try again." : "They load when GitHub is checked.";
    private bool CanInstall => phase == Phase.Idle && result is { UpdateAvailable: true, LatestRelease.InstallerAsset: not null };

    public async Task EnsureLoadedAsync()
    {
        if (attempted || phase != Phase.Idle) return;
        attempted = true;
        await CheckAsync();
    }

    public async Task CheckAsync()
    {
        if (phase != Phase.Idle) return;
        phase = Phase.Checking;
        error = "";
        note = "";
        Notify();
        try
        {
            var next = await updates.CheckAsync(lifetime.Token).ConfigureAwait(false);
            post(() => Apply(next));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        // Whatever went wrong, the panel must not stay on "Checking" with its button gone: every failure ends the check and says so.
        catch (Exception ex) { post(() => FailCheck(ex)); }
    }

    public Task DownloadAndLaunchAsync() => RunDownload(zip: false, launch: true);

    public async Task<string?> DownloadPortableAsync()
    {
        var path = await RunDownload(zip: true, launch: false);
        return path;
    }

    public void CancelDownload() => download?.Cancel();

    public void ToggleReleases()
    {
        releasesOpen = !releasesOpen;
        Changed(nameof(ReleasesChevron));
        Changed(nameof(ReleasesBodyVisibility));
    }

    public void Report(string title, string detail)
    {
        if (disposed) return;
        log.Write(ActivityLevel.Warning, ActivityArea.App, title, detail);
        toasts.Show("settings-link", ToastKind.Warning, title, ToastCenter.Flat(detail));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        download?.Cancel();
    }

    private async Task<string?> RunDownload(bool zip, bool launch)
    {
        if (disposed || phase != Phase.Idle) return null;
        var asset = zip ? result?.LatestRelease?.PortableAsset : result?.LatestRelease?.InstallerAsset;
        if (asset is null) return null;
        download = new CancellationTokenSource();
        var token = download.Token;
        downloadingZip = zip;
        phase = Phase.Downloading;
        error = "";
        note = "";
        meter.Reset(asset.SizeBytes > 0 ? asset.SizeBytes : null);
        Notify();
        using var animation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        _ = AnimateAsync(animation.Token);
        // Without a checksum from the release the download is checked by its size and file type only, which is worth knowing when something goes wrong later.
        if (asset.Sha256 is null) log.Write(ActivityLevel.Warning, ActivityArea.App, "The update's checksum was not published", $"GitHub lists no SHA-256 for {asset.Name}, so the download is checked by size and file type only.");
        // Not Progress<T>: that posts every report to the UI thread, hundreds a second. The meter just takes the number; the timer below draws it.
        var progress = new DirectProgress(report => meter.Report(report.BytesReceived, report.TotalBytes));
        try
        {
            var path = zip
                ? await updates.DownloadPortableAsync(asset, progress, token).ConfigureAwait(false)
                : await updates.DownloadInstallerAsync(asset, progress, token).ConfigureAwait(false);
            if (launch)
            {
                try { AppUpdateService.LaunchInstaller(path); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or InvalidOperationException)
                {
                    post(() => FailDownload(ex, "The installer downloaded, but it could not start."));
                    return path;
                }
            }
            post(() => FinishDownload(path, launch));
            return path;
        }
        catch (OperationCanceledException)
        {
            post(() => StopDownload("Download cancelled."));
            return null;
        }
        // A stalled connection, a checksum that does not match, or anything unforeseen: the panel goes back to idle and says what happened.
        catch (Exception ex)
        {
            post(() => FailDownload(ex, "The update could not be downloaded."));
            return null;
        }
        finally { animation.Cancel(); }
    }

    private sealed class DirectProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }

    /// <summary>Redraws the download numbers about thirty times a second until the download ends.</summary>
    private async Task AnimateAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(33));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var last = TimeSpan.Zero;
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                var now = clock.Elapsed;
                var step = (now - last).TotalSeconds;
                last = now;
                // A busy UI thread skips frames rather than queueing them.
                if (Interlocked.Exchange(ref tickPending, 1) == 1) continue;
                post(() => { Interlocked.Exchange(ref tickPending, 0); Tick(step); });
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Tick(double seconds)
    {
        if (disposed || phase != Phase.Downloading) return;
        meter.Tick(seconds);
        Changed(nameof(DownloadFraction));
        Changed(nameof(DownloadIndeterminate));
        Changed(nameof(DownloadProgressText));
        Changed(nameof(DownloadPaceText));
        Changed(nameof(DownloadPercentText));
    }

    private void Apply(UpdateCheckResult next)
    {
        if (disposed) return;
        var hadRows = Releases.Count > 0;
        // Every release starts collapsed; a refresh keeps whichever ones the person had opened.
        var open = Releases.Where(x => x.IsExpanded).Select(x => x.Tag).ToHashSet(StringComparer.OrdinalIgnoreCase);
        result = next;
        phase = Phase.Idle;
        error = "";
        note = "";
        checkedAt = DateTimeOffset.Now;
        var latest = next.LatestRelease?.TagName;
        Releases.Reset(next.RecentReleases.Select(release =>
        {
            var current = release.Version == next.CurrentVersion;
            var expanded = hadRows && open.Contains(release.TagName);
            return new ReleaseNoteRow(release, current, release.TagName == latest && !current, expanded);
        }));
        Notify();
    }

    private void FailCheck(Exception ex)
    {
        if (disposed) return;
        phase = Phase.Idle;
        error = Explain(ex);
        log.Write(ActivityLevel.Warning, ActivityArea.App, "Could not check for updates", ex.Message);
        Notify();
    }

    private void FinishDownload(string path, bool launch)
    {
        if (disposed) return;
        phase = Phase.Idle;
        note = launch
            ? "The installer is starting. TidyWindow closes if Setup needs to replace this copy. Your settings stay on this PC."
            : $"Portable ZIP downloaded to {path}";
        log.Write(ActivityLevel.Success, ActivityArea.App, launch ? "Update installer started" : "Portable update downloaded", path);
        toasts.Show("settings-update", launch ? ToastKind.Info : ToastKind.Success, launch ? "Starting the installer…" : "Portable ZIP downloaded",
            launch ? "TidyWindow closes if Setup needs to replace it." : "The folder opens so you can extract it.", seconds: 12);
        Notify();
    }

    private void FailDownload(Exception ex, string title)
    {
        if (disposed) return;
        phase = Phase.Idle;
        error = title + " " + Explain(ex);
        log.Write(ActivityLevel.Warning, ActivityArea.App, title, ex.Message);
        toasts.Show("settings-update", ToastKind.Warning, title, ToastCenter.Flat(ex.Message));
        Notify();
    }

    private void StopDownload(string message)
    {
        if (disposed) return;
        phase = Phase.Idle;
        error = "";
        note = message;
        Notify();
    }

    private void Notify()
    {
        if (disposed) return;
        foreach (var name in new[]
        {
            nameof(CurrentVersionText), nameof(InstallKind), nameof(LatestVersionText), nameof(StatusTag), nameof(StatusDetail),
            nameof(StatusGoodVisibility), nameof(StatusWarnVisibility), nameof(StatusMutedVisibility),
            nameof(NormalDetailVisibility), nameof(ErrorVisibility), nameof(CheckingVisibility), nameof(CheckVisibility), nameof(CancelVisibility),
            nameof(ProgressVisibility), nameof(DownloadText), nameof(InstallerPrimaryVisibility), nameof(InstallerSecondaryVisibility),
            nameof(ZipVisibility), nameof(ReleasePageVisibility), nameof(ActionsVisibility), nameof(ActionButtonsVisibility), nameof(ReleasePageUrl), nameof(ProgressTitle),
            nameof(DownloadFraction), nameof(DownloadIndeterminate), nameof(DownloadProgressText), nameof(DownloadPaceText), nameof(DownloadPercentText),
            nameof(ReleasesLoadingVisibility), nameof(ReleasesEmptyVisibility), nameof(ReleasesListVisibility), nameof(ReleasesEmptyTitle), nameof(ReleasesEmptyDetail)
        }) Changed(name);
    }

    private static string Explain(Exception ex) => ex switch
    {
        TimeoutException { Message.Length: > 0 } slow => slow.Message,
        TimeoutException => "GitHub took too long to answer. Try again in a moment.",
        HttpRequestException => "GitHub could not be reached. Check your connection and try again.",
        InvalidOperationException { Message.Length: > 0 } invalid => invalid.Message,
        InvalidDataException { Message.Length: > 0 } unusable => unusable.Message,
        _ => "The update information could not be read. Try again in a moment."
    };
}
