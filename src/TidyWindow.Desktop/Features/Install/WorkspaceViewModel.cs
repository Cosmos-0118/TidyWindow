using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

public sealed class ManagerRow(ManagerDefinition definition) : ObservableState
{
    public ManagerDefinition Definition { get; } = definition;
    public ManagerStatus? Status { get; private set; }
    public string Name => Definition.Name;
    public ImageSource Icon
    {
        get
        {
            var uri = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Managers", Definition.Kind switch { ManagerKind.Winget => "winget.png", ManagerKind.Chocolatey => "chocolatey.svg", _ => "scoop.png" }));
            return Definition.Kind == ManagerKind.Chocolatey ? new SvgImageSource(uri) : new BitmapImage(uri) { DecodePixelWidth = 32, DecodePixelType = DecodePixelType.Logical };
        }
    }
    public string Version => Status?.Version ?? "";
    public string StatusText => Busy ? "Working…" : Status?.Health switch { ManagerHealth.Ready => "Ready", ManagerHealth.Missing => "Not installed", ManagerHealth.ConnectionIssue => "Connection issue", ManagerHealth.Restricted => "Restricted", ManagerHealth.Unavailable => "Needs repair", _ => "Checking…" };
    public string Detail => Status?.Detail ?? "Checking this device";
    public Visibility SetupVisibility => Status is not null && Status.Health != ManagerHealth.Ready ? Visibility.Visible : Visibility.Collapsed;
    public string SetupAction => Busy ? (Status?.Health == ManagerHealth.Missing ? "Installing…" : "Repairing…")
        : Status?.Health switch { ManagerHealth.Missing => "Install", ManagerHealth.ConnectionIssue => "Retry", ManagerHealth.Restricted => "Details", _ => "Repair" };
    private bool busy;
    public bool Busy { get => busy; set { if (Set(ref busy, value)) { Changed(nameof(StatusText)); Changed(nameof(SetupAction)); Changed(nameof(BusyVisibility)); } } }
    private string progress = "";
    /// <summary>What the running setup is doing right now, shown on the manager's own row so the click visibly did something.</summary>
    public string Progress { get => progress; set => Set(ref progress, value); }
    public Visibility BusyVisibility => Busy ? Visibility.Visible : Visibility.Collapsed;
    private bool canRecover;
    public bool CanRecover { get => canRecover; set => Set(ref canRecover, value); }
    public bool Compact { get; set; }
    public GridLength StatusColumnWidth => new(Compact ? 0 : 145);
    public GridLength SetupColumnWidth => Compact ? GridLength.Auto : new(104);
    public int StatusColumn => Compact ? 1 : 2;
    public int StatusRow => Compact ? 1 : 0;
    public void SetCompact(bool value)
    {
        if (Compact == value) return;
        Compact = value;
        foreach (var name in new[] { nameof(StatusColumnWidth), nameof(SetupColumnWidth), nameof(StatusColumn), nameof(StatusRow) }) Changed(name);
    }
    private bool lightTheme;
    public void SetLightTheme(bool value) { lightTheme = value; Changed(nameof(StatusBrush)); }
    public Brush StatusBrush => BrushCache.Rgb(Status?.Health switch
    {
        ManagerHealth.Ready => lightTheme ? 0x1C703F : 0xA4E8C1,
        ManagerHealth.Unavailable or ManagerHealth.Restricted or ManagerHealth.ConnectionIssue => lightTheme ? 0x865500 : 0xF4BF6E,
        _ => lightTheme ? 0x626262 : 0xA4A4AD
    });
    public void Update(ManagerStatus status)
    { Status = status; foreach (var name in new[] { nameof(Version), nameof(StatusText), nameof(Detail), nameof(SetupVisibility), nameof(SetupAction), nameof(StatusBrush), nameof(BucketsToggleVisibility), nameof(BucketsVisibility) }) Changed(name); }

    /// <summary>Only Scoop has buckets: lists of apps it can install, which a search covers only once they are added.</summary>
    public BucketsViewModel? Buckets { get; } = definition.Kind == ManagerKind.Scoop ? new() : null;
    private bool bucketsOpen;
    public bool BucketsOpen => bucketsOpen;
    public Visibility BucketsToggleVisibility => Buckets is not null && Status?.Health == ManagerHealth.Ready ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BucketsVisibility => bucketsOpen && BucketsToggleVisibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
    public string BucketsChevron => bucketsOpen ? "" : "";
    public string BucketsName => bucketsOpen ? "Hide Scoop buckets" : "Show Scoop buckets";
    public void ToggleBuckets()
    {
        bucketsOpen = !bucketsOpen;
        foreach (var name in new[] { nameof(BucketsOpen), nameof(BucketsVisibility), nameof(BucketsChevron), nameof(BucketsName) }) Changed(name);
    }
}

