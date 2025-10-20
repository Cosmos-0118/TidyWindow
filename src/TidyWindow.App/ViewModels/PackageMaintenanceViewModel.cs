using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.Services;
using TidyWindow.Core.Install;
using TidyWindow.Core.Maintenance;

namespace TidyWindow.App.ViewModels;

public sealed partial class PackageMaintenanceViewModel : ViewModelBase, IDisposable
{
    private const string AllManagersFilter = "All managers";

    private readonly PackageInventoryService _inventoryService;
    private readonly PackageMaintenanceService _maintenanceService;
    private readonly InstallCatalogService _catalogService;
    private readonly InstallQueue _installQueue;
    private readonly MainViewModel _mainViewModel;
    private readonly IPrivilegeService _privilegeService;

    private readonly List<PackageMaintenanceItemViewModel> _allPackages = new();
    private readonly Dictionary<string, PackageMaintenanceItemViewModel> _packagesByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PackageMaintenanceItemViewModel> _packagesByInstallId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, InstallQueueOperationSnapshot> _queueSnapshots = new();
    private readonly HashSet<PackageMaintenanceItemViewModel> _attachedItems = new();

    private DateTimeOffset? _lastRefreshedAt;
    private bool _isDisposed;

    public PackageMaintenanceViewModel(
        PackageInventoryService inventoryService,
        PackageMaintenanceService maintenanceService,
        InstallCatalogService catalogService,
        InstallQueue installQueue,
        MainViewModel mainViewModel,
        IPrivilegeService privilegeService)
    {
        _inventoryService = inventoryService ?? throw new ArgumentNullException(nameof(inventoryService));
        _maintenanceService = maintenanceService ?? throw new ArgumentNullException(nameof(maintenanceService));
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _installQueue = installQueue ?? throw new ArgumentNullException(nameof(installQueue));
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        _privilegeService = privilegeService ?? throw new ArgumentNullException(nameof(privilegeService));

        ManagerFilters.Add(AllManagersFilter);

        foreach (var snapshot in _installQueue.GetSnapshot())
        {
            _queueSnapshots[snapshot.Id] = snapshot;
        }

        _installQueue.OperationChanged += OnInstallQueueChanged;
    }

    public ObservableCollection<PackageMaintenanceItemViewModel> Packages { get; } = new();

    public ObservableCollection<string> ManagerFilters { get; } = new();

    public ObservableCollection<string> Warnings { get; } = new();

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _searchText;

    [ObservableProperty]
    private string? _selectedManager = AllManagersFilter;

    [ObservableProperty]
    private bool _updatesOnly;

    [ObservableProperty]
    private PackageMaintenanceItemViewModel? _selectedPackage;

    [ObservableProperty]
    private string _headline = "Maintain installed packages";

    public string SummaryText
    {
        get
        {
            var total = _allPackages.Count;
            var updates = _allPackages.Count(item => item.HasUpdate);
            return total == 0
                ? "No installed packages detected yet."
                : updates == 1
                    ? $"{total} packages detected • 1 update available"
                    : $"{total} packages detected • {updates} updates available";
        }
    }

    public string LastRefreshedDisplay => _lastRefreshedAt is null
        ? "Inventory has not been refreshed yet."
        : $"Inventory updated {FormatRelativeTime(_lastRefreshedAt.Value)}";

    public bool HasPackages => Packages.Count > 0;

    public Func<string, bool>? ConfirmElevation { get; set; }

    public event EventHandler? AdministratorRestartRequested;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        _mainViewModel.SetStatusMessage("Refreshing package inventory...");

