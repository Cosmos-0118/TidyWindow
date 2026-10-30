using Microsoft.UI.Xaml;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

public enum TuneTab { Speed, Cleaner, Comfort, Changes }
public enum TuneFilter { All, On, Off }

public sealed class TuneRow(TuneEntry entry) : ObservableState
{
    private TuneEntry current = entry;
    private bool busy, expanded, reverted;
    private IReadOnlyList<StartupFact>? facts;

    public TuneEntry Entry => current;
    public string Id => current.Id;
    public string Name => current.Item.Name;
    // Two-way only so the switch stays attached to this value after the person flips it; the change itself is made by the page, and the switch snaps to the real state when this is raised.
    public bool Enabled { get => current.Applied; set { } }
    public bool CanToggle => current.CanChange && !busy;
    public bool Busy { get => busy; set { if (Set(ref busy, value)) Changed(nameof(CanToggle)); } }

    public string Glyph => GlyphFor(current.Item);

    /// <summary>The icon the catalog gives an item, or one for its group when it names none.</summary>
    public static string GlyphFor(TuneItem item) => char.ConvertFromUtf32(int.TryParse(item.Icon, System.Globalization.NumberStyles.HexNumber, null, out var code) ? code : item.Group switch
    {
        TuneGroup.Speed => 0xE945,
        TuneGroup.Cleaner => 0xE74D,
        _ => 0xE713
    });

    public string Sub
    {
        get
        {
            var count = current.Targets.Count;
            var parts = count == 1 ? "1 setting" : $"{count} settings";
            return current.Item.Topic.Length > 0 ? $"{current.Item.Topic} · {parts}" : parts;
        }
    }

    public string Note => current.Lock is { } rule && !current.Applied ? rule : "";
    public Visibility NoteVisibility => Note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LockVisibility => current.Lock is not null && !current.CanChange ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ToggleVisibility => current.Lock is not null && !current.CanChange ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CarefulVisibility => current.Item.Risk == TuneRisk.Careful ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AdminVisibility => current.Item.Scope == TuneScope.Machine ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PartlyVisibility => current.Partly ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RevertedVisibility => reverted && !current.Applied && !current.Partly ? Visibility.Visible : Visibility.Collapsed;
    public string TakesText => Takes(current.Item.Takes);
    public Visibility TakesVisibility => current.Item.Takes == TuneEffect.Now ? Visibility.Collapsed : Visibility.Visible;
    public string AutomationName => $"{Name} is on";
    public string DetailsName => $"Details for {Name}";

    private static string Takes(TuneEffect effect) => effect switch
    {
        TuneEffect.Explorer => "Restarts Explorer",
        TuneEffect.SignOut => "After sign-in",
        TuneEffect.Restart => "After restart",
        _ => "At once"
    };

    /// <summary>TidyWindow changed this earlier and something has put it back, as a Windows update or a policy sometimes does.</summary>
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
        Add("What you notice", item.Summary);
        Add("What you give up", item.Effect);
        Add("Right now", current.Applied ? "On" : current.Partly ? "Partly on" : "Off");
        Add("Takes effect", item.Takes switch
        {
            TuneEffect.Explorer => "When File Explorer restarts. TidyWindow can do that for you.",
            TuneEffect.SignOut => "The next time you sign in.",
            TuneEffect.Restart => "The next time you restart Windows.",
            _ => "Straight away."
        });
        Add("Changes", item.Scope == TuneScope.Machine ? "Settings for every user. Needs administrator permission." : "Settings for your account only.");
        Add("Includes", string.Join("\n", current.Targets.Select(Describe)), true);
        Add("Source", string.Join(", ", item.Sources));
        if (current.Lock is { } rule) Add("Why it is locked", rule);
        return list;
    }

    private static string Describe(TuneTargetState state)
    {
        var target = state.Target;
        var name = target.Name.Length > 0 ? target.Name : "(Default)";
        var now = state.Current is { Kind: not TuneKind.Other } value ? (value.Text.Length > 0 ? value.Text : "empty") : state.Current is null ? "not set" : "other";
        return state.Applied ? $"{name}  {now}  (set)" : $"{name}  {now} → {(TuneRules.Wanted(target, state.Current) is { Length: > 0 } wanted ? wanted : "empty")}";
    }

    public void Update(TuneEntry updated)
    {
        current = updated; facts = null;
        foreach (var name in new[]
        {
            nameof(Enabled), nameof(Entry), nameof(CanToggle), nameof(Facts), nameof(Note), nameof(NoteVisibility), nameof(LockVisibility), nameof(ToggleVisibility), nameof(PartlyVisibility), nameof(RevertedVisibility)
        }) Changed(name);
    }
}