public sealed class PackageRow(Package package) : ObservableState
{
    public Package Package { get; } = package;
    public string Name => Package.Name;
    public string Id => Package.Id;
    public string Version => Installed?.Version is { Length: > 0 } version ? version : Package.Version;
    public string Source => Package.ManagerName;
    public string InstallLabel => $"{Action} {Name} from {Source}";
    public InstalledApp? Installed { get; private set; }
    public string ActionDetail => Installed is null ? $"Install {Name} from {Source}" : Installed.Version.Length == 0 ? "Already installed on this computer" : $"Installed version {Installed.Version}";
    public void SetInstalled(InstalledApp? value)
    {
        Installed = value;
        Changed(nameof(Version)); Changed(nameof(CompactDetail)); Changed(nameof(ActionDetail));
    }
    public string CompactDetail => $"{Version} · {Source}";
    private bool compact;
    public bool Compact
    {
        get => compact;
        set { if (Set(ref compact, value)) { Changed(nameof(DetailColumnWidth)); Changed(nameof(CompactVisibility)); Changed(nameof(WideVisibility)); } }
    }
    public GridLength DetailColumnWidth => new(Compact ? 0 : 100);
    public Visibility CompactVisibility => Compact ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WideVisibility => Compact ? Visibility.Collapsed : Visibility.Visible;
    private bool canInstall = true;
    private string action = "Install";
    public bool CanInstall { get => canInstall; set => Set(ref canInstall, value); }
    public string Action { get => action; set { if (Set(ref action, value)) Changed(nameof(InstallLabel)); } }
}

public sealed class WorkspaceViewModel : ObservableState, IDisposable
{
    private readonly PackageService service;
    private readonly ActivityLog log;
    private readonly ManagerHealthService health;
    private readonly ManagerRecoveryService recovery;
    private CancellationTokenSource? searchCancellation;
    private readonly CancellationTokenSource lifetime = new();
    private int searchGeneration;
    private readonly OperationQueueViewModel queue;
    private InstalledInventory inventory = new([]);
    private bool inventoryChecking = true;
    private string sourceErrors = "";
    private string inventoryErrors = "";
    public ObservableCollection<ManagerRow> Managers { get; } = new(ManagerDefinition.All.Select(x => new ManagerRow(x)));
    public BulkObservableCollection<PackageRow> Packages { get; } = [];
    private bool compactResults;
    public Visibility ResultsHeaderVisibility => compactResults ? Visibility.Collapsed : Visibility.Visible;
    internal void ApplyLayout(AdaptiveLayout layout)
    {
        compactResults = layout.CompactResults;
        foreach (var manager in Managers) manager.SetCompact(layout.CompactManagers);
        foreach (var package in Packages) package.Compact = compactResults;
        Changed(nameof(ResultsHeaderVisibility));
    }
    private bool detecting;
    public bool Detecting { get => detecting; private set { Set(ref detecting, value); Changed(nameof(CanRefresh)); Changed(nameof(CanQueue)); SyncBucketLock(); } }
    public bool CanRefresh => !Detecting && !inventoryChecking && !Busy;
    private bool recovering;
    public bool Recovering { get => recovering; private set { Set(ref recovering, value); Changed(nameof(Busy)); Changed(nameof(CanRefresh)); Changed(nameof(CanQueue)); Changed(nameof(RecoveryProgressVisibility)); SyncBucketLock(); } }
    public bool Busy => Recovering || bucketWorking || queue.HasWork;
    public bool CanQueue => !Detecting && !inventoryChecking && !Recovering && !bucketWorking;
    private bool bucketWorking;
    private void QueueChanged()
    {
        Changed(nameof(Busy)); Changed(nameof(CanRefresh)); Changed(nameof(CanQueue));
        foreach (var manager in Managers) manager.CanRecover = !Busy && CanQueue;
        SyncBucketLock();
        foreach (var row in Packages) UpdateInstalledState(row);
    }

