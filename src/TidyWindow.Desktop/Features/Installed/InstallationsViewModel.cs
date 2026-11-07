using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

public sealed class InstalledRow(ManagedPackage package) : ObservableState
{
    public ManagedPackage Package { get; } = package;
    public string Name => Package.Name;
    // Registry programs and Store apps have no package name to show; their WinGet id is a registry path.
    public string Detail => Package.Origin == AppOrigin.Package ? $"{Package.ManagerName}{(Package.Global ? " · all users" : "")} · {Package.Id}" : Source;
    public string Id => Package.Id;
    /// <summary>Set once the row's picture and publisher were asked for, so scrolling past it again asks for nothing.</summary>
    internal bool DetailsRequested;
    internal bool PublisherRequested;
    /// <summary>Shown from the saved list while the real one is still being read: it can be looked at, not acted on.</summary>
    internal bool FromSnapshot;

    private ImageSource? icon;
    public ImageSource? Icon => icon;
    public void SetIcon(ImageSource? image) { icon = image; Changed(nameof(Icon)); }
    /// <summary>Shown while the real icon loads, and kept when the app has none: what kind of app it is.</summary>
    public string Glyph => Package.Origin switch { AppOrigin.StoreApp => "", AppOrigin.WindowsProgram => AppIcon.DefaultGlyph, _ => "" };
    public IconQuery IconQuery => InstalledHints.IconFor(Package);

    private FoundPublisher publisher = FoundPublisher.None;
    public string Publisher => publisher.Name;
    public void SetPublisher(FoundPublisher found)
    {
        if (found == publisher) return;
        publisher = found;
        foreach (var name in new[] { nameof(Publisher), nameof(Subtitle), nameof(CompactDetail) }) Changed(name);
    }
    /// <summary>The line under the name: who made it, then what the package manager calls it. Windows programs and Store apps have no package name worth showing.</summary>
    public string Subtitle => (Publisher.Length > 0, Package.Origin == AppOrigin.Package) switch
    {
        (true, true) => $"{Publisher} · {Id}",
        (true, false) => Publisher,
        (false, true) => Id,
        _ => Source
    };

    public string Source => Package.Origin switch
    {
        AppOrigin.WindowsProgram => "Installed on this PC",
        AppOrigin.StoreApp => "Microsoft Store app",
        _ => Package.ManagerName + (Package.Global ? " · all users" : "")
    };
    public string Version => Package.Version.Length == 0 ? "Unknown version" : Package.Version;
    public string UpdateDetail => Package.Held ? "Pinned in package manager" : Package.AvailableVersion is { Length: > 0 } latest ? $"Update to {latest}"
        : Package.HasStoreUpdate ? $"The Microsoft Store has version {Package.StoreVersion}. {Package.ManagerName} can’t apply Store updates: update it in the Microsoft Store or in the app itself." : Package.Note;
    private string queueStatus = "";
    public string Available => queueStatus.Length > 0 ? queueStatus : Package.Held ? "Pinned" : Package.AvailableVersion ?? (Package.HasStoreUpdate ? $"{Package.StoreVersion} · Store" : Package.Note.Length > 0 ? "Needs attention" : "—");
    public string CompactDetail => $"{(Publisher.Length > 0 ? Publisher + " · " : "")}{Source} · {Version}" + (queueStatus.Length > 0 ? $" · {queueStatus}" : Package.Held ? " · Pinned" : Package.AvailableVersion is { } latest ? $" → {latest}" : Package.HasStoreUpdate ? $" → {Package.StoreVersion} (Store)" : Package.Note.Length > 0 ? $" · {Package.Note}" : "");
    public bool CanUpdate => CanAct && Package.AvailableVersion is not null && !Package.Held;
    public void SetQueueEntry(QueueEntry? entry)
    {
        var status = entry?.State switch { QueueItemState.Waiting => "Queued", QueueItemState.Running => "Working…", QueueItemState.Finished when entry.Result?.Outcome == ActionOutcome.Failed => "Failed · see Logs", QueueItemState.Finished when entry.Result?.Outcome == ActionOutcome.RestartRequired => "Restart required", _ => "" };
        if (status == queueStatus) return;
        queueStatus = status;
        Changed(nameof(Available)); Changed(nameof(CompactDetail));
    }
    private bool isSelected;
    public bool IsSelected { get => isSelected; set => Set(ref isSelected, value); }
    public string UpdateName => $"Update {Name} using {Package.ManagerName}";
    public string UninstallName => Package.Origin == AppOrigin.Package ? $"Uninstall {Name} using {Package.ManagerName}" : $"Uninstall {Name}";
    public Visibility UpdateVisibility => Package.AvailableVersion is not null && !Package.Held ? Visibility.Visible : Visibility.Collapsed;
    private bool canAct;
    public bool CanAct { get => canAct; set { if (Set(ref canAct, value)) Changed(nameof(CanUpdate)); } }
    private bool compact;
    public bool Compact { get => compact; set { if (Set(ref compact, value)) { Changed(nameof(MetadataWidth)); Changed(nameof(NameWidth)); Changed(nameof(ColumnGap)); Changed(nameof(WideVisibility)); Changed(nameof(CompactVisibility)); } } }
    public GridLength NameWidth => new(Compact ? 1 : 2.5, GridUnitType.Star);
    public GridLength MetadataWidth => Compact ? new(0) : new(1, GridUnitType.Star);
    public double ColumnGap => Compact ? 4 : 16;
    public Visibility WideVisibility => Compact ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CompactVisibility => Compact ? Visibility.Visible : Visibility.Collapsed;
    public override string ToString() => $"{Name}. {Version}. {Detail}. {UpdateDetail}";
}

