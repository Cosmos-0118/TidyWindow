using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

public sealed class InstalledRow(ManagedPackage package) : ObservableState
{
    public ManagedPackage Package { get; } = package;
    public string Name => Package.Name;
    // Registry programs and Store apps have no package name to show; their WinGet id is a registry path.
    public string Detail => Package.Origin == AppOrigin.Package ? $"{Package.ManagerName}{(Package.Global ? " · all users" : "")} · {Package.Id}" : Source;
    public string Id => Package.Id;
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
    public string CompactDetail => $"{Source} · {Version}" + (queueStatus.Length > 0 ? $" · {queueStatus}" : Package.Held ? " · Pinned" : Package.AvailableVersion is { } latest ? $" → {latest}" : Package.Note.Length > 0 ? $" · {Package.Note}" : "");
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
    public bool Refreshing { get => refreshing; private set { Set(ref refreshing, value); NotifyState(); } }

    public bool CanRefresh => !Refreshing && workspace.CanRefresh;
    public string ProgressText => "Checking installed apps and updates…";
    public Visibility ProgressVisibility => Refreshing ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyVisibility => Rows.Count == 0 && !Refreshing ? Visibility.Visible : Visibility.Collapsed;
    public string EmptyTitle => query.Length > 0 ? "No matching apps" : Errors.Length > 0 ? "The list could not be fully checked" : updatesOnly ? "No available updates" : "No packages to manage";
    public string EmptyDetail => Errors.Length > 0 ? "Open Logs for details, then refresh to try again." : query.Length > 0 || updatesOnly ? "Change your search or filter to see more apps." : "Packages from WinGet, Chocolatey and Scoop appear here. Check Overview if a manager needs attention.";
    private string errors = "";
    public string Errors { get => errors; private set { Set(ref errors, value); Changed(nameof(ErrorVisibility)); } }
    public Visibility ErrorVisibility => Errors.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public InstallationsViewModel(InstallationService service, WorkspaceViewModel workspace, ActivityLog log, OperationQueueViewModel queue)
    {
        this.service = service; this.workspace = workspace; this.log = log; this.queue = queue; queue.StateChanged += NotifyState;
        workspace.PropertyChanged += WorkspaceChanged;
    }
    private void WorkspaceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkspaceViewModel.Busy) or nameof(WorkspaceViewModel.CanRefresh) or nameof(WorkspaceViewModel.CanQueue)) NotifyState();
        if (e.PropertyName == nameof(WorkspaceViewModel.Busy) && !workspace.Busy) NeedsRefresh = true;
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
        foreach (var name in new[] { nameof(CanRefresh), nameof(ProgressVisibility), nameof(ProgressText), nameof(EmptyVisibility) }) Changed(name);
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
    private void ApplyFilter()
    {
        // One reset instead of clearing and re-adding every row: a list of hundreds of apps would re-measure on each step.
        Rows.Reset(all.Where(x => (!updatesOnly || x.Package.AvailableVersion is not null && !x.Package.Held) &&
            (query.Length == 0 || x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Package.Id.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Package.ManagerName.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Source.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(x => x.Package.AvailableVersion is not null && !x.Package.Held).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Package.Manager).ToArray());
        foreach (var name in new[] { nameof(EmptyVisibility), nameof(EmptyTitle), nameof(EmptyDetail), nameof(TableHeaderVisibility) }) Changed(name);
        NotifySelection();
    }
    public async Task RefreshAsync()
    {
        if (!CanRefresh) return;
        Refreshing = true; Errors = "";
        if (!loaded) { all.Clear(); ApplyFilter(); }
        try
        {
            var managers = workspace.Managers.Select(x => x.Status).Where(x => x is not null).Cast<ManagerStatus>().ToArray();
            var pending = managers.Select(x => service.ReadAsync(x, token: lifetime.Token)).ToList();
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending); pending.Remove(completed);
                var inventory = await completed;
                inventories[inventory.Manager] = inventory;
                foreach (var row in all.Where(x => x.Package.Manager == inventory.Manager)) row.PropertyChanged -= SelectionChanged;
                all.RemoveAll(x => x.Package.Manager == inventory.Manager);
                foreach (var package in inventory.Packages)
                {
                    var row = new InstalledRow(package) { Compact = compact };
                    row.PropertyChanged += SelectionChanged;
                    all.Add(row);
                }
                Errors = string.Join("\n", inventories.Values.Where(x => x.Error is not null || x.UpdateError is not null)
                    .Select(x => $"{ManagerDefinition.All.Single(m => m.Kind == x.Manager).Name}: {x.Error ?? "Updates could not be checked. " + x.UpdateError}"));
                ApplyFilter();
            }
            loaded = true; NeedsRefresh = false;
            await LogCoverageAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Errors = ex.Message; log.Write(ActivityLevel.Warning, ActivityArea.Install, "Installed packages could not be checked", ex.ToString()); }
        finally { Refreshing = false; NotifyState(); }
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
    public void Dispose() { queue.StateChanged -= NotifyState; workspace.PropertyChanged -= WorkspaceChanged; lifetime.Cancel(); lifetime.Dispose(); }
}