    /// <summary>Buckets are cloned with git into Scoop's own folder, so adding one waits while anything else is installing, recovering or checking.</summary>
    private void SyncBucketLock() { foreach (var manager in Managers) if (manager.Buckets is { } buckets) buckets.Locked = Busy || !CanQueue; }
    public Visibility RecoveryProgressVisibility => Recovering ? Visibility.Visible : Visibility.Collapsed;
    private string recoveryMessage = "";
    public string RecoveryMessage { get => recoveryMessage; private set { Set(ref recoveryMessage, value); Changed(nameof(RecoveryVisibility)); } }
    private string recoveryDetail = "";
    public string RecoveryDetail { get => recoveryDetail; private set => Set(ref recoveryDetail, value); }
    public Visibility RecoveryVisibility => RecoveryMessage.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    private bool searching;
    public bool Searching { get => searching; private set { Set(ref searching, value); Changed(nameof(CancelVisibility)); Changed(nameof(EmptyVisibility)); Changed(nameof(SearchLoading)); } }
    /// <summary>True while a search has nothing to show yet; once results arrive they replace the loading state.</summary>
    public bool SearchLoading => Searching && Packages.Count == 0;
    public Visibility CancelVisibility => Searching ? Visibility.Visible : Visibility.Collapsed;
    private string sourceStatus = "";
    /// <summary>Which sources are still being waited for, or what a late one added. Empty when the list is complete.</summary>
    public string SourceStatus { get => sourceStatus; private set { if (Set(ref sourceStatus, value)) Changed(nameof(SourceStatusVisibility)); } }
    public Visibility SourceStatusVisibility => SourceStatus.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    private bool sourceBusy;
    public bool SourceBusy { get => sourceBusy; private set { if (Set(ref sourceBusy, value)) Changed(nameof(SourceBusyVisibility)); } }
    public Visibility SourceBusyVisibility => SourceBusy ? Visibility.Visible : Visibility.Collapsed;
    private string searchSummary = "Search for an app";
    public string SearchSummary { get => searchSummary; private set => Set(ref searchSummary, value); }
    private string emptyDetail = "Enter a name or package ID.";
    public string EmptyDetail { get => emptyDetail; private set => Set(ref emptyDetail, value); }
    public Visibility EmptyVisibility => Packages.Count == 0 && !Searching ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ResultsVisibility => Packages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    private string errors = "";
    public string Errors { get => errors; private set { Set(ref errors, value); Changed(nameof(ErrorVisibility)); } }
    public Visibility ErrorVisibility => Errors.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public WorkspaceViewModel(PackageService service, ActivityLog log, OperationQueueViewModel queue)
    {
        this.service = service; this.log = log; this.queue = queue; queue.StateChanged += QueueChanged;
        var runner = new ProcessRunner(); var locator = new ExecutableLocator();
        health = new(service, runner, locator);
        recovery = new(service, runner, locator);
        scoopBuckets = new(service, runner);
    }

    private bool quietRefreshing;

    /// <param name="quiet">Re-reads the managers and what is installed without locking anything: no "checking" overlay, and installing and queueing stay available.
    /// Used to catch up after the queue finishes a job, when the person is already looking at a list and may want to queue the next thing.</param>
    public async Task RefreshAsync(bool quiet = false)
    {
        if (Detecting || Busy || quietRefreshing) return;
        if (quiet) { await QuietRefreshAsync(); return; }
        foreach (var manager in Managers) manager.CanRecover = false;
        service.ClearSearchCache();
        Detecting = true;
        try
        {
            var statuses = await Task.WhenAll(ManagerDefinition.All.Select(x => Task.Run(() => health.CheckAsync(x.Kind, token: lifetime.Token))));
            foreach (var status in statuses)
            {
                var row = Managers.Single(x => x.Definition.Kind == status.Definition.Kind);
                ManagerStatusLog.Record(log, row.Status, status);
                row.Update(status);
            }
            Detecting = false;
            // The first search should not be the one that pays for Chocolatey's connection and Scoop's index.
            _ = service.WarmUpAsync(statuses, lifetime.Token);
            inventoryChecking = true; Changed(nameof(CanRefresh));
            foreach (var package in Packages) UpdateInstalledState(package);
            try
            {
                SetInventory(await service.GetInstalledAsync(statuses, lifetime.Token));
            }
            finally
            {
                inventoryChecking = false; Changed(nameof(CanRefresh)); SyncBucketLock();
                foreach (var package in Packages) UpdateInstalledState(package);
                UpdateErrors();
            }
        }
        catch (OperationCanceledException) { }
        finally { Detecting = false; foreach (var manager in Managers) manager.CanRecover = !Busy && !inventoryChecking; }
        // A bucket list that is open should reflect the Scoop that was just checked.
        foreach (var manager in Managers.Where(x => x.BucketsOpen)) await LoadBucketsAsync(manager);
    }