        try
        {
            var snapshot = await _inventoryService.GetInventoryAsync().ConfigureAwait(false);

            await RunOnUiThreadAsync(() =>
            {
                ApplySnapshot(snapshot);
                _lastRefreshedAt = snapshot.GeneratedAt;
                OnPropertyChanged(nameof(SummaryText));
                OnPropertyChanged(nameof(LastRefreshedDisplay));
                OnPropertyChanged(nameof(HasPackages));
                _mainViewModel.SetStatusMessage($"Inventory ready • {SummaryText}");
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await RunOnUiThreadAsync(() =>
            {
                _mainViewModel.SetStatusMessage($"Inventory refresh failed: {ex.Message}");
            }).ConfigureAwait(false);
        }
        finally
        {
            await RunOnUiThreadAsync(() => IsBusy = false).ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private void QueueUpdate(PackageMaintenanceItemViewModel? item)
    {
        if (item is null || !item.CanUpdate)
        {
            return;
        }

        var requiresAdmin = item.RequiresAdministrativeAccess || ManagerRequiresElevation(item.Manager);
        if (!EnsureElevation(item, requiresAdmin))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(item.InstallPackageId) || !_catalogService.TryGetPackage(item.InstallPackageId, out var definition))
        {
            _mainViewModel.SetStatusMessage($"Catalog entry missing for {item.DisplayName}. Cannot queue update.");
            return;
        }

        var snapshot = _installQueue.Enqueue(definition);
        ApplyQueueSnapshot(snapshot);
        _mainViewModel.SetStatusMessage($"Queued update for {item.DisplayName}.");
    }

    [RelayCommand(CanExecute = nameof(CanQueueSelectedUpdates))]
    private void QueueSelectedUpdates()
    {
        var candidates = Packages
            .Where(package => package.IsSelected && package.CanUpdate)
            .ToList();

        if (candidates.Count == 0)
        {
            return;
        }

        var toQueue = new List<InstallPackageDefinition>();
        foreach (var item in candidates)
        {
            if (string.IsNullOrWhiteSpace(item.InstallPackageId))
            {
                continue;
            }

            if (_catalogService.TryGetPackage(item.InstallPackageId, out var definition))
            {
                var requiresAdmin = item.RequiresAdministrativeAccess || ManagerRequiresElevation(item.Manager);
                if (!EnsureElevation(item, requiresAdmin))
                {
                    continue;
                }

                toQueue.Add(definition);
            }
        }

        if (toQueue.Count == 0)
        {
            _mainViewModel.SetStatusMessage("No catalog-managed selections ready to queue.");
            return;
        }

        var snapshots = _installQueue.EnqueueRange(toQueue);
        foreach (var snapshot in snapshots)
        {
            ApplyQueueSnapshot(snapshot);
        }

        foreach (var package in candidates)
        {
            package.IsSelected = false;
        }

        _mainViewModel.SetStatusMessage($"Queued {snapshots.Count} update(s).");
        QueueSelectedUpdatesCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task RemoveAsync(PackageMaintenanceItemViewModel? item)
    {
        if (item is null || !item.CanRemove)
        {
            return;
        }

        var requiresAdmin = item.RequiresAdministrativeAccess || ManagerRequiresElevation(item.Manager);
        if (!EnsureElevation(item, requiresAdmin))
        {
            return;
        }

        try
        {
            item.IsBusy = true;
            _mainViewModel.SetStatusMessage($"Removing {item.DisplayName}...");

            var request = new PackageMaintenanceRequest(item.Manager, item.PackageIdentifier, item.DisplayName, requiresAdmin);
            var result = await _maintenanceService.RemoveAsync(request).ConfigureAwait(false);

            await RunOnUiThreadAsync(() =>
            {
                item.ApplyOperationResult(result.Success, result.Summary);
                _mainViewModel.SetStatusMessage(result.Summary);
            }).ConfigureAwait(false);

            if (result.Success)
            {
                await RefreshAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            await RunOnUiThreadAsync(() =>
            {
                item.ApplyOperationResult(false, ex.Message);
                _mainViewModel.SetStatusMessage($"Removal failed: {ex.Message}");
            }).ConfigureAwait(false);
        }
        finally
        {
            await RunOnUiThreadAsync(() => item.IsBusy = false).ConfigureAwait(false);
        }
    }

    private bool CanQueueSelectedUpdates()
    {
        return Packages.Any(package => package.IsSelected && package.CanUpdate);
    }

    partial void OnSearchTextChanged(string? oldValue, string? newValue)
    {
        ApplyFilters();
    }

    partial void OnSelectedManagerChanged(string? oldValue, string? newValue)
    {
        ApplyFilters();
    }

    partial void OnUpdatesOnlyChanged(bool oldValue, bool newValue)
    {
        ApplyFilters();
    }

    private void ApplySnapshot(PackageInventorySnapshot snapshot)
    {
        _allPackages.Clear();
        _packagesByKey.Clear();
        _packagesByInstallId.Clear();

        Warnings.Clear();
        foreach (var warning in snapshot.Warnings)
        {
            if (!string.IsNullOrWhiteSpace(warning))
            {
                Warnings.Add(warning.Trim());
            }
        }

        var newItems = new List<PackageMaintenanceItemViewModel>();
        foreach (var package in snapshot.Packages)
        {
            var key = BuildKey(package.Manager, package.PackageIdentifier);
            if (_packagesByKey.TryGetValue(key, out var existing))
            {
                existing.UpdateFrom(package);
                newItems.Add(existing);
            }
            else
            {
                var created = new PackageMaintenanceItemViewModel(package);
                _packagesByKey[key] = created;
                if (!string.IsNullOrWhiteSpace(created.InstallPackageId))
                {
                    _packagesByInstallId[created.InstallPackageId] = created;
                }

                newItems.Add(created);
            }
        }

        _allPackages.AddRange(newItems
            .OrderBy(item => item.Manager, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase));

        EnsureManagerFilters();
        ApplyFilters();
        ApplyExistingQueueSnapshots();
    }

    private void ApplyExistingQueueSnapshots()
    {
        foreach (var snapshot in _queueSnapshots.Values)
        {
            ApplyQueueSnapshot(snapshot);
        }
    }

    private void ApplyQueueSnapshot(InstallQueueOperationSnapshot snapshot)
    {
        _queueSnapshots[snapshot.Id] = snapshot;

        if (string.IsNullOrWhiteSpace(snapshot.Package.Id))
        {
            return;
        }

        if (_packagesByInstallId.TryGetValue(snapshot.Package.Id, out var item))
        {
            item.UpdateQueueSnapshot(snapshot);
        }
    }

    private void ApplyFilters()
    {
        IEnumerable<PackageMaintenanceItemViewModel> query = _allPackages;

        if (!string.IsNullOrWhiteSpace(SelectedManager) && !string.Equals(SelectedManager, AllManagersFilter, StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(item => string.Equals(item.Manager, SelectedManager, StringComparison.OrdinalIgnoreCase));
        }

        if (UpdatesOnly)
        {
            query = query.Where(item => item.HasUpdate);
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            query = query.Where(item => item.Matches(SearchText));
        }

        var ordered = query
            .OrderBy(item => item.Manager, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        SynchronizeCollection(Packages, ordered);
        OnPropertyChanged(nameof(HasPackages));
        OnPropertyChanged(nameof(SummaryText));
        QueueSelectedUpdatesCommand.NotifyCanExecuteChanged();
    }

    private void EnsureManagerFilters()
    {
        var selected = SelectedManager;
        ManagerFilters.Clear();
        ManagerFilters.Add(AllManagersFilter);

        foreach (var manager in _allPackages
                     .Select(item => item.Manager)
                     .Where(manager => !string.IsNullOrWhiteSpace(manager))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(static manager => manager, StringComparer.OrdinalIgnoreCase))
        {
            ManagerFilters.Add(manager);
        }

        if (string.IsNullOrWhiteSpace(selected) || !ManagerFilters.Contains(selected))
        {
            SelectedManager = AllManagersFilter;
        }
        else
        {
            SelectedManager = selected;
        }
    }

    private void SynchronizeCollection(ObservableCollection<PackageMaintenanceItemViewModel> target, IList<PackageMaintenanceItemViewModel> source)
    {
        for (var index = target.Count - 1; index >= 0; index--)
        {
            var item = target[index];
            if (!source.Contains(item))
            {
                DetachItem(item);
                target.RemoveAt(index);
            }
        }

        for (var index = 0; index < source.Count; index++)
        {
            var item = source[index];
            if (index < target.Count)
            {
                if (!ReferenceEquals(target[index], item))
                {
                    if (target.Contains(item))
                    {
                        var currentIndex = target.IndexOf(item);
                        target.Move(currentIndex, index);
                    }
                    else
                    {
                        target.Insert(index, item);
                        AttachItem(item);
                    }
                }
            }
            else
            {
                target.Add(item);
                AttachItem(item);
            }
        }
    }

    private void AttachItem(PackageMaintenanceItemViewModel item)
    {
        if (_attachedItems.Add(item))
        {
            item.PropertyChanged += OnItemPropertyChanged;
        }
    }

    private void DetachItem(PackageMaintenanceItemViewModel item)
    {
        if (_attachedItems.Remove(item))
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PackageMaintenanceItemViewModel.IsSelected))
        {
            QueueSelectedUpdatesCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnInstallQueueChanged(object? sender, InstallQueueChangedEventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(() => ApplyQueueSnapshot(e.Snapshot));
        }
        else
        {
            ApplyQueueSnapshot(e.Snapshot);
        }
    }

    private bool EnsureElevation(PackageMaintenanceItemViewModel item, bool requiresAdmin)
    {
        if (!requiresAdmin)
        {
            return true;
        }

        if (_privilegeService.CurrentMode == PrivilegeMode.Administrator)
        {
            return true;
        }

        var prompt = $"'{item.DisplayName}' requires administrator privileges. Restart as administrator?";
        if (ConfirmElevation is not null && !ConfirmElevation.Invoke(prompt))
        {
            _mainViewModel.SetStatusMessage("Operation cancelled. Administrator privileges required.");
            return false;
        }

        var restart = _privilegeService.Restart(PrivilegeMode.Administrator);
        if (restart.Success)
        {
            _mainViewModel.SetStatusMessage("Restarting with administrator privileges...");
            AdministratorRestartRequested?.Invoke(this, EventArgs.Empty);
            return false;
        }

        if (restart.AlreadyInTargetMode)
        {
            _mainViewModel.SetStatusMessage("Already running with administrator privileges.");
            return true;
        }

        _mainViewModel.SetStatusMessage(restart.ErrorMessage ?? "Unable to restart with administrator privileges.");
        return false;
    }

    private static bool ManagerRequiresElevation(string manager)
    {
        return manager.Equals("winget", StringComparison.OrdinalIgnoreCase)
               || manager.Equals("choco", StringComparison.OrdinalIgnoreCase)
               || manager.Equals("chocolatey", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildKey(string manager, string identifier)
    {
        return manager.Trim().ToLowerInvariant() + "|" + identifier.Trim();
    }

    private static string FormatRelativeTime(DateTimeOffset timestamp)
    {
        var delta = DateTimeOffset.UtcNow - timestamp.ToUniversalTime();
        if (delta < TimeSpan.FromSeconds(60))
        {
            return "just now";
        }

        if (delta < TimeSpan.FromMinutes(60))
        {
            var minutes = Math.Max(1, (int)Math.Round(delta.TotalMinutes));
            return minutes == 1 ? "1 minute ago" : $"{minutes} minutes ago";
        }

        if (delta < TimeSpan.FromHours(24))
        {
            var hours = Math.Max(1, (int)Math.Round(delta.TotalHours));
            return hours == 1 ? "1 hour ago" : $"{hours} hours ago";
        }

        var days = Math.Max(1, (int)Math.Round(delta.TotalDays));
        return days == 1 ? "1 day ago" : $"{days} days ago";
    }

    private static Task RunOnUiThreadAsync(Action action)
    {
        if (action is null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            action();
            return Task.CompletedTask;
        }

        if (dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action, DispatcherPriority.Normal).Task;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _installQueue.OperationChanged -= OnInstallQueueChanged;

        foreach (var item in _attachedItems.ToList())
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }

        _attachedItems.Clear();
    }
}

public sealed partial class PackageMaintenanceItemViewModel : ObservableObject
{
    private static readonly string[] _wingetAliases = { "winget" };
    private static readonly string[] _chocoAliases = { "choco", "chocolatey" };

    public PackageMaintenanceItemViewModel(PackageInventoryItem item)
    {
        UpdateFrom(item);
        ManagerDisplay = Manager switch
        {
            var value when _wingetAliases.Contains(value, StringComparer.OrdinalIgnoreCase) => "winget",
            var value when _chocoAliases.Contains(value, StringComparer.OrdinalIgnoreCase) => "Chocolatey",
            "scoop" => "Scoop",
            _ => string.IsNullOrWhiteSpace(Manager) ? "Unknown" : Manager
        };

        if (item.Catalog is not null)
        {
            InstallPackageId = item.Catalog.InstallPackageId;
            Summary = string.IsNullOrWhiteSpace(item.Catalog.Summary) ? null : item.Catalog.Summary.Trim();
            Homepage = string.IsNullOrWhiteSpace(item.Catalog.Homepage) ? null : item.Catalog.Homepage.Trim();
            Tags = item.Catalog.Tags;
            RequiresAdministrativeAccess = item.Catalog.RequiresAdmin;
        }

        TagsDisplay = Tags.IsDefaultOrEmpty ? string.Empty : string.Join(" • ", Tags);
    }

    public string Manager { get; private set; } = string.Empty;

    public string ManagerDisplay { get; }

    public string PackageIdentifier { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public string InstalledVersion { get; private set; } = "Unknown";

    public string? AvailableVersion { get; private set; }

    public bool HasUpdate { get; private set; }

    public string Source { get; private set; } = string.Empty;

    public string? Summary { get; private set; }

    public string? Homepage { get; private set; }

    public ImmutableArray<string> Tags { get; private set; } = ImmutableArray<string>.Empty;

    public string TagsDisplay { get; private set; } = string.Empty;

    public string? InstallPackageId { get; private set; }

    public bool RequiresAdministrativeAccess { get; private set; }

    public bool CanUpdate => HasUpdate && !string.IsNullOrWhiteSpace(InstallPackageId);

    public bool CanRemove => !string.IsNullOrWhiteSpace(Manager) && !string.IsNullOrWhiteSpace(PackageIdentifier);

    public string VersionDisplay => HasUpdate && !string.IsNullOrWhiteSpace(AvailableVersion)
        ? $"{InstalledVersion} → {AvailableVersion}"
        : InstalledVersion;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isQueued;

    [ObservableProperty]
    private string? _queueStatus;

    [ObservableProperty]
    private string? _lastOperationMessage;

    [ObservableProperty]
    private bool? _lastOperationSucceeded;

    public void UpdateFrom(PackageInventoryItem item)
    {
        Manager = item.Manager ?? string.Empty;
        PackageIdentifier = item.PackageIdentifier ?? string.Empty;
        DisplayName = string.IsNullOrWhiteSpace(item.Name) ? PackageIdentifier : item.Name.Trim();
        InstalledVersion = string.IsNullOrWhiteSpace(item.InstalledVersion) ? "Unknown" : item.InstalledVersion.Trim();
        AvailableVersion = string.IsNullOrWhiteSpace(item.AvailableVersion) ? null : item.AvailableVersion.Trim();
        Source = string.IsNullOrWhiteSpace(item.Source) ? string.Empty : item.Source.Trim();
        HasUpdate = item.IsUpdateAvailable;
    }

    public bool Matches(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        var comparison = StringComparison.OrdinalIgnoreCase;
        return DisplayName.Contains(filter, comparison)
               || PackageIdentifier.Contains(filter, comparison)
               || Manager.Contains(filter, comparison)
               || (!string.IsNullOrWhiteSpace(Source) && Source.Contains(filter, comparison))
               || (!string.IsNullOrWhiteSpace(TagsDisplay) && TagsDisplay.Contains(filter, comparison));
    }

    public void UpdateQueueSnapshot(InstallQueueOperationSnapshot snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        IsQueued = snapshot.IsActive || snapshot.Status == InstallQueueStatus.Pending;
        QueueStatus = snapshot.LastMessage;

        LastOperationSucceeded = snapshot.Status switch
        {
            InstallQueueStatus.Succeeded => true,
            InstallQueueStatus.Failed => false,
            InstallQueueStatus.Cancelled => false,
            _ => LastOperationSucceeded
        };
    }

    public void ApplyOperationResult(bool success, string message)
    {
        LastOperationSucceeded = success;
        LastOperationMessage = string.IsNullOrWhiteSpace(message) ? (success ? "Operation completed." : "Operation failed.") : message.Trim();
    }
}
