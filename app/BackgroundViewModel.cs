using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

public enum BackgroundTab { Telemetry, Ai, Optional, Changes }
public enum BackgroundFilter { All, On, Off }

public sealed class BackgroundRow(BackgroundEntry entry) : ObservableState
{
    private BackgroundEntry current = entry;
    private bool busy, expanded, reverted;
    private IReadOnlyList<StartupFact>? facts;

    public BackgroundEntry Entry => current;
    public string Id => current.Id;
    public string Name => current.Item.Name;
    // Two-way only so the switch stays attached to this value after the person flips it; the change itself is made by the page, and the switch snaps to the real state when this is raised.
    public bool Enabled { get => current.On; set { } }
    public bool CanToggle => current.CanChange && !busy;
    public bool Busy { get => busy; set { if (Set(ref busy, value)) Changed(nameof(CanToggle)); } }
    // Items that are off stay in the list, but step back so the ones still active are easy to spot.
    public double Dim => current.On ? 1 : 0.55;

    public string Glyph => GlyphFor(current.Item);

    /// <summary>The icon the catalog gives an item, or one for its group when it names none.</summary>
    public static string GlyphFor(BackgroundItem item) => char.ConvertFromUtf32(int.TryParse(item.Icon, System.Globalization.NumberStyles.HexNumber, null, out var code) ? code : item.Group switch
    {
        BackgroundGroup.Telemetry => 0xE774,
        BackgroundGroup.Ai => 0xE7B3,
        _ => 0xE713
    });

    public string Sub => current.Item.Topic.Length > 0 ? $"{current.Item.Topic} · {Parts}" : Parts;

    private string Parts
    {
        get
        {
            var present = current.Targets.Where(x => x.Present).Select(x => x.Target.Kind).ToList();
            var pieces = new List<string>();
            void Add(BackgroundTargetKind kind, string one, string many) { var n = present.Count(x => x == kind); if (n > 0) pieces.Add(n == 1 ? $"1 {one}" : $"{n} {many}"); }
            Add(BackgroundTargetKind.Service, "service", "services");
            Add(BackgroundTargetKind.Task, "scheduled task", "scheduled tasks");
            Add(BackgroundTargetKind.Policy, "Windows setting", "Windows settings");
            return string.Join(", ", pieces);
        }
    }

    public string Note => current.On ? current.Lock ?? "" : "";
    public Visibility NoteVisibility => current.On && current.Lock is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LockVisibility => NoteVisibility;
    public Visibility ToggleVisibility => current.On && current.Lock is not null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility RunningVisibility => current.Running ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CarefulVisibility => current.Item.Risk == BackgroundRisk.Careful ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PartlyVisibility => current.On && !current.FullyOn ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RevertedVisibility => reverted && current.FullyOn ? Visibility.Visible : Visibility.Collapsed;
    public string AutomationName => $"{Name} is allowed in the background";
    public string DetailsName => $"Details for {Name}";

    /// <summary>TidyWindow turned this off earlier and something has switched it back on, as a Windows update sometimes does.</summary>
    public bool Reverted { get => reverted; set { if (Set(ref reverted, value)) Changed(nameof(RevertedVisibility)); } }

    public bool Expanded => expanded;
    public Visibility DetailsVisibility => expanded ? Visibility.Visible : Visibility.Collapsed;
    public string Chevron => expanded ? "" : "";
    public void ToggleDetails() { expanded = !expanded; Changed(nameof(Expanded)); Changed(nameof(DetailsVisibility)); Changed(nameof(Chevron)); }

    /// <summary>The small print, kept out of sight until the row is opened.</summary>
    public IReadOnlyList<StartupFact> Facts => facts ??= BuildFacts();