    private async Task QuietRefreshAsync()
    {
        quietRefreshing = true;
        try
        {
            service.ClearSearchCache();
            var statuses = await Task.WhenAll(ManagerDefinition.All.Select(x => Task.Run(() => health.CheckAsync(x.Kind, token: lifetime.Token))));
            foreach (var status in statuses)
            {
                var row = Managers.Single(x => x.Definition.Kind == status.Definition.Kind);
                ManagerStatusLog.Record(log, row.Status, status);
                row.Update(status);
            }
            SetInventory(await service.GetInstalledAsync(statuses, lifetime.Token));
            foreach (var package in Packages) UpdateInstalledState(package);
            UpdateErrors();
        }
        catch (OperationCanceledException) { }
        finally { quietRefreshing = false; }
    }

    private readonly ScoopBuckets scoopBuckets;

    public async Task ToggleBucketsAsync(ManagerRow row)
    {
        if (row.Buckets is null) return;
        row.ToggleBuckets();
        if (row.BucketsOpen) await LoadBucketsAsync(row);
    }

    private async Task LoadBucketsAsync(ManagerRow row)
    {
        if (row.Buckets is not { } buckets || row.Status is not { Health: ManagerHealth.Ready } status) return;
        buckets.Loading = !buckets.Loaded;
        try { buckets.Apply(await Task.Run(() => scoopBuckets.Read(status), lifetime.Token)); }
        catch (OperationCanceledException) { }
        finally { buckets.Loading = false; SyncBucketLock(); }
    }

    public async Task AddBucketAsync(ManagerRow row, BucketRow bucket)
    {
        if (row.Buckets is not { } buckets || row.Status is not { } status || !bucket.CanAdd || Busy || !CanQueue) return;
        bucketWorking = true; bucket.Adding = true; buckets.ClearMessage();
        Changed(nameof(Busy)); Changed(nameof(CanQueue)); Changed(nameof(CanRefresh));
        foreach (var manager in Managers) manager.CanRecover = false;
        SyncBucketLock();
        buckets.Show($"Adding {bucket.Name}. This downloads its app list and can take a minute…", InfoBarSeverity.Informational);
        log.Write(ActivityLevel.Info, ActivityArea.Managers, $"Adding the {bucket.Name} Scoop bucket", bucket.Repository);
        try
        {
            var result = await scoopBuckets.AddAsync(status, bucket.Name, lifetime.Token);
            buckets.Show(result.Message, result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error, result.Detail);
            if (result.Ok) log.Write(ActivityLevel.Success, ActivityArea.Managers, result.Message);
            else log.Write(ActivityLevel.Warning, ActivityArea.Managers, $"The {bucket.Name} bucket could not be added", result.Message + (result.Detail.Length > 0 ? "\n" + result.Detail : ""));
        }
        catch (OperationCanceledException) { buckets.Show("Adding the bucket was interrupted. Check the list before trying again.", InfoBarSeverity.Warning); }
        catch (Exception ex) { buckets.Show($"{bucket.Name} could not be added.", InfoBarSeverity.Error, ex.Message); log.Write(ActivityLevel.Error, ActivityArea.Managers, $"The {bucket.Name} bucket could not be added", ex.ToString()); }
        finally
        {
            bucketWorking = false; bucket.Adding = false;
            Changed(nameof(Busy)); Changed(nameof(CanQueue)); Changed(nameof(CanRefresh));
            foreach (var manager in Managers) manager.CanRecover = !Busy && CanQueue;
            await LoadBucketsAsync(row);
            foreach (var package in Packages) UpdateInstalledState(package);
        }
    }

