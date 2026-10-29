using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

public abstract class ObservableState : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Changed(name); return true; }
}

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
            return Definition.Kind == ManagerKind.Chocolatey ? new SvgImageSource(uri) : new BitmapImage(uri);
        }
    }
    public string Version => Status?.Version ?? "";
    public string StatusText => Busy ? "Working…" : Status?.Health switch { ManagerHealth.Ready => "Ready", ManagerHealth.Missing => "Not installed", ManagerHealth.ConnectionIssue => "Connection issue", ManagerHealth.Restricted => "Restricted", ManagerHealth.Unavailable => "Needs repair", _ => "Checking…" };
    public string Detail => Status?.Detail ?? "Checking this device";
    public Visibility SetupVisibility => Status is not null && Status.Health != ManagerHealth.Ready ? Visibility.Visible : Visibility.Collapsed;
    public string SetupAction => Status?.Health switch { ManagerHealth.Missing => "Install", ManagerHealth.ConnectionIssue => "Retry", ManagerHealth.Restricted => "Details", _ => "Repair" };
    private bool busy;
    public bool Busy { get => busy; set { if (Set(ref busy, value)) Changed(nameof(StatusText)); } }
    private bool canRecover;
    public bool CanRecover { get => canRecover; set => Set(ref canRecover, value); }
    public bool Compact { get; set; }
    public GridLength StatusColumnWidth => new(Compact ? 0 : 145);
    public GridLength SetupColumnWidth => Compact ? GridLength.Auto : new(86);
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
    public Brush StatusBrush => new SolidColorBrush(Status?.Health switch
    {
        ManagerHealth.Ready => lightTheme ? ColorHelper.FromArgb(255, 28, 112, 63) : ColorHelper.FromArgb(255,164,232,193),
        ManagerHealth.Unavailable or ManagerHealth.Restricted or ManagerHealth.ConnectionIssue => lightTheme ? ColorHelper.FromArgb(255, 134, 85, 0) : ColorHelper.FromArgb(255,244,191,110),
        _ => lightTheme ? ColorHelper.FromArgb(255, 98, 98, 98) : ColorHelper.FromArgb(255,164,164,173)
    });
    public void Update(ManagerStatus status)
    { Status = status; foreach (var name in new[] { nameof(Version), nameof(StatusText), nameof(Detail), nameof(SetupVisibility), nameof(SetupAction), nameof(StatusBrush) }) Changed(name); }
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
    private readonly ManagerHealthService health;
    private readonly ManagerRecoveryService recovery;
    private CancellationTokenSource? searchCancellation;
    private readonly CancellationTokenSource lifetime = new();
    private int searchGeneration;
    private readonly HashSet<string> installedThisSession = [];
    private InstalledInventory inventory = new([]);
    private bool inventoryChecking = true;
    private string sourceErrors = "";
    private string inventoryErrors = "";
    public ObservableCollection<ManagerRow> Managers { get; } = new(ManagerDefinition.All.Select(x => new ManagerRow(x)));
    public ObservableCollection<PackageRow> Packages { get; } = [];
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
    public bool Detecting { get => detecting; private set { Set(ref detecting, value); Changed(nameof(CanRefresh)); } }
    public bool CanRefresh => !Detecting && !inventoryChecking && !Busy;
    private bool recovering;
    public bool Recovering { get => recovering; private set { Set(ref recovering, value); Changed(nameof(Busy)); Changed(nameof(CanRefresh)); Changed(nameof(RecoveryProgressVisibility)); } }
    public bool Busy => Installing || Recovering;
    public Visibility RecoveryProgressVisibility => Recovering ? Visibility.Visible : Visibility.Collapsed;
    private string recoveryMessage = "";
    public string RecoveryMessage { get => recoveryMessage; private set { Set(ref recoveryMessage, value); Changed(nameof(RecoveryVisibility)); } }
    private string recoveryDetail = "";
    public string RecoveryDetail { get => recoveryDetail; private set => Set(ref recoveryDetail, value); }
    public Visibility RecoveryVisibility => RecoveryMessage.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    private bool searching;
    public bool Searching { get => searching; private set { Set(ref searching, value); Changed(nameof(CancelVisibility)); Changed(nameof(EmptyVisibility)); } }
    public Visibility CancelVisibility => Searching ? Visibility.Visible : Visibility.Collapsed;
    private string searchSummary = "Search for an app";
    public string SearchSummary { get => searchSummary; private set => Set(ref searchSummary, value); }
    private string emptyDetail = "Enter a name or package ID.";
    public string EmptyDetail { get => emptyDetail; private set => Set(ref emptyDetail, value); }
    public Visibility EmptyVisibility => Packages.Count == 0 && !Searching ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ResultsVisibility => Packages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    private string errors = "";
    public string Errors { get => errors; private set { Set(ref errors, value); Changed(nameof(ErrorVisibility)); } }
    public Visibility ErrorVisibility => Errors.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    private bool installing;
    public bool Installing { get => installing; private set { Set(ref installing, value); Changed(nameof(Busy)); Changed(nameof(InstallProgressVisibility)); Changed(nameof(CanRefresh)); foreach (var row in Managers) row.CanRecover = !value && !Recovering; } }
    public Visibility InstallProgressVisibility => Installing ? Visibility.Visible : Visibility.Collapsed;
    private string installTitle = "";
    public string InstallTitle { get => installTitle; private set { Set(ref installTitle, value); Changed(nameof(InstallVisibility)); } }
    private string installDetail = "";
    public string InstallDetail { get => installDetail; private set => Set(ref installDetail, value); }
    public Visibility InstallVisibility => InstallTitle.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public WorkspaceViewModel(PackageService service)
    {
        this.service = service;
        var runner = new ProcessRunner(); var locator = new ExecutableLocator();
        health = new(service, runner, locator);
        recovery = new(service, runner, locator);
    }

    public async Task RefreshAsync()
    {
        if (Detecting || Recovering) return;
        foreach (var manager in Managers) manager.CanRecover = false;
        service.ClearSearchCache();
        Detecting = true;
        try
        {
            var statuses = await Task.WhenAll(ManagerDefinition.All.Select(x => Task.Run(() => health.CheckAsync(x.Kind, token: lifetime.Token))));
            foreach (var status in statuses) Managers.Single(x => x.Definition.Kind == status.Definition.Kind).Update(status);
            Detecting = false;
            inventoryChecking = true; Changed(nameof(CanRefresh));
            foreach (var package in Packages) UpdateInstalledState(package);
            try
            {
                inventory = await service.GetInstalledAsync(statuses, lifetime.Token);
                inventoryErrors = string.Join("\n", inventory.Errors);
            }
            finally
            {
                inventoryChecking = false; Changed(nameof(CanRefresh));
                foreach (var package in Packages) UpdateInstalledState(package);
                UpdateErrors();
            }
        }
        catch (OperationCanceledException) { }
        finally { Detecting = false; foreach (var manager in Managers) manager.CanRecover = !Busy && !inventoryChecking; }
    }

    public async Task RecoverAsync(ManagerRow row)
    {
        if (Busy || Detecting || inventoryChecking) return;
        CancelSearch();
        Recovering = true; row.Busy = true;
        foreach (var manager in Managers) manager.CanRecover = false;
        foreach (var package in Packages) package.CanInstall = false;
        RecoveryMessage = $"Checking {row.Name}…"; RecoveryDetail = "";
        try
        {
            var progress = new Progress<string>(message => { if (Recovering) RecoveryMessage = message; });
            var result = await recovery.RecoverAsync(row.Definition.Kind, progress, lifetime.Token);
            row.Update(result.Status);
            RecoveryDetail = result.Status.Detail + (result.LogDirectory is null ? "" : "\n\nDetails and backups: " + result.LogDirectory);
            if (result.Success)
            {
                service.ClearSearchCache();
                inventory = await service.GetInstalledAsync(Managers.Where(x => x.Status is not null).Select(x => x.Status!).ToArray(), lifetime.Token);
                inventoryErrors = string.Join("\n", inventory.Errors); UpdateErrors();
            }
            RecoveryMessage = result.Message;
        }
        catch (OperationCanceledException) { RecoveryMessage = "Setup was interrupted. Check the manager before retrying."; }
        catch (Exception ex) { RecoveryMessage = "Setup could not be completed."; RecoveryDetail = ex.Message; }
        finally
        {
            Recovering = false; row.Busy = false;
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
        Packages.Clear(); Changed(nameof(EmptyVisibility)); Changed(nameof(ResultsVisibility)); sourceErrors = ""; UpdateErrors();
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
            if (debounce) await Task.Delay(250, token);
            if (Detecting) { SearchSummary = "Package managers are still being checked"; EmptyDetail = "Try your search again in a moment."; return; }
            var available = Managers.Where(x => x.Status?.Health == ManagerHealth.Ready && (filter is null || x.Definition.Kind == filter)).Select(x => x.Status!).ToArray();
            if (available.Length == 0) { SearchSummary = "No available sources"; EmptyDetail = "Set up the selected package manager on Overview, then refresh its status."; return; }
            var pending = available.Select(x => service.SearchAsync(x, query, token)).ToList();
            var responses = new List<SearchResult>();
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending);
                pending.Remove(completed);
                responses.Add(await completed);
                token.ThrowIfCancellationRequested();
                if (generation != searchGeneration) return;
                var existing = Packages.ToDictionary(x => x.Package.Key);
                Packages.Clear();
                foreach (var package in PackageRanker.Rank(responses.SelectMany(x => x.Packages), query))
                {
                    var row = existing.GetValueOrDefault(package.Key) ?? new PackageRow(package);
                    row.Compact = compactResults;
                    UpdateInstalledState(row);
                    Packages.Add(row);
                }
                sourceErrors = string.Join("\n", responses.Where(x => x.Error is not null).Select(x => $"{ManagerDefinition.All.Single(m => m.Kind == x.Manager).Name}: {x.Error}"));
                UpdateErrors();
                Changed(nameof(ResultsVisibility));
            }
            SearchSummary = Packages.Count == 0 ? Errors.Length > 0 ? "Search could not be completed" : $"No results for “{query}”" : "";
            EmptyDetail = Errors.Length > 0 ? "Expand the source details above." : "Try a different name or source.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (generation == searchGeneration) { sourceErrors = ex.Message; UpdateErrors(); SearchSummary = "Search needs attention"; EmptyDetail = "Check the details above and try again."; } }
        finally { if (generation == searchGeneration) { Searching = false; Changed(nameof(EmptyVisibility)); Changed(nameof(ResultsVisibility)); } }
    }

    public void CancelSearch()
    {
        ++searchGeneration; searchCancellation?.Cancel(); Searching = false;
        SearchSummary = "Search cancelled"; EmptyDetail = "Edit your search or press Enter to try again.";
    }

    public async Task InstallAsync(PackageRow row)
    {
        if (Busy || inventoryChecking || row.Installed is not null || installedThisSession.Contains(row.Package.Key)) return;
        Installing = true;
        foreach (var package in Packages) package.CanInstall = false;
        row.Action = "Installing…";
        InstallTitle = $"Installing {row.Name}";
        InstallDetail = row.Package.Manager == ManagerKind.Chocolatey ? "Approve the Windows administrator prompt, then follow the Chocolatey window. Keep TidyWindow open until it finishes." : "The package manager is working. Follow any installer or Windows permission prompt that opens. Keep TidyWindow open until it finishes.";
        try
        {
            var manager = Managers.Single(x => x.Definition.Kind == row.Package.Manager).Status!;
            var result = await service.InstallAsync(manager, row.Package, lifetime.Token);
            var success = result.ExitCode is 0 or 1641 or 3010;
            if (success) installedThisSession.Add(row.Package.Key);
            row.Action = success ? "Installed" : "Retry";
            InstallTitle = success ? result.ExitCode == 0 ? $"{row.Name} installed" : $"{row.Name} installed · restart required" : $"Couldn’t install {row.Name}";
            var output = PackageOutputParser.Clean(result.Output + "\n" + result.Error).Trim();
            InstallDetail = $"Exit code: {result.ExitCode}. " + (output.Length > 0 ? output : success ? "The package manager reported completion." : "Check the package manager’s window for details.");
            if (success)
            {
                // Reconcile other source choices for this app with the computer's actual inventory.
                inventoryChecking = true; Changed(nameof(CanRefresh));
                try
                {
                    inventory = await service.GetInstalledAsync(Managers.Where(x => x.Status is not null).Select(x => x.Status!).ToArray(), lifetime.Token);
                    inventoryErrors = string.Join("\n", inventory.Errors); UpdateErrors();
                }
                finally { inventoryChecking = false; Changed(nameof(CanRefresh)); }
            }
        }
        catch (OperationCanceledException) { InstallTitle = "Installation interrupted"; InstallDetail = "Check the package manager before retrying. Changes already made are not rolled back."; row.Action = "Retry"; }
        catch (Exception ex) { InstallTitle = $"Couldn’t install {row.Name}"; InstallDetail = ex.Message; row.Action = "Retry"; }
        finally
        {
            Installing = false;
            foreach (var package in Packages)
            {
                UpdateInstalledState(package);
            }
        }
    }

    private void UpdateInstalledState(PackageRow row)
    {
        row.SetInstalled(inventory.Find(row.Package));
        var installed = row.Installed is not null || installedThisSession.Contains(row.Package.Key);
        row.CanInstall = !Busy && !inventoryChecking && !installed;
        if (installed) row.Action = "Installed";
        else if (inventoryChecking) row.Action = "Checking…";
        else if (row.Action != "Installing…" || !Installing) row.Action = "Install";
    }

    private void UpdateErrors() => Errors = string.Join("\n", new[] { sourceErrors, inventoryErrors }.Where(x => x.Length > 0));

    public void Dispose() { lifetime.Cancel(); searchCancellation?.Cancel(); lifetime.Dispose(); searchCancellation?.Dispose(); }
}
