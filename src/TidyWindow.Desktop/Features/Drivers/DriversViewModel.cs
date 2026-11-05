using System.Globalization;
using Microsoft.UI.Xaml;
using TidyWindow.Next.Core;
using Windows.System;

namespace TidyWindow.Next.Desktop;

/// <summary>One driver Windows Update offers, beside the driver it would replace.</summary>
public sealed class DriverRow(DriverFinding finding) : ObservableState
{
    private bool expanded, busy;
    private IReadOnlyList<StartupFact>? facts;

    public DriverFinding Finding { get; } = finding;
    private DriverOffer Offer => Finding.Offer;

    public string Title => Offer.Title;
    public string Device => Finding.Installed?.DeviceName is { Length: > 0 } name ? name : Offer.Model.Length > 0 ? Offer.Model : Offer.Title;
    public string Sub
    {
        get
        {
            var parts = new List<string>();
            if (Offer.DeviceClass.Length > 0) parts.Add(Offer.DeviceClass);
            if (Offer.Manufacturer.Length > 0) parts.Add(Offer.Manufacturer);
            if (Offer.Date is { } date) parts.Add(date.ToString("d MMM yyyy", CultureInfo.CurrentCulture));
            return string.Join(" · ", parts);
        }
    }

    public string InstalledText => Finding.Installed is { } installed ? "Installed: " + DriverCompare.DescribeInstalled(installed) : "No driver recorded for this device";

    public string VerdictText => Finding.Verdict switch
    {
        DriverVerdict.New => "New",
        DriverVerdict.Newer => "Newer",
        DriverVerdict.Same => "Same as installed",
        DriverVerdict.Older => "Older than installed",
        _ => "Not compared"
    };
    public Visibility GoodVisibility => Finding.Verdict is DriverVerdict.New or DriverVerdict.Newer ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WarnVisibility => GoodVisibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    public string ProblemText => $"Windows reports a problem with this device (code {Offer.ProblemCode}).";
    public Visibility ProblemVisibility => Offer.ProblemCode != 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RestartVisibility => Offer.RebootRequired ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>A driver older than the one in use gets no button: it is shown so the person knows Windows Update offered it, and why it is left alone.</summary>
    public Visibility InstallVisibility => Finding.CanInstall ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RefusedVisibility => Finding.CanInstall ? Visibility.Collapsed : Visibility.Visible;
    public string Reason => Finding.Reason;

    public bool Busy { get => busy; set { if (Set(ref busy, value)) { Changed(nameof(ButtonText)); } } }
    private bool available = true;
    /// <summary>False while the page is searching or installing: one driver is installed at a time, and nothing is started over a search.</summary>
    public bool Available { get => available; set => Set(ref available, value); }
    public string ButtonText => busy ? "Installing…" : "Install";
    public string AutomationName => $"Install {Title}";
    public string DetailsName => $"Details for {Title}";

    public bool Expanded => expanded;
    public Visibility DetailsVisibility => expanded ? Visibility.Visible : Visibility.Collapsed;
    public string Chevron => expanded ? "" : "";
    public void ToggleDetails() { expanded = !expanded; Changed(nameof(Expanded)); Changed(nameof(DetailsVisibility)); Changed(nameof(Chevron)); }

    public IReadOnlyList<StartupFact> Facts => facts ??= BuildFacts();

    private List<StartupFact> BuildFacts()
    {
        var list = new List<StartupFact>();
        void Add(string label, string value, bool mono = false) { if (value.Length > 0) list.Add(new(label, value, mono)); }
        Add("Compared", Finding.Reason);
        Add("Offered", $"{Offer.Title}" + (Offer.Date is { } date ? $", dated {date.ToString("d MMM yyyy", CultureInfo.CurrentCulture)}" : ""));
        Add("Installed", Finding.Installed is { } installed ? $"{installed.DeviceName}, {DriverCompare.DescribeInstalled(installed)}, from {(installed.Provider.Length > 0 ? installed.Provider : "an unknown maker")}" : "");
        Add("Download", Offer.SizeBytes > 0 ? $"About {Offer.SizeBytes / 1_000_000.0:0.#} MB from Windows Update" : "");
        Add("Restart", Offer.RebootRequired ? "Windows says it needs a restart after this driver." : "");
        Add("Saved first", Finding.Installed is { } current && DriverGuard.IsOemInf(current.InfName) ? "The driver in use is copied to a protected folder before anything changes, so it can be put back." : Finding.Installed is not null ? "The driver in use comes with Windows, which keeps it." : "");
        Add("Hardware id", Offer.HardwareId, mono: true);
        Add("Update id", Offer.UpdateId, mono: true);
        return list;
    }
}

