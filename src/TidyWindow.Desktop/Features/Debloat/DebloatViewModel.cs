using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

public enum DebloatTab { Microsoft, Promoted, Ai, Manufacturer, Components, Removed }

/// <summary>One thing on this PC that can be removed.</summary>
public sealed class DebloatRow(DebloatEntry entry, Action selectionChanged) : ObservableState
{
    private DebloatEntry current = entry;
    private bool selected, expanded;
    private IReadOnlyList<StartupFact>? facts;

    public DebloatEntry Entry => current;
    public string Id => current.Id;
    public string Name => current.Item.Name;
    public string Glyph => GlyphFor(current.Item);

    /// <summary>The app's own picture when one is found, else <see cref="Glyph"/>.</summary>
    public ImageSource? Icon { get; private set; }
    public IconQuery? IconQuery { get; init; }
    public void SetIcon(ImageSource? image) { Icon = image; Changed(nameof(Icon)); }

    public static string GlyphFor(DebloatItem item) => char.ConvertFromUtf32(int.TryParse(item.Icon, System.Globalization.NumberStyles.HexNumber, null, out var code) ? code : item.Group switch
    {
        DebloatGroup.Promoted => 0xE8B2,
        DebloatGroup.Ai => 0xE99A,
        DebloatGroup.Manufacturer => 0xE7F8,
        DebloatGroup.Components => 0xE753,
        _ => 0xE74C
    });

    /// <summary>Ticked by the person. A row that cannot be removed cannot be ticked.</summary>
    public bool Selected
    {
        get => selected;
        set { if (Set(ref selected, value && current.CanRemove)) selectionChanged(); }
    }

    public bool CanSelect => current.CanRemove;

    public string Sub
    {
        get
        {
            var item = current.Item;
            var what = item.Kind switch
            {
                DebloatKind.Appx => current.AppCount == 1 ? "1 app" : $"{current.AppCount} apps",
                DebloatKind.Recall => "Windows feature",
                _ => "Built into Windows"
            };
            return item.Topic.Length > 0 ? $"{item.Topic} · {what}" : what;
        }
    }

    public string Summary => current.Item.Summary;
    public string Note => current.Lock ?? "";
    public Visibility NoteVisibility => current.Lock is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LockVisibility => current.Lock is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CheckVisibility => current.Lock is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CarefulVisibility => current.Item.Risk == DebloatRisk.Careful ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SuggestedVisibility => current.Item.Recommended && current.CanRemove ? Visibility.Visible : Visibility.Collapsed;
    public string AutomationName => $"Select {Name}";
    public string DetailsName => $"Details for {Name}";

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
        var item = current.Item;
        Add("What it is", item.Summary);
        Add("What you give up", item.Effect);
        Add("How it is removed", item.Kind switch
        {
            DebloatKind.Appx => "For every account on this PC, and Windows is told not to install it again for new accounts.",
            DebloatKind.Recall => "Switched off as a Windows feature, so it can be switched back on.",
            DebloatKind.OneDrive => "With OneDrive's own uninstaller. None of your files are deleted.",
            DebloatKind.Edge => "With Edge's own uninstaller, with the updater told not to install it again. Web components that apps need are kept.",
            _ => ""
        });
        Add("Can be put back", item.StoreId.Length > 0 ? "Yes. It is installed again from the Microsoft Store or WinGet." : item.Kind == DebloatKind.Recall ? "Yes, from the Removed tab." : "Windows may install it again. You can also find it in the Microsoft Store.");
        Add("Packages", string.Join("\n", current.Packages.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)), mono: true);
        Add("Source", string.Join(", ", item.Sources));
        if (current.Lock is { } rule) Add("Why it is locked", rule);
        return list;
    }

    public void Update(DebloatEntry updated)
    {
        current = updated; facts = null;
        if (!updated.CanRemove && selected) { selected = false; Changed(nameof(Selected)); selectionChanged(); }
        foreach (var name in new[] { nameof(Entry), nameof(Facts), nameof(Note), nameof(NoteVisibility), nameof(LockVisibility), nameof(CheckVisibility), nameof(CanSelect), nameof(SuggestedVisibility) }) Changed(name);
    }
}