    private List<StartupFact> BuildFacts()
    {
        var list = new List<StartupFact>();
        void Add(string label, string value, bool mono = false) { if (value.Length > 0) list.Add(new(label, value, mono)); }
        var item = current.Item;
        Add("What it does", item.Summary);
        Add("If you turn it off", item.Effect);
        Add("Right now", current.Running ? "Running" : current.On ? (current.FullyOn ? "Allowed, but not running" : "Partly turned off") : "Turned off");
        Add("Seen as", string.Join(", ", item.Processes));
        Add("Includes", string.Join("\n", current.Targets.Where(x => x.Present).Select(Describe)), true);
        if (current.Lock is { } rule && current.On) Add("Why it is locked", rule);
        return list;
    }

    private static string Describe(BackgroundTargetState state) => state.Target.Kind switch
    {
        BackgroundTargetKind.Service => $"Service  {state.Target.Name}{(state.On ? "" : "  (off)")}",
        BackgroundTargetKind.Task => $"Task  {state.Target.Name}{(state.On ? "" : "  (off)")}",
        _ => $"Setting  {state.Target.Name}{(state.On ? "" : "  (set)")}"
    };

    public void Update(BackgroundEntry updated)
    {
        current = updated; facts = null;
        foreach (var name in new[]
        {
            nameof(Enabled), nameof(Entry), nameof(Sub), nameof(RunningVisibility), nameof(CanToggle), nameof(Dim), nameof(Facts), nameof(Note), nameof(NoteVisibility),
            nameof(LockVisibility), nameof(ToggleVisibility), nameof(PartlyVisibility), nameof(RevertedVisibility)
        }) Changed(name);
    }
}

public sealed class BackgroundChangeRow(BackgroundChange change, string glyph, IconQuery? query) : ObservableState
{
    private ImageSource? icon;
    public BackgroundChange Change { get; } = change;
    /// <summary>The app's own icon when one is found, else the same category icon the item has in its list.</summary>
    public ImageSource? Icon => icon;
    public string Glyph { get; } = glyph;
    public IconQuery? IconQuery { get; } = query;
    public void SetIcon(ImageSource? image) { icon = image; Changed(nameof(Icon)); }
    public string Name => Change.Name;
    public string Sub => $"Turned off {Change.When.ToLocalTime():d MMM yyyy, h:mm tt}";
    public string AutomationName => $"Turn {Name} back on";
}