/// <summary>A driver TidyWindow installed earlier, with the button that puts the previous one back when a saved copy exists.</summary>
public sealed class DriverHistoryRow(DriverChange change) : ObservableState
{
    private bool busy;
    public DriverChange Change { get; } = change;
    public string Title => Change.DeviceName.Length > 0 ? Change.DeviceName : Change.Title;
    public string Sub
    {
        get
        {
            var versions = Change.FromVersion is { Length: > 0 } from && Change.ToVersion is { Length: > 0 } to ? $" · {from} to {to}" : Change.ToVersion is { Length: > 0 } only ? $" · {only}" : "";
            return $"Installed {Change.When.ToLocalTime():d MMM yyyy, h:mm tt}{versions}" +(Change.Restored ? " · earlier driver put back" : Change.BackupId is null ? " · no saved copy to put back" : "");
        }
    }
    public bool Busy { get => busy; set { if (Set(ref busy, value)) Changed(nameof(CanRestore)); } }
    public bool CanRestore => !busy && Change is { Restored: false, BackupId: not null };
    public Visibility RestoreVisibility => Change is { Restored: false, BackupId: not null } ? Visibility.Visible : Visibility.Collapsed;
    public string AutomationName => $"Put back the earlier driver for {Title}";
}

/// <summary>A tool the PC or graphics maker publishes, with the ways to get it.</summary>
public sealed class DriverMakerRow(DriverMakerTool tool)
{
    public DriverMakerTool Tool { get; } = tool;
    public string Name => Tool.Name;
    public string Why => Tool.Why;
    public Visibility HubVisibility => Tool.WinGetId is null ? Visibility.Collapsed : Visibility.Visible;
    public string OpenName => $"Open the download page for {Name}";
    public string HubName => $"Find {Name} in Install Hub";
}

/// <summary>
/// The Drivers page. It lists what Windows Update offers (Microsoft's own list, the one in Settings under Optional updates), sets each offer beside the driver
/// it would replace, and installs one at a time through the administrator helper. A driver older than the one in use is never offered a button.
/// </summary>
public sealed class DriversViewModel : ObservableState, IDisposable
{
    private readonly DriverScanner scanner;
    private readonly DriverManager manager;
    private readonly ActivityLog log;
    private readonly ToastCenter toasts;
    private readonly Func<string, Task> openLink;
    private readonly Action<string> findInInstallHub;
    private readonly Func<string, string, string, Task<bool>> confirm;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? job;
    private bool scanning, installing, scanned, local;
    private string checkedAt = "", error = "", heading = "", line = "";

    public DriversViewModel(DriverScanner scanner, DriverManager manager, ActivityLog log, ToastCenter toasts, Func<string, Task> openLink, Action<string> findInInstallHub, Func<string, string, string, Task<bool>> confirm)
    {
        this.scanner = scanner; this.manager = manager; this.log = log; this.toasts = toasts; this.openLink = openLink; this.findInInstallHub = findInInstallHub; this.confirm = confirm;
        RefreshHistory();
    }

    public BulkObservableCollection<DriverRow> Rows { get; } = new();
    public BulkObservableCollection<string> Notices { get; } = new();
    public BulkObservableCollection<DriverMakerRow> MakerTools { get; } = new();
    public BulkObservableCollection<DriverHistoryRow> History { get; } = new();

    public bool Busy => scanning || installing;
    /// <summary>Work that changes the PC, which closing the window would cut off.</summary>
    public bool Changing => installing;
    public bool CanScan => !Busy;
    public bool CanStop => Busy;
    public string ScanLabel => scanning ? "Checking…" : scanned ? "Check again" : "Check for drivers";
    public Visibility WorkVisibility => Busy ? Visibility.Visible : Visibility.Collapsed;
    public string WorkHeading => heading;
    public string WorkLine => line;
    public string StopLabel => installing ? "Stop" : "Cancel";
    public string StopTip => installing ? "A driver install can only be stopped before it starts to change anything." : "Ends the search";
    public string CheckedText => checkedAt;

    public Visibility NoticesVisibility => Notices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MakerVisibility => MakerTools.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HistoryVisibility => History.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ErrorVisibility => error.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string ErrorText => error;
    public Visibility ListVisibility => Rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>The card that holds the list or its empty message. While the first search runs there is neither, and an empty card would only be a stray line.</summary>
    public Visibility CardVisibility => Rows.Count > 0 || EmptyVisibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;

    public Visibility EmptyVisibility => !Busy && Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string EmptyGlyph => error.Length > 0 ? "" : scanned ? "" : "";
    public string EmptyTitle => error.Length > 0 ? "Windows Update could not be asked" : scanned ? "Windows Update has no drivers to offer" : "Check what Windows Update offers";
    public string EmptyText => error.Length > 0 ? error
        : scanned ? "Nothing is waiting. Windows installs most drivers by itself."
        : "Looks only at drivers Microsoft has tested for this PC.";