    public async Task RecoverAsync(ManagerRow row)
    {
        if (Busy || Detecting || inventoryChecking) return;
        CancelSearch();
        Recovering = true; row.Busy = true;
        foreach (var manager in Managers) manager.CanRecover = false;
        foreach (var package in Packages) package.CanInstall = false;
        RecoveryMessage = row.Progress = $"Checking {row.Name}…"; RecoveryDetail = "";
        log.Write(ActivityLevel.Info, ActivityArea.Managers, $"{row.Name} setup started");
        try
        {
            var progress = new Progress<string>(message => { if (Recovering) RecoveryMessage = row.Progress = message; });
            var result = await recovery.RecoverAsync(row.Definition.Kind, progress, lifetime.Token);
            row.Update(result.Status);
            RecoveryDetail = result.Status.Detail + (result.LogDirectory is null ? "" : "\n\nDetails and backups: " + result.LogDirectory);
            if (result.Success)
            {
                service.ClearSearchCache();
                SetInventory(await service.GetInstalledAsync(Managers.Where(x => x.Status is not null).Select(x => x.Status!).ToArray(), lifetime.Token));
                UpdateErrors();
            }
            RecoveryMessage = result.Message;
            // A failed attempt that changed nothing (declined, restricted, offline) is a warning; one that changed things and failed is an error.
            if (result.Success) log.Write(ActivityLevel.Success, ActivityArea.Managers, result.Changed ? $"{row.Name} setup finished" : $"{row.Name} checked, no changes needed", result.LogDirectory is null ? null : "Backups and details: " + result.LogDirectory);
            else log.Write(result.Changed ? ActivityLevel.Error : ActivityLevel.Warning, ActivityArea.Managers, $"{row.Name} setup failed", result.Message + "\n" + result.Status.Detail + (result.LogDirectory is null ? "" : "\nBackups and details: " + result.LogDirectory));
        }
        catch (OperationCanceledException) { RecoveryMessage = "Setup was interrupted. Check the manager before retrying."; log.Write(ActivityLevel.Warning, ActivityArea.Managers, $"{row.Name} setup was interrupted", "Check the manager before retrying."); }
        catch (Exception ex) { RecoveryMessage = "Setup could not be completed."; RecoveryDetail = ex.Message; log.Write(ActivityLevel.Error, ActivityArea.Managers, $"{row.Name} setup failed", ex.ToString()); }
        finally
        {
            Recovering = false; row.Busy = false; row.Progress = "";
            foreach (var manager in Managers) manager.CanRecover = true;
            foreach (var package in Packages) UpdateInstalledState(package);
        }
    }