public sealed class TuneChangeRow(TuneChange change, string glyph) : ObservableState
{
    public TuneChange Change { get; } = change;
    public string Glyph { get; } = glyph;
    public string Name => Change.Name;
    public string Sub => $"Changed {Change.When.ToLocalTime():d MMM yyyy, h:mm tt}";
    public string AutomationName => $"Put {Name} back";
}

public sealed class TuneViewModel(ITuneSystem system, IReadOnlyList<TuneItem> catalog, TuneManager manager, ActivityLog log, ToastCenter toasts, Func<Task>? restartExplorer = null) : ObservableState, IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private bool scanning, scanned, working;
    private string query = "", checkedAt = "";
    private TuneTab tab = TuneTab.Speed;
    private TuneFilter filter = TuneFilter.All;
    private IReadOnlyList<TuneRow> all = [];
    private int notOnThisPc;

    /// <summary>The rows of the open tab, already narrowed by the search and the On/Off filter.</summary>
    public BulkObservableCollection<TuneRow> Rows { get; } = new();
    public BulkObservableCollection<TuneChangeRow> Changes { get; } = new();

    public bool Scanned => scanned;
    public bool Busy => scanning || working;
    public bool CanScan => !Busy;
    public bool CanRestore => !Busy && Changes.Count > 0;
    public string ScanLabel => scanning ? "Checking…" : "Refresh";
    public string CheckedText => checkedAt;
    public bool ScanOverlay => scanning && all.Count == 0;

    public string SpeedCount => Count(TuneGroup.Speed);
    public string CleanerCount => Count(TuneGroup.Cleaner);
    public string ComfortCount => Count(TuneGroup.Comfort);
    public string ChangesCount => Changes.Count.ToString("N0");
    private string Count(TuneGroup group) => scanned ? all.Count(x => x.Entry.Item.Group == group).ToString("N0") : "–";

    /// <summary>One line on how much is on, shown above the list.</summary>
    public string Summary
    {
        get
        {
            if (!scanned) return "";
            var on = all.Count(x => x.Entry.Applied);
            return $"{on:N0} of {all.Count:N0} on · {all.Count - on:N0} off";
        }
    }

    public string TabNote => tab switch
    {
        TuneTab.Speed => "Things you wait for: big folders, shutdown, menus, and Edge running when you are not using it.",
        TuneTab.Cleaner => "Stop Windows suggesting, installing and recording things you did not ask for.",
        TuneTab.Comfort => "Small annoyances fixed, so Windows behaves the way you expect.",
        _ => ""
    };

    public Visibility ListVisibility => tab == TuneTab.Changes ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ChangesVisibility => tab == TuneTab.Changes ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyVisibility => scanned && tab != TuneTab.Changes && Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoChangesVisibility => Changes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string EmptyGlyph => query.Length > 0 ? "" : "";
    public string EmptyTitle => query.Length > 0 ? "No matches" : filter switch { TuneFilter.On => "Nothing is on", TuneFilter.Off => "Nothing is off", _ => "Nothing here on this PC" };
    public string EmptyText => query.Length > 0 ? "Try a different word." : filter switch
    {
        TuneFilter.On => "None of these tweaks is on yet.",
        TuneFilter.Off => "Every tweak in this list is already on.",
        _ => "None of these settings exist on this PC."
    };

    public string FooterNote => $"Only settings with a clear, noticeable effect are listed. Popular “registry boosters” with no proof behind them are left out on purpose. Every change keeps the original value so it can be put back exactly.{(notOnThisPc > 0 ? $" {notOnThisPc:N0} more are for other versions of Windows." : "")}";

    public string Query { get => query; set { if (Set(ref query, value)) ApplyFilter(); } }
    public TuneTab Tab { get => tab; set { if (Set(ref tab, value)) ApplyFilter(); } }
    public TuneFilter Filter { get => filter; set { if (Set(ref filter, value)) ApplyFilter(); } }

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
            var scan = await Task.Run(() => TuneScan.Run(system, catalog), lifetime.Token);
            all = scan.Entries.Select(x => new TuneRow(x)).ToList();
            notOnThisPc = scan.NotOnThisPc;
            scanned = true;
            foreach (var change in manager.History.All())
            {
                // Gone from this PC (a Windows version without it): nothing to put back.
                if (all.FirstOrDefault(x => x.Id == change.Id) is not { } row) manager.History.Remove(change.Id);
                else row.Reverted = !row.Entry.Applied && !row.Entry.Partly;
            }
            checkedAt = $"Checked {DateTime.Now:h:mm tt}";
            ApplyFilter();
            RefreshChanges();
            foreach (var name in new[] { nameof(FooterNote), nameof(CheckedText), nameof(Summary) }) Changed(name);
            log.Write(ActivityLevel.Info, ActivityArea.App, "Tune scan finished", $"{all.Count} tweaks on this PC; {all.Count(x => x.Entry.Applied)} on.");
            var back = all.Where(x => x.Reverted).ToList();
            if (back.Count > 0) toasts.Show("tune-reverted", ToastKind.Info, back.Count == 1 ? $"{back[0].Name} was changed back" : $"{back.Count} tweaks were changed back", "Something, such as a Windows update, put them back. You can turn them on again here.", seconds: 10);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            toasts.Show("tune-scan", ToastKind.Warning, "Settings could not be read", ToastCenter.Brief(ex.Message));
            log.Write(ActivityLevel.Warning, ActivityArea.App, "Tune scan could not finish", ex.Message);
        }
        finally { scanning = false; Notify(); }
    }

    private void ApplyFilter()
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Matches(TuneRow row) => words.All(w =>
            row.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase) || row.Sub.Contains(w, StringComparison.CurrentCultureIgnoreCase) ||
            row.Entry.Item.Summary.Contains(w, StringComparison.CurrentCultureIgnoreCase) || row.Entry.Targets.Any(t => t.Target.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase)));
        bool Wanted(TuneRow row) => filter switch { TuneFilter.On => row.Enabled, TuneFilter.Off => !row.Enabled, _ => true };
        var group = tab switch { TuneTab.Speed => TuneGroup.Speed, TuneTab.Cleaner => TuneGroup.Cleaner, _ => TuneGroup.Comfort };
        var inTab = tab == TuneTab.Changes ? [] : all.Where(x => x.Entry.Item.Group == group);
        // What can still be switched on comes first, so there is something to do at the top; what is already on follows, A to Z.
        Rows.Reset(inTab.Where(Matches).Where(Wanted).OrderBy(x => x.Enabled).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
        foreach (var name in new[]
        {
            nameof(SpeedCount), nameof(CleanerCount), nameof(ComfortCount), nameof(TabNote), nameof(Summary),
            nameof(ListVisibility), nameof(ChangesVisibility), nameof(EmptyVisibility), nameof(EmptyGlyph), nameof(EmptyTitle), nameof(EmptyText)
        }) Changed(name);
    }

    public void RefreshChanges()
    {
        // An item something else has already put back is not "changed" any more; it shows in its list with a notice instead.
        var stillOn = manager.History.All().Where(x => all.FirstOrDefault(r => r.Id == x.Id) is not { Reverted: true }).Select(x =>
        {
            var item = catalog.FirstOrDefault(c => string.Equals(c.Id, x.Id, StringComparison.OrdinalIgnoreCase));
            return new TuneChangeRow(x, item is null ? "" : TuneRow.GlyphFor(item));
        }).ToList();
        Changes.Reset(stillOn);
        foreach (var name in new[] { nameof(ChangesCount), nameof(NoChangesVisibility), nameof(CanRestore) }) Changed(name);
    }

    /// <summary>What turning this on will mean, shown before the person confirms. Null when no confirmation is needed.</summary>
    public static string? Warning(TuneRow row) => row.Entry.Item.Risk == TuneRisk.Careful
        ? row.Entry.Item.Effect + " You can turn it off from this page at any time."
        : null;

    /// <summary>Turns one tweak on or off. Returns false if nothing changed, so the switch can go back to where it was.</summary>
    public async Task<bool> SetAsync(TuneRow row, bool enable)
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
                var item = row.Entry.Item;
                if (await Task.Run(() => TuneScan.Find(system, catalog, row.Id)) is { } after) row.Update(after);
                row.Reverted = false;
                var restart = item.Takes == TuneEffect.Explorer && restartExplorer is not null;
                if (enable) toasts.Show("tune", ToastKind.Success, $"{row.Name} is on", outcome.Note ?? "You can turn it off any time.", restart ? "Restart Explorer" : "Turn off", restart ? RestartExplorerAsync : () => SetAsync(row, false), 12);
                else toasts.Show("tune", ToastKind.Success, $"{row.Name} is off", outcome.Note ?? "Put back the way it was.", restart ? "Restart Explorer" : "", restart ? RestartExplorerAsync : null, 12);
                log.Write(ActivityLevel.Success, ActivityArea.App, enable ? "Tweak turned on" : "Tweak turned off", $"{row.Name}.{(outcome.Note is { } note ? " " + note : "")}");
            }
            else
            {
                toasts.Show("tune", ToastKind.Warning, $"{row.Name} could not be changed", ToastCenter.Brief(outcome.Error ?? "Windows did not accept the change."));
                log.Write(ActivityLevel.Warning, ActivityArea.App, "Tweak could not be changed", $"{row.Name}: {outcome.Error}");
                if (await Task.Run(() => TuneScan.Find(system, catalog, row.Id)) is { } current) row.Update(current);
            }
        }
        catch (OperationCanceledException)
        {
            toasts.Show("tune", ToastKind.Info, "Administrator permission was declined", "Nothing was changed.");
            log.Write(ActivityLevel.Info, ActivityArea.App, "Tweak change stopped", "Administrator permission was declined.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            toasts.Show("tune", ToastKind.Warning, $"{row.Name} could not be changed", ToastCenter.Brief(ex.Message));
            log.Write(ActivityLevel.Error, ActivityArea.App, "Tweak could not be changed", ex.Message);
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

    private async Task RestartExplorerAsync()
    {
        if (restartExplorer is null) return;
        try { await restartExplorer(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            toasts.Show("tune-explorer", ToastKind.Warning, "File Explorer could not be restarted", ToastCenter.Brief(ex.Message));
            log.Write(ActivityLevel.Warning, ActivityArea.App, "File Explorer could not be restarted", ex.Message);
        }
    }

    public Task<bool> PutBackAsync(TuneChangeRow change) =>
        all.FirstOrDefault(x => x.Id == change.Change.Id) is { } row ? SetAsync(row, false) : Task.FromResult(false);

    /// <summary>Puts back everything TidyWindow changed.</summary>
    public async Task RestoreAllAsync()
    {
        if (Busy) return;
        working = true; Notify();
        try
        {
            var results = await manager.RestoreAllAsync(system, catalog);
            var done = results.Count(x => x.Outcome.Ok);
            if (results.Count == 0) toasts.Show("tune", ToastKind.Info, "Nothing to put back");
            else if (done == results.Count) toasts.Show("tune", ToastKind.Success, done == 1 ? "1 tweak put back" : $"{done} tweaks put back");
            else toasts.Show("tune", ToastKind.Warning, $"{done} of {results.Count} put back", ToastCenter.Brief(string.Join(" ", results.Where(x => !x.Outcome.Ok).Select(x => $"{x.Change.Name}: {x.Outcome.Error}"))), seconds: 15);
            log.Write(ActivityLevel.Info, ActivityArea.App, "Tweaks put back", $"{done} of {results.Count}.");
        }
        catch (OperationCanceledException) { toasts.Show("tune", ToastKind.Info, "Administrator permission was declined", "Nothing was changed."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            toasts.Show("tune", ToastKind.Warning, "Could not put everything back", ToastCenter.Brief(ex.Message));
            log.Write(ActivityLevel.Error, ActivityArea.App, "Tweaks could not all be put back", ex.Message);
        }
        finally { working = false; Notify(); }
        await ScanAsync();
    }

    public void Dispose() { lifetime.Cancel(); lifetime.Dispose(); }
}