    public string Summary
    {
        get
        {
            if (!scanned || Rows.Count == 0) return "";
            var worth = Rows.Count(x => DriverCompare.IsActionable(x.Finding));
            return worth == Rows.Count ? $"{Rows.Count:N0} offered" : $"{Rows.Count:N0} offered · {worth:N0} worth installing";
        }
    }

    private void Notify()
    {
        foreach (var row in Rows) row.Available = !Busy;
        foreach (var name in new[]
        {
            nameof(Busy), nameof(Changing), nameof(CanScan), nameof(CanStop), nameof(ScanLabel), nameof(WorkVisibility), nameof(WorkHeading), nameof(WorkLine), nameof(StopLabel), nameof(StopTip),
            nameof(EmptyVisibility), nameof(EmptyGlyph), nameof(EmptyTitle), nameof(EmptyText), nameof(ErrorVisibility), nameof(ErrorText), nameof(ListVisibility), nameof(CardVisibility), nameof(Summary), nameof(CheckedText),
            nameof(NoticesVisibility), nameof(MakerVisibility), nameof(HistoryVisibility)
        }) Changed(name);
    }

    /// <summary>The quick part of opening the page: the maker tools for this PC, any policy that limits drivers, and what TidyWindow installed before. Windows Update is only asked on request.</summary>
    public async Task PrepareAsync()
    {
        RefreshHistory();
        if (local || lifetime.IsCancellationRequested) return;
        try
        {
            var (tools, notices) = await scanner.LocalAsync(lifetime.Token);
            MakerTools.Reset(tools.Select(x => new DriverMakerRow(x)));
            Notices.Reset(notices);
            local = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            log.Write(ActivityLevel.Warning, ActivityArea.App, "Drivers could not be read", ex.Message);
        }
        Notify();
    }