    public async Task SearchAsync(string query, ManagerKind? filter, bool debounce = true)
    {
        var generation = ++searchGeneration;
        searchCancellation?.Cancel();
        searchCancellation?.Dispose();
        searchCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = searchCancellation.Token;
        Packages.Reset([]); Changed(nameof(EmptyVisibility)); Changed(nameof(ResultsVisibility)); Changed(nameof(SearchLoading)); sourceErrors = ""; UpdateErrors();
        SourceStatus = ""; SourceBusy = false;
        query = query.Trim();
        if (query.Length < 2)
        {
            Searching = false; SearchSummary = "Search for an app";
            EmptyDetail = query.Length == 0 ? "Enter a name or package ID." : "Type at least 2 characters.";
            return;
        }
        Searching = true; SearchSummary = $"Searching for “{query}”…"; EmptyDetail = "Checking available sources. You can keep typing to refine your search.";
        try
        {
            if (debounce) await Task.Delay(200, token);
            if (Detecting) { SearchSummary = "Package managers are still being checked"; EmptyDetail = "Try your search again in a moment."; return; }
            var available = Managers.Where(x => x.Status?.Health == ManagerHealth.Ready && (filter is null || x.Definition.Kind == filter)).Select(x => x.Status!).ToArray();
            if (available.Length == 0) { SearchSummary = "No available sources"; EmptyDetail = "Set up the selected package manager on Overview, then refresh its status."; return; }
            var sources = available.Select(x => (x.Definition.Kind, service.SearchAsync(x, query, token))).ToList();
            var logged = new HashSet<ManagerKind>();
            var waitedFor = new List<ManagerKind>();
            var shown = 0;
            // One ranked list for everything that answers together. A source that is slow enough to miss it is announced, not slipped in.
            await foreach (var snapshot in PackageSearchSession.StreamAsync(sources, query, PackageSearchSession.DefaultSettle, token: token))
            {
                if (generation != searchGeneration) return;
                // Searches themselves are routine; only a source failing is worth a line.
                foreach (var failed in snapshot.Answered.Where(x => x.Error is not null && logged.Add(x.Manager)))
                    log.Write(ActivityLevel.Warning, ActivityArea.Search, $"{SourceName(failed.Manager)} search failed", failed.Error!);
                var existing = Packages.ToDictionary(x => x.Package.Key);
                var rows = new List<PackageRow>(snapshot.Packages.Count);
                foreach (var package in snapshot.Packages)
                {
                    var row = existing.GetValueOrDefault(package.Key) ?? new PackageRow(package);
                    row.Compact = compactResults;
                    UpdateInstalledState(row);
                    rows.Add(row);
                }
                Packages.Reset(rows);
                sourceErrors = string.Join("\n", snapshot.Answered.Where(x => x.Error is not null).Select(x => $"{SourceName(x.Manager)}: {x.Error}"));
                UpdateErrors();
                if (!snapshot.Complete) { waitedFor = [.. snapshot.Waiting]; SourceStatus = $"Still searching {Join(snapshot.Waiting)}…"; SourceBusy = true; }
                else
                {
                    SourceBusy = false;
                    // Results that arrive after the list was shown are said out loud, so nobody misses them.
                    var added = snapshot.Packages.Count - shown;
                    SourceStatus = waitedFor.Count > 0 && added > 0 ? $"{Join(waitedFor)} answered: {added} more {(added == 1 ? "result was" : "results were")} added." : "";
                }
                shown = snapshot.Packages.Count;
                Changed(nameof(ResultsVisibility)); Changed(nameof(SearchLoading));
            }
            SearchSummary = Packages.Count == 0 ? Errors.Length > 0 ? "Search could not be completed" : $"No results for “{query}”" : "";
            EmptyDetail = Errors.Length > 0 ? "Open Logs for details, then try again."
                : available.Any(x => x.Definition.Kind == ManagerKind.Scoop) ? "Try a different name or source. Scoop only searches the buckets you have added; add more under Package managers."
                : "Try a different name or source.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (generation == searchGeneration) { log.Write(ActivityLevel.Error, ActivityArea.Search, "Search failed unexpectedly", ex.ToString()); sourceErrors = ex.Message; UpdateErrors(); SearchSummary = "Search needs attention"; EmptyDetail = "Check the details above and try again."; } }
        finally { if (generation == searchGeneration) { Searching = false; Changed(nameof(EmptyVisibility)); Changed(nameof(ResultsVisibility)); Changed(nameof(SearchLoading)); } }
    }

    private static string SourceName(ManagerKind kind) => ManagerDefinition.All.Single(m => m.Kind == kind).Name;
    private static string Join(IReadOnlyList<ManagerKind> kinds) => kinds.Count == 1 ? SourceName(kinds[0]) : string.Join(", ", kinds.Take(kinds.Count - 1).Select(SourceName)) + " and " + SourceName(kinds[^1]);

    public void CancelSearch()
    {
        ++searchGeneration; searchCancellation?.Cancel(); Searching = false; SourceBusy = false; SourceStatus = "";
        SearchSummary = "Search cancelled"; EmptyDetail = "Edit your search or press Enter to try again.";
    }

    public void QueueInstall(PackageRow row)
    {
        if (!row.CanInstall || !CanQueue) return;
        queue.Enqueue([PackageOperation.Install(row.Package)]);
    }

    private void UpdateInstalledState(PackageRow row)
    {
        row.SetInstalled(inventory.Find(row.Package));
        var entry = queue.Find(row.Package.Manager, row.Package.Id);
        var pending = entry?.State is QueueItemState.Waiting or QueueItemState.Running;
        var completedInstall = entry is { State: QueueItemState.Finished, Operation.Kind: PackageOperationKind.Install, Result.Outcome: ActionOutcome.Completed or ActionOutcome.RestartRequired };
        var installed = row.Installed is not null || completedInstall && queue.HasWork;
        row.CanInstall = CanQueue && !installed && !pending;
        row.Action = pending ? entry!.State == QueueItemState.Waiting ? "Queued" : "Working…"
            : installed ? "Installed" : inventoryChecking ? "Checking…"
            : entry is { State: QueueItemState.Finished, Result.Outcome: ActionOutcome.Failed or ActionOutcome.Unverified } ? "Retry" : "Install";
    }

    private void SetInventory(InstalledInventory value)
    {
        inventory = value;
        inventoryErrors = string.Join("\n", value.Errors);
        if (value.Errors.Any()) log.Write(ActivityLevel.Warning, ActivityArea.Managers, "Installed apps could not be fully checked", inventoryErrors);
    }

    private void UpdateErrors() => Errors = string.Join("\n", new[] { sourceErrors, inventoryErrors }.Where(x => x.Length > 0));


    public void Dispose() { queue.StateChanged -= QueueChanged; lifetime.Cancel(); searchCancellation?.Cancel(); lifetime.Dispose(); searchCancellation?.Dispose(); }
}