public sealed class BackgroundViewModel(IBackgroundSystem system, IReadOnlyList<BackgroundItem> catalog, BackgroundManager manager, ActivityLog log, ToastCenter toasts, IconService? iconService = null) : ObservableState, IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly IconService icons = iconService ?? IconService.Shared;
    private bool scanning, scanned, working, listening;
    private string query = "", checkedAt = "";
    private BackgroundTab tab = BackgroundTab.Telemetry;
    private BackgroundFilter filter = BackgroundFilter.All;
    private IReadOnlyList<BackgroundRow> all = [];
    private int notOnThisPc;

    /// <summary>The rows of the open tab, already narrowed by the search and the On/Off filter.</summary>
    public BulkObservableCollection<BackgroundRow> Rows { get; } = new();
    public BulkObservableCollection<BackgroundChangeRow> Changes { get; } = new();

    public bool Scanned => scanned;
    public bool Busy => scanning || working;
    public bool CanScan => !Busy;
    public bool CanRestore => !Busy && Changes.Count > 0;
    public string ScanLabel => scanning ? "Checking…" : "Refresh";
    public string CheckedText => checkedAt;
    public bool ScanOverlay => scanning && all.Count == 0;

    public string TelemetryCount => Count(BackgroundGroup.Telemetry);
    public string AiCount => Count(BackgroundGroup.Ai);
    public string OptionalCount => Count(BackgroundGroup.Optional);
    public string ChangesCount => Changes.Count.ToString("N0");
    private string Count(BackgroundGroup group) => scanned ? all.Count(x => x.Entry.Item.Group == group).ToString("N0") : "–";

    /// <summary>One line on how much is active, shown above the list.</summary>
    public string Summary
    {
        get
        {
            if (!scanned) return "";
            var running = all.Count(x => x.Entry.Running);
            var allowed = all.Count(x => x.Entry.On);
            return $"{running:N0} running now · {allowed:N0} of {all.Count:N0} allowed · {all.Count - allowed:N0} turned off";
        }
    }

    public string TabNote => tab switch
    {
        BackgroundTab.Telemetry => "Diagnostic and usage data sent to Microsoft. Nothing you use depends on it, so this is the safest group.",
        BackgroundTab.Ai => "Features that watch your screen or run AI on this PC. Turning one off does not uninstall it.",
        BackgroundTab.Optional => "Features many people never use. Each one says what you would lose, and Windows can still start the ones set to “when needed”.",
        _ => ""
    };

    public Visibility ListVisibility => tab == BackgroundTab.Changes ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ChangesVisibility => tab == BackgroundTab.Changes ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyVisibility => scanned && tab != BackgroundTab.Changes && Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoChangesVisibility => Changes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string EmptyGlyph => query.Length > 0 ? "" : "";
    public string EmptyTitle => query.Length > 0 ? "No matches" : filter switch { BackgroundFilter.On => "Nothing is allowed", BackgroundFilter.Off => "Nothing is turned off", _ => "Nothing here on this PC" };
    public string EmptyText => query.Length > 0 ? "Try a different name or the name you saw in Task Manager." : filter switch
    {
        BackgroundFilter.On => "Everything in this list is already turned off.",
        BackgroundFilter.Off => "Everything in this list is still allowed to run.",
        _ => "None of these features exist on this PC."
    };

    public string FooterNote => $"Some things are never offered here because other apps depend on them: Windows Update, Windows Search, notifications, audio, printing, Microsoft Edge updates and security services. Other companies' background apps are on the Startup page.{(notOnThisPc > 0 ? $" {notOnThisPc:N0} more known items are not on this PC." : "")}";

    public string Query { get => query; set { if (Set(ref query, value)) ApplyFilter(); } }
    public BackgroundTab Tab { get => tab; set { if (Set(ref tab, value)) ApplyFilter(); } }
    public BackgroundFilter Filter { get => filter; set { if (Set(ref filter, value)) ApplyFilter(); } }

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
            var scan = await Task.Run(() => BackgroundScan.Run(system, catalog), lifetime.Token);
            all = scan.Entries.Select(x => new BackgroundRow(x)).ToList();
            notOnThisPc = scan.NotOnThisPc;
            scanned = true;
            foreach (var change in manager.History.All())
            {
                // Gone from this PC (a Windows feature was removed): nothing to turn back on.
                if (all.FirstOrDefault(x => x.Id == change.Id) is not { } row) manager.History.Remove(change.Id);
                else row.Reverted = row.Entry.FullyOn;
            }
            checkedAt = $"Checked {DateTime.Now:h:mm tt}";
            ApplyFilter();
            RefreshChanges();
            foreach (var name in new[] { nameof(FooterNote), nameof(CheckedText), nameof(Summary) }) Changed(name);
            log.Write(ActivityLevel.Info, ActivityArea.App, "Background scan finished", $"{all.Count} items on this PC; {all.Count(x => x.Entry.Running)} running.");
            var back = all.Where(x => x.Reverted).ToList();
            if (back.Count > 0) toasts.Show("background-reverted", ToastKind.Info, back.Count == 1 ? $"{back[0].Name} was turned back on" : $"{back.Count} items were turned back on", "Something, such as a Windows update, switched them back on. You can turn them off again here.", seconds: 10);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            toasts.Show("background-scan", ToastKind.Warning, "Background items could not be read", ToastCenter.Brief(ex.Message));
            log.Write(ActivityLevel.Warning, ActivityArea.App, "Background scan could not finish", ex.Message);
        }
        finally { scanning = false; Notify(); }
    }

    private void ApplyFilter()
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Matches(BackgroundRow row) => words.All(w =>
            row.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase) || row.Sub.Contains(w, StringComparison.CurrentCultureIgnoreCase) ||
            row.Entry.Item.Summary.Contains(w, StringComparison.CurrentCultureIgnoreCase) || row.Entry.Item.Processes.Any(p => p.Contains(w, StringComparison.CurrentCultureIgnoreCase)) ||
            row.Entry.Targets.Any(t => t.Target.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase)));
        bool Wanted(BackgroundRow row) => filter switch { BackgroundFilter.On => row.Enabled, BackgroundFilter.Off => !row.Enabled, _ => true };
        var group = tab switch { BackgroundTab.Telemetry => BackgroundGroup.Telemetry, BackgroundTab.Ai => BackgroundGroup.Ai, _ => BackgroundGroup.Optional };
        var inTab = tab == BackgroundTab.Changes ? [] : all.Where(x => x.Entry.Item.Group == group);
        // What is still active comes first, running before idle, then A to Z.
        Rows.Reset(inTab.Where(Matches).Where(Wanted).OrderByDescending(x => x.Enabled).ThenByDescending(x => x.Entry.Running).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
        foreach (var name in new[]
        {
            nameof(TelemetryCount), nameof(AiCount), nameof(OptionalCount), nameof(TabNote), nameof(Summary),
            nameof(ListVisibility), nameof(ChangesVisibility), nameof(EmptyVisibility), nameof(EmptyGlyph), nameof(EmptyTitle), nameof(EmptyText)
        }) Changed(name);
    }

    public void RefreshChanges()
    {
        // An item Windows has already switched back on is not "turned off" any more; it shows in its list with a notice instead.
        var stillOff = manager.History.All().Where(x => all.FirstOrDefault(r => r.Id == x.Id) is not { Entry.FullyOn: true }).Select(x =>
        {
            var item = catalog.FirstOrDefault(c => string.Equals(c.Id, x.Id, StringComparison.OrdinalIgnoreCase));
            // An item the catalog no longer lists keeps the generic Optional icon.
            return new BackgroundChangeRow(x, item is null ? "" : BackgroundRow.GlyphFor(item), item is null ? null : AppIconQuery(item));
        }).ToList();
        foreach (var row in stillOff) if (row.IconQuery is { } query && icons.TryGet(query, out var cached) && cached is not null) row.SetIcon(cached);
        Changes.Reset(stillOff);
        foreach (var name in new[] { nameof(ChangesCount), nameof(NoChangesVisibility), nameof(CanRestore) }) Changed(name);
        if (!listening) { IconService.DarkChanged += ReloadIcons; listening = true; }
        _ = LoadIconsAsync(stillOff);
    }

    private void ReloadIcons() => _ = LoadIconsAsync(Changes.ToList(), everyRow: true);

    // Most items are parts of Windows that run from the Windows folder, which have no icon of their own; only a program installed elsewhere (a media player's sharing service, say) is worth looking up.
    private IconQuery? AppIconQuery(BackgroundItem item)
    {
        foreach (var target in item.Targets)
        {
            if (target.Kind != BackgroundTargetKind.Service || system.Service(target.Name) is not { ImagePath.Length: > 0 } service) continue;
            if (!BackgroundGuard.IsInWindows(service.ImagePath, system.WindowsFolder)) return new(item.Name, service.ImagePath);
        }
        return null;
    }

    private async Task LoadIconsAsync(IReadOnlyList<BackgroundChangeRow> rows, bool everyRow = false)
    {
        try
        {
            await Task.WhenAll(rows.Where(x => x.IconQuery is not null && (everyRow || x.Icon is null)).Select(async row =>
            {
                if (await icons.LoadAsync(row.IconQuery!, lifetime.Token) is { } image) row.SetIcon(image);
            }));
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>What turning this off will mean, shown before the person confirms. Null when no confirmation is needed.</summary>
    public static string? Warning(BackgroundRow row) => row.Entry.Item.Risk == BackgroundRisk.Careful
        ? row.Entry.Item.Effect + " You can turn it back on from this page at any time."
        : null;

    /// <summary>Turns one item on or off. Returns false if nothing changed, so the switch can go back to where it was.</summary>
    public async Task<bool> SetAsync(BackgroundRow row, bool enable)
    {
        if (Busy || !row.Entry.CanChange || row.Enabled == enable) return false;
        working = true; row.Busy = true; Notify();
        var changed = false;
        try
        {
            var outcome = await manager.SetAsync(row.Entry, enable);
            if (outcome.Ok)
            {
                changed = true;
                if (await Task.Run(() => BackgroundScan.Find(system, catalog, row.Id)) is { } after) row.Update(after);
                row.Reverted = false;
                if (enable) toasts.Show("background", ToastKind.Success, $"{row.Name} turned back on", outcome.Note ?? "");
                else toasts.Show("background", ToastKind.Success, $"{row.Name} turned off", outcome.Note ?? "You can turn it back on any time.", "Turn back on", () => SetAsync(row, true), 12);
                log.Write(ActivityLevel.Success, ActivityArea.App, enable ? "Background item turned on" : "Background item turned off", $"{row.Name}.{(outcome.Note is { } note ? " " + note : "")}");
            }
            else
            {
                toasts.Show("background", ToastKind.Warning, $"{row.Name} could not be changed", ToastCenter.Brief(outcome.Error ?? "Windows did not accept the change."));
                log.Write(ActivityLevel.Warning, ActivityArea.App, "Background item could not be changed", $"{row.Name}: {outcome.Error}");
                if (await Task.Run(() => BackgroundScan.Find(system, catalog, row.Id)) is { } current) row.Update(current);
            }
        }
        catch (OperationCanceledException)
        {
            toasts.Show("background", ToastKind.Info, "Administrator permission was declined", "Nothing was changed.");
            log.Write(ActivityLevel.Info, ActivityArea.App, "Background change stopped", "Administrator permission was declined.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            toasts.Show("background", ToastKind.Warning, $"{row.Name} could not be changed", ToastCenter.Brief(ex.Message));
            log.Write(ActivityLevel.Error, ActivityArea.App, "Background item could not be changed", ex.Message);
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

    public Task<bool> TurnBackOnAsync(BackgroundChangeRow change) =>
        all.FirstOrDefault(x => x.Id == change.Change.Id) is { } row ? SetAsync(row, true) : Task.FromResult(false);

    /// <summary>Turns back on everything TidyWindow turned off.</summary>
    public async Task RestoreAllAsync()
    {
        if (Busy) return;
        working = true; Notify();
        try
        {
            var results = await manager.RestoreAllAsync(system, catalog);
            var done = results.Count(x => x.Outcome.Ok);
            if (results.Count == 0) toasts.Show("background", ToastKind.Info, "Nothing to turn back on");
            else if (done == results.Count) toasts.Show("background", ToastKind.Success, done == 1 ? "1 item turned back on" : $"{done} items turned back on");
            else toasts.Show("background", ToastKind.Warning, $"{done} of {results.Count} turned back on", ToastCenter.Brief(string.Join(" ", results.Where(x => !x.Outcome.Ok).Select(x => $"{x.Change.Name}: {x.Outcome.Error}"))), seconds: 15);
            log.Write(ActivityLevel.Info, ActivityArea.App, "Background items turned back on", $"{done} of {results.Count}.");
        }
        catch (OperationCanceledException) { toasts.Show("background", ToastKind.Info, "Administrator permission was declined", "Nothing was changed."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            toasts.Show("background", ToastKind.Warning, "Could not turn everything back on", ToastCenter.Brief(ex.Message));
            log.Write(ActivityLevel.Error, ActivityArea.App, "Background restore could not finish", ex.Message);
        }
        finally { working = false; Notify(); }
        await ScanAsync();
    }

    public void Dispose() { IconService.DarkChanged -= ReloadIcons; lifetime.Cancel(); lifetime.Dispose(); }
}
