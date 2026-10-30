using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

public sealed record StartupFact(string Label, string Value, bool Mono)
{
    public Visibility MonoVisibility => Mono ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PlainVisibility => Mono ? Visibility.Collapsed : Visibility.Visible;
}

public enum StartupTab { Apps, Background, Protected, Changes }
public enum StartupFilter { All, On, Off }

public sealed class StartupRow(StartupEntry entry) : ObservableState
{
    private StartupEntry current = entry;
    private bool busy, expanded;
    private ImageSource? icon;
    private IReadOnlyList<StartupFact>? facts;

    public StartupEntry Entry => current;
    public string Id => current.Id;
    public string Name => current.Name;
    // Two-way only so the switch stays attached to this value after the person flips it; the change itself is made by the page, and the switch snaps to the real state when this is raised.
    public bool Enabled { get => current.Enabled; set { } }
    public bool CanToggle => current.CanChange && !busy;
    public bool Busy { get => busy; set { if (Set(ref busy, value)) Changed(nameof(CanToggle)); } }
    // Items that are off stay in the list, but step back so the ones that still start are easy to spot.
    public double Dim => current.Enabled ? 1 : 0.55;

    public string Kind => current.Kind switch
    {
        StartupKind.RunKey => "Startup entry",
        StartupKind.StartupFolder => "Startup folder",
        StartupKind.ScheduledTask => "Scheduled task",
        StartupKind.Service => "Background service",
        StartupKind.StoreApp => "Store app",
        _ => "Windows setting"
    };

    private string Publisher => current.Publisher.Length > 0 ? current.Publisher : current.Kind == StartupKind.WindowsSetting ? "Windows" : "Unknown publisher";
    public string Sub => $"{Publisher} · {Kind}";
    // Locked items say why right on the row, so nobody has to guess why there is no switch.
    public string Note => current.Reason;
    public Visibility NoteVisibility => current.CanChange ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CarefulVisibility => current.Risk == StartupRisk.Careful ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProtectedVisibility => current.CanChange ? Visibility.Collapsed : Visibility.Visible;
    public Visibility MissingVisibility => current.FileMissing ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ToggleVisibility => current.CanChange ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LockVisibility => ProtectedVisibility;
    public Visibility RunningVisibility => current.Running ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AdminVisibility => current.CanChange && current.Scope == StartupScope.Machine ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FolderVisibility => current.ExePath.Length > 0 && File.Exists(current.ExePath) ? Visibility.Visible : Visibility.Collapsed;
    public string AutomationName => $"{Name} starts with Windows";
    public string DetailsName => $"Details for {Name}";

    public string Initial => Name.Any(char.IsLetterOrDigit) ? char.ToUpperInvariant(Name.First(char.IsLetterOrDigit)).ToString() : "?";
    public ImageSource? Icon => icon;
    public Visibility IconVisibility => icon is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility InitialVisibility => icon is null ? Visibility.Visible : Visibility.Collapsed;
    public void SetIcon(ImageSource? image) { icon = image; Changed(nameof(Icon)); Changed(nameof(IconVisibility)); Changed(nameof(InitialVisibility)); }

    public bool Expanded => expanded;
    public Visibility DetailsVisibility => expanded ? Visibility.Visible : Visibility.Collapsed;
    public string Chevron => expanded ? "\uE70E" : "\uE70D";
    public void ToggleDetails() { expanded = !expanded; Changed(nameof(Expanded)); Changed(nameof(DetailsVisibility)); Changed(nameof(Chevron)); }

    /// <summary>The small print, kept out of sight until the row is opened.</summary>
    public IReadOnlyList<StartupFact> Facts => facts ??= BuildFacts();

    private List<StartupFact> BuildFacts()
    {
        var list = new List<StartupFact>();
        void Add(string label, string value, bool mono = false) { if (value.Length > 0) list.Add(new(label, value, mono)); }
        if (!string.Equals(current.Description, Name, StringComparison.OrdinalIgnoreCase)) Add("About", current.Description);
        Add("Safety", current.Reason);
        Add("Runs", current.Command, true);
        Add("Where", current.Location);
        if (current.Kind != StartupKind.StoreApp) Add("Applies to", current.Scope == StartupScope.User ? "Just you" : "Everyone who uses this PC");
        Add("Signature", current.Trust switch
        {
            StartupTrust.Verified => "Signed, and the signature checks out",
            StartupTrust.Unsigned => "Not signed",
            StartupTrust.Invalid => "The signature does not check out",
            _ => ""
        });
        return list;
    }

