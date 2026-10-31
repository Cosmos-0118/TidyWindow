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
    public string UpdateDetail => Package.Held ? "Pinned in package manager" : Package.AvailableVersion is { Length: > 0 } latest ? $"Update to {latest}" : Package.Note;
    private string queueStatus = "";
    public string Available => queueStatus.Length > 0 ? queueStatus : Package.Held ? "Pinned" : Package.AvailableVersion ?? (Package.Note.Length > 0 ? "Needs attention" : "—");
    public string CompactDetail => $"{(Publisher.Length > 0 ? Publisher + " · " : "")}{Source} · {Version}" + (queueStatus.Length > 0 ? $" · {queueStatus}" : Package.Held ? " · Pinned" : Package.AvailableVersion is { } latest ? $" → {latest}" : Package.Note.Length > 0 ? $" · {Package.Note}" : "");
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
    private bool listening;
    public InstallationsViewModel(InstallationService service, WorkspaceViewModel workspace, ActivityLog log, OperationQueueViewModel queue, IconService? iconService = null, PublisherService? publisherService = null)
    {
        this.service = service; this.workspace = workspace; this.log = log; this.queue = queue; queue.StateChanged += NotifyState;
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
                row.CanAct = !Refreshing && workspace.CanQueue && entry?.State is not (QueueItemState.Waiting or QueueItemState.Running);
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
    /// <param name="keepPosition">The list is being refreshed under the person, not re-filtered by them: change only the rows that differ, so the scroll position stays.</param>
    private void ApplyFilter(bool keepPosition = false)
    {
        var shown = all.Where(x => (!updatesOnly || x.Package.AvailableVersion is not null && !x.Package.Held) &&
            (query.Length == 0 || x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Publisher.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Package.Id.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Package.ManagerName.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Source.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(x => x.Package.AvailableVersion is not null && !x.Package.Held).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Package.Manager).ToArray();
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
        if (loaded) Updating = true; else { Refreshing = true; all.Clear(); ApplyFilter(); }
        try
        {
            var managers = workspace.Managers.Select(x => x.Status).Where(x => x is not null).Cast<ManagerStatus>().ToArray();
            var pending = managers.Select(x => service.ReadAsync(x, token: lifetime.Token)).ToList();
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending); pending.Remove(completed);
                var inventory = await completed;
                inventories[inventory.Manager] = inventory;
                // An app that did not change keeps its row, with its selection, so only what really changed is touched on screen.
                var previous = new Dictionary<string, InstalledRow>();
                foreach (var old in all.Where(x => x.Package.Manager == inventory.Manager)) previous.TryAdd(old.Package.Key, old);
                var reused = new HashSet<InstalledRow>();
                var current = new List<InstalledRow>(inventory.Packages.Count);
                foreach (var package in inventory.Packages)
                {
                    if (previous.TryGetValue(package.Key, out var old) && old.Package == package && reused.Add(old)) { current.Add(old); continue; }
                    var row = new InstalledRow(package) { Compact = compact };
                    // What was found before comes back with the list, so a refresh does not blank it.
                    if (icons.TryGet(row.IconQuery, out var cached) && cached is not null) row.SetIcon(cached);
                    if (publishers.TryGet(package, out var named)) row.SetPublisher(named);
                    row.PropertyChanged += SelectionChanged;
                    current.Add(row);
                }
                foreach (var old in all.Where(x => x.Package.Manager == inventory.Manager && !reused.Contains(x))) old.PropertyChanged -= SelectionChanged;
                all.RemoveAll(x => x.Package.Manager == inventory.Manager);
                all.AddRange(current);
                Errors = string.Join("\n", inventories.Values.Where(x => x.Error is not null || x.UpdateError is not null)
                    .Select(x => $"{ManagerDefinition.All.Single(m => m.Kind == x.Manager).Name}: {x.Error ?? "Updates could not be checked. " + x.UpdateError}"));
                ApplyFilter(keepPosition: true);
                if (!listening) { IconService.DarkChanged += ReloadIcons; listening = true; }
                _ = LoadDetailsAsync(current);
            }
            loaded = true; NeedsRefresh = false;
            await LogCoverageAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Errors = ex.Message; log.Write(ActivityLevel.Warning, ActivityArea.Install, "Installed packages could not be checked", ex.ToString()); }
        finally { Refreshing = false; Updating = false; NotifyState(); }
    }
    // Icons and publishers arrive a moment after the list, a few at a time, and a row without them keeps its glyph and its package line.
    private void ReloadIcons() => _ = LoadDetailsAsync(all, everyRow: true);

    private async Task LoadDetailsAsync(IReadOnlyList<InstalledRow> rows, bool everyRow = false)
    {
        // What is on screen first.
        var shown = Rows.ToHashSet();
        var queued = rows.Where(x => everyRow || x.Icon is null || x.Publisher.Length == 0).OrderBy(x => shown.Contains(x) ? 0 : 1).ToList();
        try { await Task.WhenAll(queued.Select(LoadRowAsync)); }
        catch (OperationCanceledException) { }
    }

    private async Task LoadRowAsync(InstalledRow row)
    {
        var icon = icons.LoadAsync(row.IconQuery, lifetime.Token);
        var publisher = publishers.FindAsync(row.Package);
        if (await icon is { } image) row.SetIcon(image);
        row.SetPublisher(await publisher);
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