/// <summary>Something TidyWindow removed earlier, with the button that puts it back.</summary>
public sealed class DebloatRemovedRow(DebloatRemoved removed, DebloatItem? item) : ObservableState
{
    private bool busy;
    public DebloatRemoved Removed { get; } = removed;
    public DebloatItem? Item { get; } = item;
    public string Id => Removed.Id;
    public string Name => Removed.Name;
    public string Glyph => Item is null ? "" : DebloatRow.GlyphFor(Item);
    /// <summary>The picture the app had in the list before it was removed, while this session still remembers it.</summary>
    public ImageSource? Icon { get; private set; }
    public void SetIcon(ImageSource? image) { Icon = image; Changed(nameof(Icon)); }
    public string Sub => $"Removed {Removed.When.ToLocalTime():d MMM yyyy, h:mm tt}";
    public bool Busy { get => busy; set { if (Set(ref busy, value)) Changed(nameof(CanPutBack)); } }
    public bool CanPutBack => !busy && Item is not null;
    public string AutomationName => $"Put {Name} back";
}

public sealed class DebloatViewModel(IDebloatSystem system, IReadOnlyList<DebloatItem> catalog, DebloatManager manager, ActivityLog log, ToastCenter toasts, IconService? iconService = null) : ObservableState, IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly IconService icons = iconService ?? IconService.Shared;
    // Where each item's picture came from, kept so that an item that has been removed can still be shown with it for the rest of the session.
    private readonly Dictionary<string, IconQuery> iconQueries = new(StringComparer.OrdinalIgnoreCase);
    private bool listening;
    private bool scanning, scanned, working;
    private string query = "", checkedAt = "", workHeading = "", workCounter = "", workText = "Removing…";
    private DebloatTab tab = DebloatTab.Microsoft;
    private List<DebloatRow> all = [];
    private int notOnThisPc;

    /// <summary>The rows of the open tab, already narrowed by the search.</summary>
    public BulkObservableCollection<DebloatRow> Rows { get; } = new();
    public BulkObservableCollection<DebloatRemovedRow> Removed { get; } = new();
    /// <summary>What has been removed so far in the run in progress, shown as a checklist.</summary>
    public ObservableCollection<string> Progress { get; } = [];

    public bool Scanned => scanned;
    public bool Busy => scanning || working;
    public bool CanScan => !Busy;
    public string ScanLabel => scanning ? "Checking…" : "Refresh";
    public string CheckedText => checkedAt;
    public bool ScanOverlay => scanning && all.Count == 0;
    public bool WorkOverlay => working || ScanOverlay;
    public string WorkHeading => working ? workHeading : "";
    public string WorkCounter => working ? workCounter : "";
    public string WorkText => working ? workText : "Checking what is installed…";

    public string MicrosoftCount => Count(DebloatGroup.Microsoft);
    public string PromotedCount => Count(DebloatGroup.Promoted);
    public string AiCount => Count(DebloatGroup.Ai);
    public string ManufacturerCount => Count(DebloatGroup.Manufacturer);
    public string ComponentsCount => Count(DebloatGroup.Components);
    public string RemovedCount => Removed.Count.ToString("N0");
    public bool CanPutAllBack => !Busy && Removed.Any(x => x.Item is not null);
    private string Count(DebloatGroup group) => scanned ? all.Count(x => x.Entry.Item.Group == group).ToString("N0") : "–";

    public int SelectedCount => all.Count(x => x.Selected);
    public bool HasSelection => SelectedCount > 0;
    public bool CanRemove => !Busy && HasSelection;
    public string SelectedText => SelectedCount switch { 0 => "Nothing selected", 1 => "1 selected", var n => $"{n:N0} selected" };
    public string RemoveLabel => SelectedCount switch { 0 => "Remove selected", 1 => "Remove 1", var n => $"Remove {n:N0}" };
    public bool CanSuggest => !Busy && all.Any(x => x.Entry.Item.Recommended && x.Entry.CanRemove && !x.Selected);

    public string Summary => scanned ? $"{all.Count:N0} found on this PC" : "";

    public string TabNote => tab switch
    {
        DebloatTab.Microsoft => "Apps Microsoft installs for you. Select what you do not use. Everything here can be put back.",
        DebloatTab.Promoted => "Games and apps that Windows installs and pins to Start as advertising.",
        DebloatTab.Ai => "Copilot and Recall. Removing them stops them being offered or switched on again.",
        DebloatTab.Manufacturer => "Extras the PC maker added. Some manage drivers, updates or fan settings, so these ask before they go.",
        DebloatTab.Components => "Parts of Windows with their own uninstallers. TidyWindow refuses when removing one would leave you stuck.",
        _ => ""
    };

    public Visibility ListVisibility => tab == DebloatTab.Removed ? Visibility.Collapsed : Visibility.Visible;
    public Visibility RemovedVisibility => tab == DebloatTab.Removed ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyVisibility => scanned && tab != DebloatTab.Removed && Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoRemovedVisibility => Removed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string EmptyGlyph => query.Length > 0 ? "\uE721" : "\uE73E";
    public string EmptyTitle => query.Length > 0 ? "No matches" : "Nothing to remove here";
    public string EmptyText => query.Length > 0 ? "Try a different word." : "This PC has none of what this list covers.";

    public string FooterNote => $"Windows parts that other apps or Windows itself depend on are never offered, and the helper checks that again before it removes anything. Your files are never deleted.{(notOnThisPc > 0 ? $" {notOnThisPc:N0} more items are not on this PC." : "")}";

    public string Query { get => query; set { if (Set(ref query, value)) ApplyFilter(); } }
    public DebloatTab Tab { get => tab; set { if (Set(ref tab, value)) ApplyFilter(); } }

    private void Notify()
    {
        foreach (var name in new[] { nameof(Busy), nameof(CanScan), nameof(CanRemove), nameof(CanSuggest), nameof(CanPutAllBack), nameof(ScanLabel), nameof(ScanOverlay), nameof(WorkOverlay), nameof(WorkHeading), nameof(WorkCounter), nameof(WorkText), nameof(EmptyVisibility) }) Changed(name);
    }

    private void SelectionChanged()
    {
        foreach (var name in new[] { nameof(SelectedCount), nameof(HasSelection), nameof(CanRemove), nameof(CanSuggest), nameof(SelectedText), nameof(RemoveLabel) }) Changed(name);
    }

    public async Task ScanAsync()
    {
        if (Busy || lifetime.IsCancellationRequested) return;
        scanning = true; Notify();
        try
        {
            var scan = await Task.Run(() => DebloatScan.Run(system, catalog), lifetime.Token);
            var kept = all.Where(x => x.Selected).Select(x => x.Id).ToHashSet();
            // Recall stays installed when it is switched off, so what TidyWindow has already switched off is not offered again.
            var done = manager.History.All().Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            all = scan.Entries.Where(x => !(x.Item.Kind == DebloatKind.Recall && done.Contains(x.Id))).Select(x => new DebloatRow(x, SelectionChanged) { Selected = kept.Contains(x.Id), IconQuery = IconQueryFor(x) }).ToList();
            foreach (var row in all)
                if (row.IconQuery is { } query)
                {
                    iconQueries[row.Id] = query;
                    if (icons.TryGet(query, out var cached) && cached is not null) row.SetIcon(cached);
                }
            notOnThisPc = scan.NotOnThisPc;
            scanned = true;
            checkedAt = $"Checked {DateTime.Now:h:mm tt}";
            ApplyFilter(keepPosition: true);
            RefreshRemoved();
            foreach (var name in new[] { nameof(FooterNote), nameof(CheckedText), nameof(Summary) }) Changed(name);
            SelectionChanged();
            log.Write(ActivityLevel.Info, ActivityArea.App, "Apps scan finished", $"{all.Count} removable items found on this PC.");
            if (!listening) { IconService.DarkChanged += ReloadIcons; listening = true; }
            _ = LoadIconsAsync(all);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            toasts.Show("debloat-scan", ToastKind.Warning, "Installed apps could not be read", ToastCenter.Brief(ex.Message));
            log.Write(ActivityLevel.Warning, ActivityArea.App, "Apps scan could not finish", ex.Message);
        }
        finally { scanning = false; Notify(); }
    }

    private void ApplyFilter(bool keepPosition = false)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Matches(DebloatRow row) => words.All(w =>
            row.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase) || row.Sub.Contains(w, StringComparison.CurrentCultureIgnoreCase) ||
            row.Summary.Contains(w, StringComparison.CurrentCultureIgnoreCase) || row.Entry.Packages.Any(p => p.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase)));
        var group = tab switch { DebloatTab.Microsoft => DebloatGroup.Microsoft, DebloatTab.Promoted => DebloatGroup.Promoted, DebloatTab.Ai => DebloatGroup.Ai, DebloatTab.Manufacturer => DebloatGroup.Manufacturer, _ => DebloatGroup.Components };
        var inTab = tab == DebloatTab.Removed ? [] : all.Where(x => x.Entry.Item.Group == group);
        // What is suggested comes first, then A to Z; a locked row goes last, since there is nothing to do with it.
        var shown = inTab.Where(Matches).OrderBy(x => x.Entry.CanRemove ? 0 : 1).ThenByDescending(x => x.Entry.Item.Recommended).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (keepPosition) Rows.Sync(shown, x => x.Id); else Rows.Reset(shown);
        foreach (var name in new[]
        {
            nameof(MicrosoftCount), nameof(PromotedCount), nameof(AiCount), nameof(ManufacturerCount), nameof(ComponentsCount), nameof(TabNote), nameof(Summary),
            nameof(ListVisibility), nameof(RemovedVisibility), nameof(EmptyVisibility), nameof(EmptyGlyph), nameof(EmptyTitle), nameof(EmptyText)
        }) Changed(name);
    }

    public void RefreshRemoved()
    {
        var rows = manager.History.All().Select(x => new DebloatRemovedRow(x, catalog.FirstOrDefault(c => string.Equals(c.Id, x.Id, StringComparison.OrdinalIgnoreCase)))).ToList();
        foreach (var row in rows)
            if (iconQueries.TryGetValue(row.Id, out var query) && icons.TryGet(query, out var cached) && cached is not null) row.SetIcon(cached);
        Removed.Reset(rows);
        foreach (var name in new[] { nameof(RemovedCount), nameof(NoRemovedVisibility), nameof(CanPutAllBack) }) Changed(name);
    }

    /// <summary>
    /// What to look at to draw an item's picture: a Store app's package folder (its logo is in the manifest), or the program of OneDrive and Edge.
    /// Packages are tried in the order the catalog lists them, so an item that covers many apps shows its main one. Recall has no picture of its own.
    /// </summary>
    public static IconQuery? IconQueryFor(DebloatEntry entry)
    {
        var item = entry.Item;
        switch (item.Kind)
        {
            case DebloatKind.Appx:
                foreach (var pattern in item.Packages)
                {
                    if (entry.Packages.FirstOrDefault(x => x.InstallLocation.Length > 0 && DebloatGuard.Matches(pattern, x.Name)) is { } package) return new(package.Name, InstallLocation: package.InstallLocation);
                    if (entry.Provisioned.FirstOrDefault(x => x.InstallLocation.Length > 0 && DebloatGuard.Matches(pattern, x.DisplayName)) is { } staged) return new(staged.DisplayName, InstallLocation: staged.InstallLocation);
                }
                return null;
            case DebloatKind.OneDrive:
                return FirstExisting(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft OneDrive", "OneDrive.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "OneDrive", "OneDrive.exe")) is { } oneDrive ? new(item.Name, ExePath: oneDrive) : null;
            case DebloatKind.Edge:
                return FirstExisting(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe")) is { } edge ? new(item.Name, ExePath: edge) : null;
            default:
                return null;
        }
    }

    private static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);

    private void ReloadIcons() => _ = LoadIconsAsync(all, everyRow: true);

    private async Task LoadIconsAsync(IReadOnlyList<DebloatRow> rows, bool everyRow = false)
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

    /// <summary>Ticks everything that is suggested: apps nobody misses. Nothing marked Careful is ever ticked.</summary>
    public void SelectSuggested()
    {
        foreach (var row in all.Where(x => x.Entry.Item.Recommended && x.Entry.CanRemove)) row.Selected = true;
    }

    public void ClearSelection()
    {
        foreach (var row in all.Where(x => x.Selected)) row.Selected = false;
    }

    public IReadOnlyList<DebloatRow> Selected() => all.Where(x => x.Selected && x.Entry.CanRemove).ToList();

    /// <summary>What the person is told before they confirm. Names what goes, and spells out what each careful item costs.</summary>
    public static string Warning(IReadOnlyList<DebloatRow> rows)
    {
        var lines = new List<string> { rows.Count == 1 ? $"{rows[0].Name} will be removed for every account on this PC." : $"These {rows.Count:N0} items will be removed for every account on this PC:" };
        if (rows.Count > 1) lines.AddRange(rows.Select(x => "• " + x.Name));
        foreach (var careful in rows.Where(x => x.Entry.Item.Risk == DebloatRisk.Careful))
            lines.Add($"{careful.Name}: {careful.Entry.Item.Effect}");
        lines.Add("Windows will ask for administrator permission once. You can put each item back from the Removed tab.");
        return string.Join("\n\n", lines);
    }

    /// <summary>Removes the ticked items one after another, showing the run as a checklist. One item that fails does not stop the others.</summary>
    public async Task RemoveSelectedAsync() => await RemoveAsync(Selected());

    public async Task RemoveAsync(IReadOnlyList<DebloatRow> rows)
    {
        if (Busy || rows.Count == 0) return;
        working = true; Progress.Clear(); workText = "Removing…"; workHeading = ""; workCounter = ""; Notify();
        int done = 0, failed = 0;
        string? firstError = null;
        var restart = false;
        try
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                workHeading = row.Name; workCounter = rows.Count > 1 ? $"{i + 1} of {rows.Count}" : "";
                workText = row.Entry.Item.Kind switch { DebloatKind.Edge => "This can take a few minutes.", DebloatKind.OneDrive => "Running its uninstaller…", _ => "Removing for every account…" };
                Notify();
                var outcome = await manager.RemoveAsync(row.Entry);
                if (outcome.Ok)
                {
                    done++; Progress.Add(row.Name);
                    restart |= row.Entry.Item.Kind == DebloatKind.Recall;
                    log.Write(ActivityLevel.Success, ActivityArea.App, "Removed", $"{row.Name}.{(outcome.Note is { } note ? " " + note : "")}");
                }
                else
                {
                    failed++; firstError ??= $"{row.Name}: {outcome.Error}";
                    log.Write(ActivityLevel.Warning, ActivityArea.App, "Could not remove", $"{row.Name}: {outcome.Error}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            toasts.Show("debloat", ToastKind.Info, "Administrator permission was declined", done == 0 ? "Nothing was removed." : $"{done} removed before that.");
            log.Write(ActivityLevel.Info, ActivityArea.App, "Removal stopped", "Administrator permission was declined.");
            failed = -1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            toasts.Show("debloat", ToastKind.Warning, "Removal stopped", ToastCenter.Brief(ex.Message));
            log.Write(ActivityLevel.Error, ActivityArea.App, "Removal could not finish", ex.Message);
            failed = -1;
        }
        finally { working = false; Notify(); }

        if (failed >= 0)
        {
            if (failed == 0) toasts.Show("debloat", ToastKind.Success, done == 1 ? $"{rows[0].Name} removed" : $"{done:N0} items removed", restart ? "Restart Windows to finish removing Recall." : "You can put them back from the Removed tab.", seconds: 12);
            else toasts.Show("debloat", ToastKind.Warning, $"{done:N0} removed, {failed:N0} could not be", ToastCenter.Brief(firstError ?? ""), seconds: 15);
        }
        // One read of the PC settles what is really gone, whatever each step reported.
        await ScanAsync();
    }

    /// <summary>Lets Windows have the item back and installs it again where that is possible.</summary>
    public async Task PutBackAsync(DebloatRemovedRow row)
    {
        if (Busy || row.Item is not { } item) return;
        working = true; row.Busy = true; workHeading = row.Name; workCounter = ""; workText = "Putting it back…"; Progress.Clear(); Notify();
        try
        {
            var outcome = await manager.RestoreAsync(item, lifetime.Token);
            if (outcome.Ok)
            {
                toasts.Show("debloat", ToastKind.Success, $"{row.Name} is back", outcome.Note ?? "Installed again.", seconds: 10);
                log.Write(ActivityLevel.Success, ActivityArea.App, "Put back", $"{row.Name}.{(outcome.Note is { } note ? " " + note : "")}");
            }
            else
            {
                toasts.Show("debloat", ToastKind.Warning, $"{row.Name} could not be put back", ToastCenter.Brief(outcome.Error ?? "Windows did not accept it."));
                log.Write(ActivityLevel.Warning, ActivityArea.App, "Could not put back", $"{row.Name}: {outcome.Error}");
            }
        }
        catch (OperationCanceledException) { toasts.Show("debloat", ToastKind.Info, "Administrator permission was declined", "Nothing was changed."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            toasts.Show("debloat", ToastKind.Warning, $"{row.Name} could not be put back", ToastCenter.Brief(ex.Message));
            log.Write(ActivityLevel.Error, ActivityArea.App, "Could not put back", ex.Message);
        }
        finally { row.Busy = false; working = false; Notify(); }
        RefreshRemoved();
        await ScanAsync();
    }

    /// <summary>Puts back everything that was removed, one item after another, after a single permission prompt.</summary>
    public async Task PutAllBackAsync()
    {
        var rows = Removed.Where(x => x.Item is not null).ToList();
        if (Busy || rows.Count == 0) return;
        working = true; Progress.Clear(); workText = "Putting it back…"; workHeading = ""; workCounter = "";
        foreach (var row in rows) row.Busy = true;
        Notify();
        DebloatRestoreBatch? batch = null;
        var started = 0;
        try
        {
            batch = await manager.RestoreAllAsync(rows.Select(x => x.Item!).ToList(), (item, outcome) =>
            {
                if (outcome is null) { started++; workHeading = item.Name; workCounter = rows.Count > 1 ? $"{started} of {rows.Count}" : ""; Notify(); }
                else if (outcome.Ok) Progress.Add(item.Name);
            }, lifetime.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            foreach (var row in rows) row.Busy = false;
            working = false; Notify();
        }
        if (batch is not null) ReportPutBack(batch, rows.Count);
        RefreshRemoved();
        await ScanAsync();
    }

    private void ReportPutBack(DebloatRestoreBatch batch, int asked)
    {
        var done = batch.Results.Count(x => x.Outcome.Ok);
        var failed = batch.Results.Where(x => !x.Outcome.Ok).ToList();
        var title = done == 1 ? "1 item put back" : $"{done:N0} items put back";
        if (batch.Declined) toasts.Show("debloat", ToastKind.Info, "Administrator permission was declined", done > 0 ? $"{title} before that. The rest were not changed." : "Nothing was changed.", seconds: 12);
        else if (failed.Count == 0) toasts.Show("debloat", ToastKind.Success, title, "Installed again where that is possible.", seconds: 10);
        else toasts.Show("debloat", ToastKind.Warning, $"{done:N0} of {asked:N0} put back", ToastCenter.Brief(string.Join(" ", failed.Select(x => $"{x.Item.Name}: {x.Outcome.Error}"))), seconds: 15);
        log.Write(failed.Count == 0 && !batch.Declined ? ActivityLevel.Success : ActivityLevel.Warning, ActivityArea.App, "Put everything back", $"{done} of {asked}.{(batch.Declined ? " Administrator permission was declined." : "")}{(failed.Count > 0 ? " Not put back: " + string.Join(", ", failed.Select(x => x.Item.Name)) + "." : "")}");
    }

    public void Dispose() { IconService.DarkChanged -= ReloadIcons; lifetime.Cancel(); lifetime.Dispose(); }
}