    public void Update(StartupEntry updated)
    {
        current = updated; facts = null;
        foreach (var name in new[] { nameof(Enabled), nameof(Entry), nameof(RunningVisibility), nameof(CanToggle), nameof(Dim), nameof(Facts) }) Changed(name);
    }
}

public sealed class ChangeRow(StartupChange change) : ObservableState
{
    public StartupChange Change { get; } = change;
    public string Name => Change.Name;
    public string When => Change.When.ToLocalTime().ToString("d MMM yyyy, h:mm tt");
    public string Kind => Change.Kind switch
    {
        StartupKind.ScheduledTask => "Scheduled task",
        StartupKind.Service => "Background service",
        StartupKind.StoreApp => "Microsoft Store app",
        StartupKind.StartupFolder => "Startup folder",
        _ => "Startup entry"
    };
    public string Sub => $"{Kind} · turned off {When}";
    public string Initial => Name.Any(char.IsLetterOrDigit) ? char.ToUpperInvariant(Name.First(char.IsLetterOrDigit)).ToString() : "?";
    public string AutomationName => $"Turn {Name} back on";
}

public sealed class StartupViewModel(IStartupSystem system, StartupService service, ActivityLog log, ToastCenter toasts) : ObservableState, IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, ImageSource?> icons = new(StringComparer.OrdinalIgnoreCase);
    private bool scanning, scanned, working;
    private string query = "", checkedAt = "";
    private StartupTab tab = StartupTab.Apps;
    private StartupFilter filter = StartupFilter.All;
    private IReadOnlyList<StartupRow> all = [];
    private int hiddenTasks, hiddenServices;

    /// <summary>The rows of the open tab, already narrowed by the search and the On/Off filter.</summary>
    public BulkObservableCollection<StartupRow> Rows { get; } = new();
    public BulkObservableCollection<ChangeRow> Changes { get; } = new();

    public bool Scanned => scanned;
    public bool Busy => scanning || working;
    public bool CanScan => !Busy;
    public bool CanRestore => !Busy && Changes.Count > 0;
    public string ScanLabel => scanning ? "Checking…" : "Refresh";
    public string CheckedText => checkedAt;
    public bool ScanOverlay => scanning && all.Count == 0;

    public string AppsCount => scanned ? Apps().Count().ToString("N0") : "–";
    public string BackgroundCount => scanned ? Background().Count().ToString("N0") : "–";
    public string ProtectedCount => scanned ? Locked().Count().ToString("N0") : "–";
    public string ChangesCount => Changes.Count.ToString("N0");

    public Visibility ListVisibility => tab == StartupTab.Changes ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ChangesVisibility => tab == StartupTab.Changes ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProtectedNoteVisibility => tab == StartupTab.Protected ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyVisibility => scanned && tab != StartupTab.Changes && Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoChangesVisibility => Changes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string EmptyGlyph => query.Length > 0 ? "" : "";
    public string EmptyTitle => query.Length > 0 ? "No matches" : filter switch { StartupFilter.On => "Nothing is turned on", StartupFilter.Off => "Nothing is turned off", _ => tab == StartupTab.Background ? "No background items" : "Nothing starts here" };
    public string EmptyText => query.Length > 0 ? "Try a different name, publisher or part of the command." : filter switch
    {
        StartupFilter.On => "Everything in this list is already switched off.",
        StartupFilter.Off => "Everything in this list still starts with Windows.",
        _ => tab == StartupTab.Background ? "No scheduled tasks or services from other companies start with Windows." : "No apps start with Windows."
    };
    public string HiddenNote => $"These belong to Windows or to security software, so they cannot be changed here. Also left out: {hiddenTasks:N0} Windows tasks and {hiddenServices:N0} Windows services.";

    public string Query { get => query; set { if (Set(ref query, value)) ApplyFilter(); } }
    public StartupTab Tab { get => tab; set { if (Set(ref tab, value)) ApplyFilter(); } }
    public StartupFilter Filter { get => filter; set { if (Set(ref filter, value)) ApplyFilter(); } }

    private IEnumerable<StartupRow> Apps() => all.Where(x => x.Entry.CanChange && x.Entry.Kind is StartupKind.RunKey or StartupKind.StartupFolder or StartupKind.StoreApp);
    private IEnumerable<StartupRow> Background() => all.Where(x => x.Entry.CanChange && x.Entry.Kind is StartupKind.ScheduledTask or StartupKind.Service);
    private IEnumerable<StartupRow> Locked() => all.Where(x => !x.Entry.CanChange);
    private void Notify()
    {
        foreach (var name in new[] { nameof(Busy), nameof(CanScan), nameof(CanRestore), nameof(ScanLabel), nameof(ScanOverlay), nameof(EmptyVisibility) }) Changed(name);
    }

    public async Task ScanAsync()
    {
        if (Busy || lifetime.IsCancellationRequested) return;
        scanning = true; Notify();
        try
        {
            var remembered = service.History.All();
            var services = remembered.Where(x => x.Kind == StartupKind.Service).Select(x => x.Id.Split('|')[^1]).ToList();
            var scan = await Task.Run(() => StartupScan.Run(system, services), lifetime.Token);
            all = scan.Entries.Select(x => new StartupRow(x)).ToList();
            hiddenTasks = scan.WindowsTasks; hiddenServices = scan.WindowsServices;
            scanned = true;
            // Anything turned back on outside TidyWindow no longer counts as turned off by it.
            foreach (var change in remembered)
                if (scan.Entries.FirstOrDefault(x => x.Id == change.Id) is not { Enabled: false }) service.History.Remove(change.Id);
            checkedAt = $"Checked {DateTime.Now:h:mm tt}";
            ApplyFilter();
            RefreshChanges();
            foreach (var name in new[] { nameof(HiddenNote), nameof(CheckedText) }) Changed(name);
            _ = LoadIconsAsync(all);
            log.Write(ActivityLevel.Info, ActivityArea.App, "Startup scan finished", $"{scan.Entries.Count} items; {scan.Entries.Count(x => x.CanChange && x.Enabled)} can be turned off.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            toasts.Show("startup-scan", ToastKind.Warning, "Startup could not be read", ToastCenter.Brief(ex.Message));
            log.Write(ActivityLevel.Warning, ActivityArea.App, "Startup scan could not finish", ex.Message);
        }
        finally { scanning = false; Notify(); }
    }

    private void ApplyFilter()
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Matches(StartupRow row) => words.All(w => row.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase) || row.Sub.Contains(w, StringComparison.CurrentCultureIgnoreCase) || row.Entry.Command.Contains(w, StringComparison.CurrentCultureIgnoreCase));
        bool Wanted(StartupRow row) => filter switch { StartupFilter.On => row.Enabled, StartupFilter.Off => !row.Enabled, _ => true };
        var inTab = tab switch { StartupTab.Apps => Apps(), StartupTab.Background => Background(), StartupTab.Protected => Locked(), _ => [] };
        // What still starts comes first, then A to Z.
        Rows.Reset(inTab.Where(Matches).Where(Wanted).OrderByDescending(x => x.Enabled).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
        foreach (var name in new[]
        {
            nameof(AppsCount), nameof(BackgroundCount), nameof(ProtectedCount),
            nameof(ListVisibility), nameof(ChangesVisibility), nameof(ProtectedNoteVisibility), nameof(EmptyVisibility), nameof(EmptyGlyph), nameof(EmptyTitle), nameof(EmptyText)
        }) Changed(name);
    }

    public void RefreshChanges()
    {
        Changes.Reset(service.History.All().Select(x => new ChangeRow(x)).ToList());
        foreach (var name in new[] { nameof(ChangesCount), nameof(NoChangesVisibility), nameof(CanRestore) }) Changed(name);
    }

    // Real icons make an unfamiliar list readable at a glance. They arrive a moment after the list, one at a time, and a row without one keeps its initial.
    private async Task LoadIconsAsync(IReadOnlyList<StartupRow> rows)
    {
        foreach (var row in rows)
        {
            if (lifetime.IsCancellationRequested) return;
            var path = row.Entry.ExePath;
            if (path.Length == 0) continue;
            if (!icons.TryGetValue(path, out var image)) icons[path] = image = await IconOf(path);
            if (image is not null) row.SetIcon(image);
        }
    }

    private static async Task<ImageSource?> IconOf(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 64, ThumbnailOptions.UseCurrentScale);
            if (thumbnail is null || thumbnail.Size == 0) return null;
            var image = new BitmapImage();
            await image.SetSourceAsync(thumbnail);
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException) { return null; }
    }

    /// <summary>What turning this off will mean, shown before the person confirms. Null when no confirmation is needed.</summary>
    public static string? Warning(StartupRow row) => row.Entry.Risk == StartupRisk.Careful
        ? row.Entry.Reason + (row.Entry.Kind == StartupKind.Service ? " Windows can still start it when an app asks for it." : "") + " You can turn it back on from this page at any time."
        : null;

    /// <summary>Turns one item on or off. Returns false if nothing changed, so the switch can go back to where it was.</summary>
    public async Task<bool> SetAsync(StartupRow row, bool enable)
    {
        if (Busy || !row.Entry.CanChange || row.Enabled == enable) return false;
        working = true; row.Busy = true; Notify();
        var changed = false;
        try
        {
            var outcome = await service.SetAsync(row.Entry, enable);
            if (outcome.Ok && outcome.Entry is { } after)
            {
                row.Update(after); changed = true;
                var note = !enable && row.Entry.Running ? " It is running now, so this takes effect the next time you start Windows." : "";
                if (enable) toasts.Show("startup", ToastKind.Success, $"{row.Name} will start with Windows");
                else toasts.Show("startup", ToastKind.Success, $"{row.Name} will not start with Windows", (row.Entry.Kind == StartupKind.Service ? "It starts only when something needs it. " : "") + "You can turn it back on any time." + note, "Turn back on", () => SetAsync(row, true), 12);
                log.Write(ActivityLevel.Success, ActivityArea.App, enable ? "Startup item turned on" : "Startup item turned off", $"{row.Name} ({row.Kind}).");
            }
            else
            {
                toasts.Show("startup", ToastKind.Warning, $"{row.Name} could not be changed", ToastCenter.Brief(outcome.Error ?? "Windows did not accept the change."));
                log.Write(ActivityLevel.Warning, ActivityArea.App, "Startup item could not be changed", $"{row.Name}: {outcome.Error}");
                if (outcome.Entry is { } current) row.Update(current);
            }
        }
        catch (OperationCanceledException)
        {
            toasts.Show("startup", ToastKind.Info, "Administrator permission was declined", "Nothing was changed.");
            log.Write(ActivityLevel.Info, ActivityArea.App, "Startup change stopped", "Administrator permission was declined.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            toasts.Show("startup", ToastKind.Warning, $"{row.Name} could not be changed", ToastCenter.Brief(ex.Message));
            log.Write(ActivityLevel.Error, ActivityArea.App, "Startup item could not be changed", ex.Message);
        }
        finally
        {
            row.Busy = false; working = false;
            RefreshChanges(); Notify();
            // A rescan is not needed for one switch; the filtered lists only need the new order.
            ApplyFilter();
        }
        return changed;
    }

    public Task<bool> TurnBackOnAsync(ChangeRow change) =>
        all.FirstOrDefault(x => x.Id == change.Change.Id) is { } row ? SetAsync(row, true) : Task.FromResult(false);

    /// <summary>Turns back on everything TidyWindow turned off.</summary>
    public async Task RestoreAllAsync()
    {
        if (Busy) return;
        working = true; Notify();
        try
        {
            var results = await service.RestoreAllAsync(system);
            var done = results.Count(x => x.Outcome.Ok);
            if (results.Count == 0) toasts.Show("startup", ToastKind.Info, "Nothing to turn back on");
            else if (done == results.Count) toasts.Show("startup", ToastKind.Success, done == 1 ? "1 item turned back on" : $"{done} items turned back on");
            else toasts.Show("startup", ToastKind.Warning, $"{done} of {results.Count} turned back on", ToastCenter.Brief(string.Join(" ", results.Where(x => !x.Outcome.Ok).Select(x => $"{x.Change.Name}: {x.Outcome.Error}"))), seconds: 15);
            log.Write(ActivityLevel.Info, ActivityArea.App, "Startup items turned back on", $"{done} of {results.Count}.");
        }
        catch (OperationCanceledException) { toasts.Show("startup", ToastKind.Info, "Administrator permission was declined", "Nothing was changed."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            toasts.Show("startup", ToastKind.Warning, "Could not turn everything back on", ToastCenter.Brief(ex.Message));
            log.Write(ActivityLevel.Error, ActivityArea.App, "Startup restore could not finish", ex.Message);
        }
        finally { working = false; Notify(); }
        await ScanAsync();
    }

    public static void ShowInFolder(StartupRow row)
    {
        if (row.Entry.ExePath.Length == 0 || !File.Exists(row.Entry.ExePath)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{row.Entry.ExePath}\"") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    public void Dispose() { lifetime.Cancel(); lifetime.Dispose(); }
}