    private void RefreshHistory()
    {
        try { History.Reset(manager.History.All().Select(x => new DriverHistoryRow(x))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { History.Reset([]); }
        Changed(nameof(HistoryVisibility));
    }

    public async Task ScanAsync()
    {
        if (Busy || lifetime.IsCancellationRequested) return;
        scanning = true; error = ""; heading = "Asking Windows Update"; line = "This usually takes under a minute, and sometimes several."; Notify();
        using var scan = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        job = scan;
        try
        {
            var result = await scanner.ScanAsync(scan.Token);
            Notices.Reset(result.Notices);
            error = result.Error ?? "";
            if (result.Error is null)
            {
                Rows.Reset(result.Findings.Select(x => new DriverRow(x)));
                scanned = true;
                checkedAt = $"Checked {DateTime.Now:h:mm tt}";
                local = true;
                MakerTools.Reset((await scanner.LocalAsync(scan.Token)).Tools.Select(x => new DriverMakerRow(x)));
                log.Write(ActivityLevel.Info, ActivityArea.App, "Driver check finished", $"Windows Update offers {result.Findings.Count} driver{(result.Findings.Count == 1 ? "" : "s")}.");
            }
            else log.Write(ActivityLevel.Warning, ActivityArea.App, "Driver check could not finish", result.Error);
        }
        catch (OperationCanceledException) { error = ""; }
        catch (Exception ex)
        {
            error = ex.Message;
            log.Write(ActivityLevel.Error, ActivityArea.App, "Driver check failed", ex.ToString());
        }
        finally { job = null; scanning = false; Notify(); }
    }

    /// <summary>Ends a search, or asks a running install to stop. An install honours that only before it has started to change anything.</summary>
    public void Stop()
    {
        try { job?.Cancel(); } catch (ObjectDisposedException) { }
    }

    public static string Warning(DriverRow row)
    {
        var finding = row.Finding;
        var lines = new List<string> { $"{row.Title}\nFor: {row.Device}\n{row.InstalledText}" };
        if (finding.Verdict is DriverVerdict.Same or DriverVerdict.Unknown) lines.Add(finding.Reason + " Installing it may change nothing.");
        lines.Add(finding.Installed is { } current && DriverGuard.IsOemInf(current.InfName)
            ? "The driver in use is saved first, so you can put it back from this page."
            : finding.Installed is not null ? "The driver in use comes with Windows, which keeps it." : "There is no driver in use to save.");
        lines.Add("Windows asks for administrator permission, then downloads and installs it. That can take a few minutes. Keep the PC on and do not close TidyWindow." + (row.Finding.Offer.RebootRequired ? " Windows will need a restart afterwards." : ""));
        if (finding.Offer.DeviceClass.Equals("Display", StringComparison.OrdinalIgnoreCase)) lines.Add("The screen may go black for a moment while a graphics driver installs.");
        return string.Join("\n\n", lines);
    }

    public async Task InstallAsync(DriverRow row)
    {
        if (Busy || !row.Finding.CanInstall) return;
        if (!await confirm("Install this driver?", Warning(row), "Install")) return;
        installing = true; row.Busy = true; heading = row.Title; line = "Waiting for administrator permission…"; Notify();
        using var work = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        job = work;
        var say = new Progress<string>(text => { line = text; Changed(nameof(WorkLine)); });
        DriverResult? result = null;
        try
        {
            result = await manager.InstallAsync(row.Finding, say, work.Token);
            if (result.Ok)
            {
                toasts.Show("drivers", result.Verified ? ToastKind.Success : ToastKind.Info, result.Verified ? $"{row.Device}: driver installed" : $"{row.Device}: driver sent to Windows",
                    result.Note ?? "", seconds: 15);
                log.Write(ActivityLevel.Success, ActivityArea.App, "Driver installed", $"{row.Title}. {result.Note}".TrimEnd());
            }
            else
            {
                toasts.Show("drivers", ToastKind.Warning, $"{row.Device}: driver not installed", ToastCenter.Flat(result.Error ?? "Windows Update did not install it."), seconds: 20);
                log.Write(ActivityLevel.Warning, ActivityArea.App, "Driver not installed", $"{row.Title}: {result.Error}");
            }
        }
        catch (OperationCanceledException) { toasts.Show("drivers", ToastKind.Info, "Nothing was installed", "Administrator permission was declined, or the install was stopped before it began.", seconds: 10); }
        catch (Exception ex)
        {
            toasts.Show("drivers", ToastKind.Warning, "The driver could not be installed", ToastCenter.Flat(ex.Message), seconds: 20);
            log.Write(ActivityLevel.Error, ActivityArea.App, "Driver install failed", ex.ToString());
        }
        finally { job = null; installing = false; row.Busy = false; Notify(); }
        RefreshHistory();
        // What is installed has changed, so what Windows Update offers is read again.
        if (result is { Ok: true }) await ScanAsync();
    }

    public async Task RestoreAsync(DriverHistoryRow row)
    {
        if (Busy || !row.CanRestore) return;
        if (!await confirm("Put the earlier driver back?", $"{row.Title}\n\nTidyWindow puts the driver it saved before the update back into Windows and removes the one the update added. Windows asks for administrator permission. If the device has been given a different driver since, it will say so and change nothing.", "Put back")) return;
        installing = true; row.Busy = true; heading = row.Title; line = "Waiting for administrator permission…"; Notify();
        using var work = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        job = work;
        var say = new Progress<string>(text => { line = text; Changed(nameof(WorkLine)); });
        try
        {
            var result = await manager.RestoreAsync(row.Change, say, work.Token);
            if (result.Ok)
            {
                toasts.Show("drivers", result.Verified ? ToastKind.Success : ToastKind.Info, result.Verified ? "Earlier driver put back" : "Earlier driver sent to Windows", result.Note ?? "", seconds: 15);
                log.Write(ActivityLevel.Success, ActivityArea.App, "Earlier driver put back", $"{row.Title}. {result.Note}".TrimEnd());
            }
            else
            {
                toasts.Show("drivers", ToastKind.Warning, "The earlier driver was not put back", ToastCenter.Flat(result.Error ?? "Windows did not accept it."), seconds: 20);
                log.Write(ActivityLevel.Warning, ActivityArea.App, "Earlier driver not put back", $"{row.Title}: {result.Error}");
            }
        }
        catch (OperationCanceledException) { toasts.Show("drivers", ToastKind.Info, "Nothing was changed", "Administrator permission was declined, or it was stopped before it began.", seconds: 10); }
        catch (Exception ex)
        {
            toasts.Show("drivers", ToastKind.Warning, "The earlier driver could not be put back", ToastCenter.Flat(ex.Message), seconds: 20);
            log.Write(ActivityLevel.Error, ActivityArea.App, "Driver restore failed", ex.ToString());
        }
        finally { job = null; installing = false; row.Busy = false; Notify(); }
        RefreshHistory();
    }

    public Task OpenToolAsync(DriverMakerRow row) => openLink(row.Tool.Url);

    public void FindToolInInstallHub(DriverMakerRow row) { if (row.Tool.WinGetId is { } id) findInInstallHub(id); }

    public async Task OpenOptionalUpdatesAsync()
    {
        try { await Launcher.LaunchUriAsync(new Uri("ms-settings:windowsupdate-optionalupdates")); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UriFormatException) { /* Settings could not be opened; nothing else depends on it. */ }
    }

    public void Dispose()
    {
        try { lifetime.Cancel(); } catch (ObjectDisposedException) { }
        lifetime.Dispose();
    }
}