public sealed class InstallationsViewModel : ObservableState, IDisposable
{
    private readonly InstallationService service;
    private readonly WorkspaceViewModel workspace;
    private readonly ActivityLog log;
    private readonly OperationQueueViewModel queue;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<ManagerKind, ManagedInventory> inventories = [];
    private readonly List<InstalledRow> all = [];
    private string query = "";
    private bool updatesOnly;
    private bool compact;
    private bool refreshing;

    private bool loaded;
    public BulkObservableCollection<InstalledRow> Rows { get; } = [];
    public Visibility TableHeaderVisibility => !compact && Rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool NeedsRefresh { get; set; } = true;
    /// <summary>The first read, with nothing to show yet: this is the only time the page is covered and its buttons are off.</summary>
    public bool Refreshing { get => refreshing; private set { Set(ref refreshing, value); NotifyState(); } }
    private bool updating;
    /// <summary>A later read, with the list already on screen. The list stays usable, so apps can still be queued while it catches up.</summary>
    public bool Updating { get => updating; private set { Set(ref updating, value); NotifyState(); } }
    private bool refreshAgain;

    public bool CanRefresh => !Refreshing && !Updating && workspace.CanRefresh;
    // A read was asked for while the package managers were still being checked (or busy): it starts by itself as soon as they are free.
    private bool waiting;
    /// <summary>Covers the page while there is nothing to show yet, whether the read is running or only waiting for its turn.</summary>
    public bool ShowLoading => Refreshing || waiting && !loaded;
    public string ProgressText => Refreshing ? "Checking installed apps and updates…" : "Waiting for your package managers…";
    public Visibility ProgressVisibility => ShowLoading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UpdatingVisibility => Updating ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyVisibility => Rows.Count == 0 && !ShowLoading ? Visibility.Visible : Visibility.Collapsed;
    public string EmptyTitle => query.Length > 0 ? "No matching apps" : Errors.Length > 0 ? "The list could not be fully checked" : updatesOnly ? "No available updates" : "No packages to manage";
    public string EmptyDetail => Errors.Length > 0 ? "Open Logs for details, then refresh to try again." : query.Length > 0 || updatesOnly ? "Change your search or filter to see more apps." : "Packages from WinGet, Chocolatey and Scoop appear here. Check Overview if a manager needs attention.";
    private string errors = "";
    public string Errors { get => errors; private set { Set(ref errors, value); Changed(nameof(ErrorVisibility)); } }
    public Visibility ErrorVisibility => Errors.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    private readonly IconService icons;
    private readonly PublisherService publishers;
    private readonly InstalledSnapshot? snapshot;
    private bool listening;
    private int generation;
    public InstallationsViewModel(InstallationService service, WorkspaceViewModel workspace, ActivityLog log, OperationQueueViewModel queue, IconService? iconService = null, PublisherService? publisherService = null, InstalledSnapshot? snapshot = null)
    {
        this.service = service; this.workspace = workspace; this.log = log; this.queue = queue; this.snapshot = snapshot; queue.StateChanged += NotifyState;
        icons = iconService ?? IconService.Shared; publishers = publisherService ?? PublisherService.Shared;
        workspace.PropertyChanged += WorkspaceChanged;
    }
    private void WorkspaceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkspaceViewModel.Busy) or nameof(WorkspaceViewModel.CanRefresh) or nameof(WorkspaceViewModel.CanQueue)) NotifyState();
        if (e.PropertyName == nameof(WorkspaceViewModel.Busy) && !workspace.Busy) NeedsRefresh = true;
        if (waiting && e.PropertyName is nameof(WorkspaceViewModel.CanRefresh) or nameof(WorkspaceViewModel.Busy)) _ = ResumeAsync();
    }
    // The managers report "free" a moment before they start checking what is installed, so let that settle before looking again.
    private async Task ResumeAsync()
    {
        await Task.Yield();
        if (waiting && !lifetime.IsCancellationRequested && workspace.CanRefresh) await RefreshAsync();
    }
    private void SetWaiting(bool value)
    {
        if (waiting == value) return;
        waiting = value;
        NotifyState();
    }
    private void NotifyState()
    {
        var index = queue.Index();
        selecting = true;
        try
        {
            foreach (var row in all)
            {
                index.TryGetValue(PackageOperation.Resource(row.Package.Manager, row.Package.Id, row.Package.Global), out var entry);
                row.SetQueueEntry(entry);
                row.CanAct = !Refreshing && !row.FromSnapshot && workspace.CanQueue && entry?.State is not (QueueItemState.Waiting or QueueItemState.Running);
                if (!row.CanAct) row.IsSelected = false;
            }
        }
        finally { selecting = false; }
        NotifySelection();
        foreach (var name in new[] { nameof(CanRefresh), nameof(ShowLoading), nameof(ProgressVisibility), nameof(UpdatingVisibility), nameof(ProgressText), nameof(EmptyVisibility) }) Changed(name);
    }
    internal void ApplyLayout(AdaptiveLayout layout) { compact = layout.CompactResults; foreach (var row in all) row.Compact = compact; Changed(nameof(TableHeaderVisibility)); }
    // While true, selection changes are batched: one notification at the end instead of one per row.
    private bool selecting;
    public void Filter(string text, bool onlyUpdates) { ClearSelection(); query = text.Trim(); updatesOnly = onlyUpdates; ApplyFilter(); }
    private void ClearSelection()
    {
        selecting = true;
        try { foreach (var row in all) row.IsSelected = false; }
        finally { selecting = false; }
    }
    /// <summary>Puts <see cref="all"/> in the order the page shows: apps with an update first, then by name. Done once per read, not once per keystroke in the search box.</summary>
    private void SortAll()
    {
        var sorted = all.OrderByDescending(x => x.Package.HasUpdateInfo).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Package.Manager).ToList();
        all.Clear();
        all.AddRange(sorted);
    }

    /// <param name="keepPosition">The list is being refreshed under the person, not re-filtered by them: change only the rows that differ, so the scroll position stays.</param>
    private void ApplyFilter(bool keepPosition = false)
    {
        var shown = all.Where(x => (!updatesOnly || x.Package.HasUpdateInfo) &&
            (query.Length == 0 || x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Publisher.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Package.Id.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Package.ManagerName.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Source.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        // A new filter starts the list over (one reset instead of clearing and re-adding every row, which would re-measure on each step).
        if (keepPosition) Rows.Sync(shown, x => x.Package.Key); else Rows.Reset(shown);
        foreach (var name in new[] { nameof(EmptyVisibility), nameof(EmptyTitle), nameof(EmptyDetail), nameof(TableHeaderVisibility) }) Changed(name);
        NotifySelection();
    }
    public async Task RefreshAsync()
    {
        // Something finished while a read was already going, so what that read saw may be out of date: read once more when it ends.
        if (Refreshing || Updating) { if (workspace.CanRefresh) refreshAgain = true; return; }
        if (!CanRefresh) { SetWaiting(true); return; }
        SetWaiting(false);
        do { refreshAgain = false; await ReadAsync(); }
        while (refreshAgain && workspace.CanRefresh);
    }

    private async Task ReadAsync()
    {
        Errors = "";
        var mine = Interlocked.Increment(ref generation);
        // Nothing here is cleared before the first answer: the page shows what it already has (the last read, or the saved list from the last run) and
        // changes only what differs, instead of covering everything until every package manager has finished.
        Updating = true;
        try
        {
            var managers = workspace.Managers.Select(x => x.Status).Where(x => x is not null).Cast<ManagerStatus>().ToArray();
            if (!loaded)
            {
                var saved = await SavedListAsync(managers);
                if (saved.Count > 0) ShowSaved(saved);
                else { Updating = false; Refreshing = true; all.Clear(); ApplyFilter(); }
            }

            var done = new HashSet<ManagerKind>();
            // The list arrives as soon as a manager knows what is installed; its check for updates (the slow part) fills in the rest afterwards.
            var early = new Progress<ManagedInventory>(partial => { if (mine == generation && !done.Contains(partial.Manager)) Apply(partial, partial: true); });
            var pending = managers.Select(x => service.ReadAsync(x, token: lifetime.Token, installedReady: early)).ToList();
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending); pending.Remove(completed);
                var inventory = await completed;
                done.Add(inventory.Manager);
                Apply(inventory, partial: false);
            }
            loaded = true; NeedsRefresh = false;
            // Whatever is still from the saved list belongs to a manager that was not read this time, so it is gone.
            if (all.Any(x => x.FromSnapshot)) { Drop(all.Where(x => x.FromSnapshot).ToList()); ApplyFilter(keepPosition: true); }
            if (snapshot is not null && inventories.Values.All(x => x.Error is null))
            {
                var packages = all.Select(x => x.Package).ToArray();
                _ = Task.Run(() => snapshot.Save(packages));
            }
            // Neither of these is needed to use the page, so neither holds it up.
            _ = BackfillPublishersAsync(mine);
            _ = LogCoverageAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Errors = ex.Message; log.Write(ActivityLevel.Warning, ActivityArea.Install, "Installed packages could not be checked", ex.ToString()); }
        finally
        {
            // A read that failed before its first answer must not leave rows that can never be acted on.
            if (!loaded && all.Any(x => x.FromSnapshot)) { Drop(all.Where(x => x.FromSnapshot).ToList()); ApplyFilter(keepPosition: true); }
            Refreshing = false; Updating = false; NotifyState();
        }
    }

    private InstalledRow MakeRow(ManagedPackage package)
    {
        var row = new InstalledRow(package) { Compact = compact };
        // What was found before comes back with the list, so a refresh does not blank it.
        if (icons.TryGet(row.IconQuery, out var cached) && cached is not null) row.SetIcon(cached);
        if (publishers.TryGet(package, out var named)) row.SetPublisher(named);
        row.PropertyChanged += SelectionChanged;
        return row;
    }

    private void Drop(IReadOnlyList<InstalledRow> rows)
    {
        foreach (var row in rows) row.PropertyChanged -= SelectionChanged;
        var gone = rows.ToHashSet();
        all.RemoveAll(gone.Contains);
    }

    /// <summary>The list from the last run, for the managers that can be read now. Empty when there is none.</summary>
    private async Task<IReadOnlyList<ManagedPackage>> SavedListAsync(IReadOnlyList<ManagerStatus> managers)
    {
        if (snapshot is null) return [];
        var ready = managers.Where(x => x.Health == ManagerHealth.Ready).Select(x => x.Definition.Kind).ToHashSet();
        var saved = await Task.Run(snapshot.Load, lifetime.Token);
        return saved.Where(x => ready.Contains(x.Manager)).ToArray();
    }

    private void ShowSaved(IReadOnlyList<ManagedPackage> saved)
    {
        all.Clear();
        foreach (var package in saved) { var row = MakeRow(package); row.FromSnapshot = true; all.Add(row); }
        SortAll();
        ApplyFilter();
        NotifyState();
    }

    /// <summary>Puts one manager's answer on the page. A row that did not change keeps its place, its picture and its selection, so only what really changed is touched on screen.</summary>
    /// <param name="partial">What is installed, before the check for updates is known: the update information already on the page is kept until the real answer arrives.</param>
    private void Apply(ManagedInventory inventory, bool partial)
    {
        inventories[inventory.Manager] = inventory;
        var previous = new Dictionary<string, InstalledRow>();
        foreach (var old in all.Where(x => x.Package.Manager == inventory.Manager)) previous.TryAdd(old.Package.Key, old);
        var reused = new HashSet<InstalledRow>();
        var current = new List<InstalledRow>(inventory.Packages.Count);
        foreach (var listed in inventory.Packages)
        {
            var package = listed;
            // What the page already knew about updates (from the last read in this run, not from the saved list) is kept until the real answer replaces it.
            if (partial && previous.TryGetValue(package.Key, out var before) && !before.FromSnapshot)
                package = package with { AvailableVersion = before.Package.AvailableVersion, StoreVersion = before.Package.StoreVersion, Held = package.Held || before.Package.Held, Note = before.Package.Note.Length > 0 ? before.Package.Note : package.Note };
            if (previous.TryGetValue(package.Key, out var old) && old.Package == package && reused.Add(old)) { old.FromSnapshot = false; current.Add(old); continue; }
            // The saved list is only a first guess: a row that is not confirmed by the first real answer is a new row, not the saved one.
            current.Add(MakeRow(package));
        }
        Drop(all.Where(x => x.Package.Manager == inventory.Manager && !reused.Contains(x)).ToList());
        all.RemoveAll(x => x.Package.Manager == inventory.Manager);
        all.AddRange(current);
        SortAll();
        Errors = string.Join("\n", inventories.Values.Where(x => x.Error is not null || x.UpdateError is not null)
            .Select(x => $"{ManagerDefinition.All.Single(m => m.Kind == x.Manager).Name}: {x.Error ?? "Updates could not be checked. " + x.UpdateError}"));
        ApplyFilter(keepPosition: true);
        if (!listening) { IconService.DarkChanged += ReloadIcons; listening = true; }
        // The first rows to show lift the cover: from here the page is usable while the rest is read.
        if (Refreshing && all.Count > 0) { Refreshing = false; Updating = true; }
        else NotifyState();
    }

    /// <summary>
    /// A row is about to be shown. Its picture is asked for now rather than when the list is read: a list of hundreds is only ever looked at a screenful at a time,
    /// and reading every program's icon from disk is what made a slow PC crawl.
    /// </summary>
    public void Realized(InstalledRow row)
    {
        if (row.DetailsRequested) return;
        row.DetailsRequested = true;
        _ = LoadRowAsync(row);
    }

    // A theme change gives Store apps a different logo, so the rows that were already shown ask again. The others ask when they are shown.
    private void ReloadIcons() { foreach (var row in all.Where(x => x.DetailsRequested).ToList()) _ = LoadRowAsync(row); }

    private async Task LoadRowAsync(InstalledRow row)
    {
        try
        {
            var publisher = row.PublisherRequested ? null : publishers.FindAsync(row.Package);
            row.PublisherRequested = true;
            if (await icons.LoadAsync(row.IconQuery, lifetime.Token) is { } image) row.SetIcon(image);
            if (publisher is not null) row.SetPublisher(await publisher);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Names the publisher of the rows nobody has scrolled to yet, one at a time and after the list has settled, because searching by publisher should find them too.
    /// One at a time keeps a slow PC's disk and processor free for what the person is doing.
    /// </summary>
    private async Task BackfillPublishersAsync(int mine)
    {
        try
        {
            await Task.Delay(750, lifetime.Token);
            foreach (var row in all.ToArray())
            {
                if (mine != generation) return;
                if (row.PublisherRequested) continue;
                row.PublisherRequested = true;
                row.SetPublisher(await publishers.FindAsync(row.Package));
                lifetime.Token.ThrowIfCancellationRequested();
            }
            // The search box may be waiting on a publisher name that only now exists.
            if (mine == generation && query.Length > 0) ApplyFilter(keepPosition: true);
        }
        catch (OperationCanceledException) { }
    }

    // Records how much of Windows' own installed-apps list this page shows, so a gap in detection is visible in Logs instead of silent.
    private async Task LogCoverageAsync()
    {
        try
        {
            var listed = all.Select(x => x.Name).ToArray();
            var coverage = await Task.Run(() => InstalledInventory.Coverage(InstalledInventory.ReadListedByWindows(), listed), lifetime.Token);
            if (coverage.Total == 0 || coverage.Missing.Count == 0) return;
            log.Write(ActivityLevel.Info, ActivityArea.Install, $"{coverage.Listed} of {coverage.Total} installed programs are listed ({coverage.Percent:0}%)",
                "Windows lists these but no package manager reported them:\n" + string.Join("\n", coverage.Missing));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
    }

    public void QueueAction(InstalledRow row, PackageAction action)
    {
        if (!row.CanAct || action == PackageAction.Update && !row.CanUpdate) return;
        queue.Enqueue([PackageOperation.Manage(row.Package, action)]);
        row.IsSelected = false;
    }
    public void QueueSelected(PackageAction action)
    {
        var selected = Rows.Where(x => x.IsSelected && x.CanAct && (action != PackageAction.Update || x.CanUpdate)).ToArray();
        queue.Enqueue(selected.Select(x => PackageOperation.Manage(x.Package, action)));
        selecting = true;
        try { foreach (var row in Rows) row.IsSelected = false; }
        finally { selecting = false; }
        NotifySelection();
    }
    public void SelectAll(bool value)
    {
        selecting = true;
        try { foreach (var row in Rows.Where(x => x.CanAct)) row.IsSelected = value; }
        finally { selecting = false; }
        NotifySelection();
    }
    public IReadOnlyList<InstalledRow> Selected => Rows.Where(x => x.IsSelected && x.CanAct).ToArray();
    public bool? AllSelected
    {
        get
        {
            var eligible = Rows.Where(x => x.CanAct).ToArray();
            var count = eligible.Count(x => x.IsSelected);
            return count == 0 ? false : count == eligible.Length ? true : null;
        }
    }
    public bool CanSelect => Rows.Any(x => x.CanAct);
    public bool CanUpdateSelected => Selected.Any(x => x.CanUpdate);
    public bool CanUninstallSelected => Selected.Count > 0;
    public string SelectionLabel => Selected.Count == 0 ? "" : $"{Selected.Count} selected";
    private void SelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (!selecting && e.PropertyName == nameof(InstalledRow.IsSelected)) NotifySelection(); }
    private void NotifySelection()
    { foreach (var name in new[] { nameof(AllSelected), nameof(CanSelect), nameof(CanUpdateSelected), nameof(CanUninstallSelected), nameof(SelectionLabel) }) Changed(name); }
    public void Dispose() { IconService.DarkChanged -= ReloadIcons; queue.StateChanged -= NotifyState; workspace.PropertyChanged -= WorkspaceChanged; lifetime.Cancel(); lifetime.Dispose(); }
}
