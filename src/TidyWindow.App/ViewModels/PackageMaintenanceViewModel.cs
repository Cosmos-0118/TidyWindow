using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
    private readonly MainViewModel _mainViewModel;
    private readonly IPrivilegeService _privilegeService;
    private readonly ActivityLogService _activityLog;

    private const int WingetCannotUpgradeExitCode = -1978334956;

    private readonly List<PackageMaintenanceItemViewModel> _allPackages = new();
    private readonly Dictionary<string, PackageMaintenanceItemViewModel> _packagesByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<PackageMaintenanceItemViewModel> _attachedItems = new();
    private readonly HashSet<PackageMaintenanceOperationViewModel> _attachedOperations = new();
    private readonly Queue<MaintenanceOperationRequest> _pendingOperations = new();
    private readonly object _operationLock = new();

    private bool _isProcessingOperations;
    private DateTimeOffset? _lastRefreshedAt;
    private bool _isDisposed;

    public PackageMaintenanceViewModel(
        PackageInventoryService inventoryService,
        PackageMaintenanceService maintenanceService,
        InstallCatalogService catalogService,
        MainViewModel mainViewModel,
        IPrivilegeService privilegeService,
        ActivityLogService activityLogService)
    {
        _inventoryService = inventoryService ?? throw new ArgumentNullException(nameof(inventoryService));
        _maintenanceService = maintenanceService ?? throw new ArgumentNullException(nameof(maintenanceService));
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        _privilegeService = privilegeService ?? throw new ArgumentNullException(nameof(privilegeService));
        _activityLog = activityLogService ?? throw new ArgumentNullException(nameof(activityLogService));

        ManagerFilters.Add(AllManagersFilter);
        Operations.CollectionChanged += OnOperationsCollectionChanged;
    }

    public ObservableCollection<PackageMaintenanceItemViewModel> Packages { get; } = new();

    public ObservableCollection<string> ManagerFilters { get; } = new();

    public ObservableCollection<string> Warnings { get; } = new();

    public ObservableCollection<PackageMaintenanceOperationViewModel> Operations { get; } = new();

    public bool HasOperations => Operations.Count > 0;

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
    private PackageMaintenanceOperationViewModel? _selectedOperation;

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

    public bool HasLoadedInitialData { get; private set; }

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
        _activityLog.LogInformation("Maintenance", "Refreshing package inventory...");

        try
        {
            var snapshot = await _inventoryService.GetInventoryAsync().ConfigureAwait(false);

            await RunOnUiThreadAsync(() =>
            {
                ApplySnapshot(snapshot);
                _lastRefreshedAt = snapshot.GeneratedAt;
                HasLoadedInitialData = true;
                OnPropertyChanged(nameof(SummaryText));
                OnPropertyChanged(nameof(LastRefreshedDisplay));
                OnPropertyChanged(nameof(HasPackages));
                _mainViewModel.SetStatusMessage($"Inventory ready • {SummaryText}");
            }).ConfigureAwait(false);

            var totalPackages = snapshot.Packages.Length;
            var updatesAvailable = snapshot.Packages.Count(static package => package.IsUpdateAvailable);
            var message = $"Inventory refreshed • {totalPackages} package(s) • {updatesAvailable} update(s).";
            _activityLog.LogSuccess("Maintenance", message, BuildInventoryDetails(snapshot));

            foreach (var warning in snapshot.Warnings)
            {
                if (!string.IsNullOrWhiteSpace(warning))
                {
                    _activityLog.LogWarning("Maintenance", warning.Trim());
                }
            }
        }
        catch (Exception ex)
        {
            await RunOnUiThreadAsync(() =>
            {
                _mainViewModel.SetStatusMessage($"Inventory refresh failed: {ex.Message}");
            }).ConfigureAwait(false);

            _activityLog.LogError("Maintenance", $"Inventory refresh failed: {ex.Message}", new[] { ex.ToString() });
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

        EnqueueMaintenanceOperation(item, MaintenanceOperationKind.Update);
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

        var enqueued = 0;
        foreach (var item in candidates)
        {
            if (EnqueueMaintenanceOperation(item, MaintenanceOperationKind.Update))
            {
                enqueued++;
            }
        }

        if (enqueued == 0)
        {
            _mainViewModel.SetStatusMessage("No maintenance updates queued.");
            return;
        }

        foreach (var package in candidates)
        {
            package.IsSelected = false;
        }

        _mainViewModel.SetStatusMessage($"Queued {enqueued} update(s).");
        QueueSelectedUpdatesCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Remove(PackageMaintenanceItemViewModel? item)
    {
        if (item is null || !item.CanRemove)
        {
            return;
        }

        EnqueueMaintenanceOperation(item, MaintenanceOperationKind.Remove);
    }

    [RelayCommand]
    private void ForceRemove(PackageMaintenanceItemViewModel? item)
    {
        if (item is null || !item.CanRemove)
        {
            return;
        }

        EnqueueMaintenanceOperation(item, MaintenanceOperationKind.ForceRemove);
    }

    [RelayCommand(CanExecute = nameof(CanRetryFailed))]
    private void RetryFailed()
    {
        var failed = Operations
            .Where(operation => operation.Status == MaintenanceOperationStatus.Failed)
            .ToList();

        if (failed.Count == 0)
        {
            _mainViewModel.SetStatusMessage("No failed maintenance operations to retry.");
            return;
        }

        var enqueued = 0;
        foreach (var operation in failed)
        {
            if (EnqueueMaintenanceOperation(operation.Item, operation.Kind))
            {
                enqueued++;
            }
        }

        _mainViewModel.SetStatusMessage(enqueued == 0
            ? "No failed maintenance operations to retry."
            : $"Retrying {enqueued} operation(s)...");
    }

    private bool CanRetryFailed()
    {
        return Operations.Any(operation => operation.Status == MaintenanceOperationStatus.Failed);
    }

    [RelayCommand(CanExecute = nameof(CanClearCompleted))]
    private void ClearCompleted()
    {
        var completed = Operations
            .Where(operation => !operation.IsPendingOrRunning)
            .ToList();

        if (completed.Count == 0)
        {
            return;
        }

        foreach (var operation in completed)
        {
            Operations.Remove(operation);
        }

        _mainViewModel.SetStatusMessage($"Cleared {completed.Count} completed operation(s).");
    }

    private bool CanClearCompleted()
    {
        return Operations.Any(operation => !operation.IsPendingOrRunning);
    }

    private bool EnqueueMaintenanceOperation(PackageMaintenanceItemViewModel item, MaintenanceOperationKind kind)
    {
        if (item is null)
        {
            return false;
        }

        if (kind == MaintenanceOperationKind.Update && !item.CanUpdate)
        {
            return false;
        }

        if (kind is MaintenanceOperationKind.Remove or MaintenanceOperationKind.ForceRemove && !item.CanRemove)
        {
            return false;
        }

        if (Operations.Any(operation => ReferenceEquals(operation.Item, item) && operation.IsPendingOrRunning))
        {
            _mainViewModel.SetStatusMessage($"'{item.DisplayName}' already has a queued task.");
            _activityLog.LogInformation("Maintenance", $"Skipped {ResolveOperationNoun(kind).ToLowerInvariant()} for '{item.DisplayName}' because a task is already queued.");
            return false;
        }

        var requiresAdmin = item.RequiresAdministrativeAccess || ManagerRequiresElevation(item.Manager);

        var packageId = ResolveMaintenancePackageId(item, kind);
        if (string.IsNullOrWhiteSpace(packageId))
        {
            var noun = ResolveOperationNoun(kind).ToLowerInvariant();
            _mainViewModel.SetStatusMessage($"Unable to queue {noun} for '{item.DisplayName}' because its identifier is unknown.");
            _activityLog.LogWarning("Maintenance", $"Unable to queue {noun} for '{item.DisplayName}' (missing identifier).", BuildOperationDetails(item, kind, packageId: null, requiresAdmin, requestedVersion: null));
            return false;
        }
        if (!EnsureElevation(item, requiresAdmin))
        {
            return false;
        }

        string? requestedVersion = null;
        if (kind == MaintenanceOperationKind.Update)
        {
            requestedVersion = string.IsNullOrWhiteSpace(item.TargetVersion) ? null : item.TargetVersion.Trim();
            if (!string.Equals(requestedVersion, item.TargetVersion, StringComparison.Ordinal))
            {
                item.TargetVersion = requestedVersion;
            }
        }

        var operation = new PackageMaintenanceOperationViewModel(item, kind);
        var queuedMessage = ResolveQueuedMessage(kind, requestedVersion);
        operation.MarkQueued(queuedMessage);

        var request = new MaintenanceOperationRequest(item, kind, packageId, requiresAdmin, operation, requestedVersion);

        bool shouldStartProcessor;

        lock (_operationLock)
        {
            _pendingOperations.Enqueue(request);
            shouldStartProcessor = !_isProcessingOperations;
            if (shouldStartProcessor)
            {
                _isProcessingOperations = true;
            }
        }

        item.IsQueued = true;
        item.IsBusy = true;
        item.QueueStatus = queuedMessage;
        item.LastOperationSucceeded = null;
        item.LastOperationMessage = queuedMessage;

        Operations.Insert(0, operation);
        SelectedOperation = operation;
        _activityLog.LogInformation("Maintenance", $"{operation.OperationDisplay} queued for '{item.DisplayName}'.", BuildOperationDetails(item, kind, packageId, requiresAdmin, requestedVersion));

        _mainViewModel.SetStatusMessage($"{operation.OperationDisplay} queued for '{item.DisplayName}'.");

        if (shouldStartProcessor)
        {
            _ = Task.Run(ProcessOperationsAsync);
        }

        return true;
    }

    private async Task ProcessOperationsAsync()
    {
        while (true)
        {
            MaintenanceOperationRequest next;

            lock (_operationLock)
            {
                if (_isDisposed || _pendingOperations.Count == 0)
                {
                    _isProcessingOperations = false;
                    return;
                }

                next = _pendingOperations.Dequeue();
            }

            await ProcessOperationAsync(next).ConfigureAwait(false);
        }
    }

    private async Task ProcessOperationAsync(MaintenanceOperationRequest request)
    {
        var item = request.Item;
        var operation = request.Operation;
        var progressMessage = ResolveProcessingMessage(request.Kind, request.TargetVersion);
        var contextDetails = BuildOperationDetails(item, request.Kind, request.PackageId, request.RequiresAdministrator, request.TargetVersion);

        await RunOnUiThreadAsync(() =>
        {
            operation.MarkStarted(progressMessage);
            item.QueueStatus = progressMessage;
        }).ConfigureAwait(false);

        _activityLog.LogInformation("Maintenance", $"{operation.OperationDisplay} started for '{item.DisplayName}'.", contextDetails);

        try
        {
            var payload = new PackageMaintenanceRequest(
                request.Item.Manager,
                request.PackageId,
                request.Item.DisplayName,
                request.RequiresAdministrator,
                request.TargetVersion);

            PackageMaintenanceResult result = request.Kind switch
            {
                MaintenanceOperationKind.Update => await _maintenanceService.UpdateAsync(payload).ConfigureAwait(false),
                MaintenanceOperationKind.ForceRemove => await _maintenanceService.ForceRemoveAsync(payload).ConfigureAwait(false),
                _ => await _maintenanceService.RemoveAsync(payload).ConfigureAwait(false)
            };

            var message = string.IsNullOrWhiteSpace(result.Summary)
                ? BuildDefaultCompletionMessage(request.Kind, result.Success)
                : result.Summary.Trim();

            var isNonActionableFailure = false;
            if (!result.Success && TryGetNonActionableMaintenanceMessage(result, item.DisplayName, out var friendlyMessage))
            {
                message = friendlyMessage;
                isNonActionableFailure = true;
            }

            await RunOnUiThreadAsync(() =>
            {
                operation.UpdateTranscript(result.Output, result.Errors);
                operation.MarkCompleted(result.Success, message);
                item.IsBusy = false;
                item.IsQueued = false;
                item.QueueStatus = message;
                item.ApplyOperationResult(result.Success, message);
            }).ConfigureAwait(false);

            await RunOnUiThreadAsync(() => _mainViewModel.SetStatusMessage(message)).ConfigureAwait(false);

            var resultDetails = BuildResultDetails(result);
            if (result.Success)
            {
                _activityLog.LogSuccess("Maintenance", $"{operation.OperationDisplay} for '{item.DisplayName}' completed: {message}", resultDetails);
            }
            else if (isNonActionableFailure)
            {
                _activityLog.LogWarning("Maintenance", $"{operation.OperationDisplay} for '{item.DisplayName}' requires manual action: {message}", resultDetails);
            }
            else
            {
                _activityLog.LogError("Maintenance", $"{operation.OperationDisplay} for '{item.DisplayName}' failed: {message}", resultDetails);
            }
        }
        catch (Exception ex)
        {
            var message = string.IsNullOrWhiteSpace(ex.Message)
                ? BuildDefaultCompletionMessage(request.Kind, success: false)
                : ex.Message.Trim();

            await RunOnUiThreadAsync(() =>
            {
                operation.UpdateTranscript(ImmutableArray<string>.Empty, ImmutableArray.Create(message));
                operation.MarkCompleted(false, message);
                item.IsBusy = false;
                item.IsQueued = false;
                item.QueueStatus = message;
                item.ApplyOperationResult(false, message);
            }).ConfigureAwait(false);

            await RunOnUiThreadAsync(() => _mainViewModel.SetStatusMessage(message)).ConfigureAwait(false);

            _activityLog.LogError("Maintenance", $"{operation.OperationDisplay} for '{item.DisplayName}' failed: {message}", new[] { ex.ToString() });
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
                newItems.Add(created);
            }
        }

        _allPackages.AddRange(newItems
            .OrderBy(item => item.Manager, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase));

        EnsureManagerFilters();
        ApplyFilters();
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
            query = query.Where(item => item.HasUpdate || !string.IsNullOrWhiteSpace(item.TargetVersion));
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

    private static IEnumerable<string>? BuildInventoryDetails(PackageInventorySnapshot snapshot)
    {
        if (snapshot.Packages.Length == 0 && snapshot.Warnings.Length == 0)
        {
            return null;
        }

        var lines = new List<string>
        {
            $"Generated at: {snapshot.GeneratedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}"
        };

        var managerGroups = snapshot.Packages
            .GroupBy(static package => string.IsNullOrWhiteSpace(package.Manager) ? "Unknown" : package.Manager, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static group => group.Count())
            .ThenBy(static group => group.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in managerGroups)
        {
            lines.Add($"Manager '{group.Key}': {group.Count()} package(s)");
        }

        var updatesByManager = snapshot.Packages
            .Where(static package => package.IsUpdateAvailable)
            .GroupBy(static package => string.IsNullOrWhiteSpace(package.Manager) ? "Unknown" : package.Manager, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static group => group.Count())
            .ThenBy(static group => group.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in updatesByManager)
        {
            lines.Add($"Updates via '{group.Key}': {group.Count()} pending");
        }

        return lines;
    }

    private static IEnumerable<string>? BuildOperationDetails(PackageMaintenanceItemViewModel item, MaintenanceOperationKind kind, string? packageId, bool requiresAdmin, string? requestedVersion)
    {
        if (item is null)
        {
            return null;
        }

        var lines = new List<string>
        {
            $"Operation: {ResolveOperationNoun(kind)}",
            $"Manager: {item.Manager}",
            $"Package identifier: {packageId ?? "(unknown)"}",
            $"Requires admin: {requiresAdmin}"
        };

        if (!string.IsNullOrWhiteSpace(requestedVersion))
        {
            lines.Add($"Requested version: {requestedVersion.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(item.InstallPackageId))
        {
            lines.Add($"Install catalog id: {item.InstallPackageId}");
        }

        if (!string.IsNullOrWhiteSpace(item.VersionDisplay))
        {
            lines.Add($"Version: {item.VersionDisplay}");
        }

        return lines;
    }

    private static IEnumerable<string>? BuildResultDetails(PackageMaintenanceResult result)
    {
        var lines = new List<string>
        {
            $"Operation: {(!string.IsNullOrWhiteSpace(result.Operation) ? result.Operation : "(unknown)")}",
            $"Manager: {(!string.IsNullOrWhiteSpace(result.Manager) ? result.Manager : "(unknown)")}",
            $"Package identifier: {(!string.IsNullOrWhiteSpace(result.PackageId) ? result.PackageId : "(unknown)")}",
            $"Attempted: {result.Attempted}",
            $"Exit code: {result.ExitCode}"
        };

        if (!string.IsNullOrWhiteSpace(result.RequestedVersion))
        {
            lines.Add($"Requested version: {result.RequestedVersion}");
        }

        if (!string.IsNullOrWhiteSpace(result.StatusBefore) || !string.IsNullOrWhiteSpace(result.StatusAfter))
        {
            lines.Add($"Status before: {result.StatusBefore ?? "(unknown)"}");
            lines.Add($"Status after: {result.StatusAfter ?? "(unknown)"}");
        }

        if (!string.IsNullOrWhiteSpace(result.InstalledVersion))
        {
            lines.Add($"Installed version: {result.InstalledVersion}");
        }

        if (!string.IsNullOrWhiteSpace(result.LatestVersion))
        {
            lines.Add($"Latest version: {result.LatestVersion}");
        }

        if (!result.Output.IsDefaultOrEmpty && result.Output.Length > 0)
        {
            lines.Add("--- Output ---");
            foreach (var line in result.Output)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    lines.Add(line);
                }
            }
        }

        if (!result.Errors.IsDefaultOrEmpty && result.Errors.Length > 0)
        {
            lines.Add("--- Errors ---");
            foreach (var line in result.Errors)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    lines.Add(line);
                }
            }
        }

        return lines;
    }

    private static bool TryGetNonActionableMaintenanceMessage(PackageMaintenanceResult result, string packageDisplayName, out string message)
    {
        message = string.Empty;

        if (!string.Equals(result.Manager, "winget", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (result.Attempted)
        {
            return false;
        }

        if (result.ExitCode != WingetCannotUpgradeExitCode && !ContainsNonActionableWingetMessage(result))
        {
            return false;
        }

        var targetVersion = string.IsNullOrWhiteSpace(result.RequestedVersion)
            ? result.LatestVersion
            : result.RequestedVersion;

        var versionText = string.IsNullOrWhiteSpace(targetVersion)
            ? "the latest available version"
            : $"version {targetVersion}";

        message = $"{packageDisplayName} cannot be updated automatically with winget. Use the publisher's installer to update to {versionText}.";
        return true;
    }

    private static bool ContainsNonActionableWingetMessage(PackageMaintenanceResult result)
    {
        static bool ContainsPhrase(IEnumerable<string> lines)
        {
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (line.IndexOf("cannot be upgraded using winget", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        if (!result.Output.IsDefaultOrEmpty && result.Output.Length > 0 && ContainsPhrase(result.Output))
        {
            return true;
        }

        if (!result.Errors.IsDefaultOrEmpty && result.Errors.Length > 0 && ContainsPhrase(result.Errors))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(result.Summary)
            && result.Summary.IndexOf("cannot be upgraded using winget", StringComparison.OrdinalIgnoreCase) >= 0;
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

    private void AttachOperation(PackageMaintenanceOperationViewModel operation)
    {
        if (operation is null)
        {
            return;
        }

        if (_attachedOperations.Add(operation))
        {
            operation.PropertyChanged += OnOperationPropertyChanged;
        }
    }

    private void DetachOperation(PackageMaintenanceOperationViewModel operation)
    {
        if (operation is null)
        {
            return;
        }

        if (_attachedOperations.Remove(operation))
        {
            operation.PropertyChanged -= OnOperationPropertyChanged;
        }
    }

    private void OnOperationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PackageMaintenanceOperationViewModel.Status))
        {
            RetryFailedCommand.NotifyCanExecuteChanged();
            ClearCompletedCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnOperationsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (PackageMaintenanceOperationViewModel operation in e.NewItems)
            {
                AttachOperation(operation);
            }
        }

        if (e.OldItems is not null)
        {
            foreach (PackageMaintenanceOperationViewModel operation in e.OldItems)
            {
                DetachOperation(operation);
            }
        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var operation in _attachedOperations.ToList())
            {
                DetachOperation(operation);
            }
        }

        OnPropertyChanged(nameof(HasOperations));

        if (Operations.Count == 0)
        {
            SelectedOperation = null;
        }
        else if (SelectedOperation is null || !Operations.Contains(SelectedOperation))
        {
            SelectedOperation = Operations[0];
        }

        RetryFailedCommand.NotifyCanExecuteChanged();
        ClearCompletedCommand.NotifyCanExecuteChanged();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PackageMaintenanceItemViewModel.IsSelected)
            || e.PropertyName == nameof(PackageMaintenanceItemViewModel.TargetVersion))
        {
            QueueSelectedUpdatesCommand.NotifyCanExecuteChanged();
        }

        if (e.PropertyName == nameof(PackageMaintenanceItemViewModel.TargetVersion))
        {
            ApplyFilters();
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
            _activityLog.LogWarning("Maintenance", $"User cancelled administrator escalation for '{item.DisplayName}'.");
            return false;
        }

        var restart = _privilegeService.Restart(PrivilegeMode.Administrator);
        if (restart.Success)
        {
            _mainViewModel.SetStatusMessage("Restarting with administrator privileges...");
            _activityLog.LogInformation("Maintenance", $"Restarting application with administrator privileges for '{item.DisplayName}'.");
            AdministratorRestartRequested?.Invoke(this, EventArgs.Empty);
            return false;
        }

        if (restart.AlreadyInTargetMode)
        {
            _mainViewModel.SetStatusMessage("Already running with administrator privileges.");
            _activityLog.LogInformation("Maintenance", "Maintenance operation already running with administrator privileges.");
            return true;
        }

        _mainViewModel.SetStatusMessage(restart.ErrorMessage ?? "Unable to restart with administrator privileges.");
        _activityLog.LogError("Maintenance", $"Failed to restart with administrator privileges for '{item.DisplayName}': {restart.ErrorMessage ?? "Unknown error"}.");
        return false;
    }

    private static string ResolveOperationNoun(MaintenanceOperationKind kind)
    {
        return kind switch
        {
            MaintenanceOperationKind.Update => "Update",
            MaintenanceOperationKind.Remove => "Removal",
            MaintenanceOperationKind.ForceRemove => "Force removal",
            _ => "Operation"
        };
    }

    private static string ResolveQueuedMessage(MaintenanceOperationKind kind, string? targetVersion)
    {
        return kind switch
        {
            MaintenanceOperationKind.Update when !string.IsNullOrWhiteSpace(targetVersion) => $"Update queued ({targetVersion.Trim()})",
            MaintenanceOperationKind.Update => "Update queued",
            MaintenanceOperationKind.Remove => "Removal queued",
            MaintenanceOperationKind.ForceRemove => "Force removal queued",
            _ => "Operation queued"
        };
    }

    private static string ResolveProcessingMessage(MaintenanceOperationKind kind, string? targetVersion)
    {
        return kind switch
        {
            MaintenanceOperationKind.Update when !string.IsNullOrWhiteSpace(targetVersion) => $"Updating ({targetVersion.Trim()})...",
            MaintenanceOperationKind.Update => "Updating...",
            MaintenanceOperationKind.Remove => "Removing...",
            MaintenanceOperationKind.ForceRemove => "Force removing...",
            _ => "Processing..."
        };
    }

    private static string BuildDefaultCompletionMessage(MaintenanceOperationKind kind, bool success)
    {
        var noun = ResolveOperationNoun(kind);
        return success ? $"{noun} completed." : $"{noun} failed.";
    }

    private static string? ResolveMaintenancePackageId(PackageMaintenanceItemViewModel item, MaintenanceOperationKind kind)
    {
        if (item is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(item.PackageIdentifier))
        {
            return item.PackageIdentifier;
        }

        return string.IsNullOrWhiteSpace(item.InstallPackageId) ? null : item.InstallPackageId;
    }

    private sealed record MaintenanceOperationRequest(
        PackageMaintenanceItemViewModel Item,
        MaintenanceOperationKind Kind,
        string PackageId,
        bool RequiresAdministrator,
        PackageMaintenanceOperationViewModel Operation,
        string? TargetVersion);

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
        Operations.CollectionChanged -= OnOperationsCollectionChanged;

        foreach (var item in _attachedItems.ToList())
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }

        _attachedItems.Clear();

        foreach (var operation in _attachedOperations.ToList())
        {
            operation.PropertyChanged -= OnOperationPropertyChanged;
        }

        _attachedOperations.Clear();
    }
}

public enum MaintenanceOperationKind
{
    Update,
    Remove,
    ForceRemove
}

public enum MaintenanceOperationStatus
{
    Pending,
    Running,
    Succeeded,
    Failed
}

public sealed partial class PackageMaintenanceOperationViewModel : ObservableObject
{
    public PackageMaintenanceOperationViewModel(PackageMaintenanceItemViewModel item, MaintenanceOperationKind kind)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        Kind = kind;
        Id = Guid.NewGuid();
        MarkQueued("Queued");
    }

    public Guid Id { get; }

    public PackageMaintenanceItemViewModel Item { get; }

    public MaintenanceOperationKind Kind { get; }

    public string OperationDisplay => Kind switch
    {
        MaintenanceOperationKind.Update => "Update",
        MaintenanceOperationKind.Remove => "Removal",
        MaintenanceOperationKind.ForceRemove => "Force removal",
        _ => "Operation"
    };

    public string PackageDisplay => Item.DisplayName;

    public string StatusDisplay => Status switch
    {
        MaintenanceOperationStatus.Pending => "Queued",
        MaintenanceOperationStatus.Running => "Running",
        MaintenanceOperationStatus.Succeeded => "Completed",
        MaintenanceOperationStatus.Failed => "Failed",
        _ => Status.ToString()
    };

    public bool IsPendingOrRunning => Status is MaintenanceOperationStatus.Pending or MaintenanceOperationStatus.Running;

    public bool IsActive => IsPendingOrRunning;

    public bool HasErrors => !Errors.IsDefaultOrEmpty && Errors.Length > 0;

    public IReadOnlyList<string> DisplayLines => HasErrors ? Errors : Output;

    [ObservableProperty]
    private MaintenanceOperationStatus _status;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private DateTimeOffset _enqueuedAt;

    [ObservableProperty]
    private DateTimeOffset? _startedAt;

    [ObservableProperty]
    private DateTimeOffset? _completedAt;

    [ObservableProperty]
    private ImmutableArray<string> _output = ImmutableArray<string>.Empty;

    [ObservableProperty]
    private ImmutableArray<string> _errors = ImmutableArray<string>.Empty;

    public void MarkQueued(string message)
    {
        Status = MaintenanceOperationStatus.Pending;
        Message = message;
        EnqueuedAt = DateTimeOffset.UtcNow;
        StartedAt = null;
        CompletedAt = null;
        UpdateTranscript(ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);
    }

    public void MarkStarted(string message)
    {
        Status = MaintenanceOperationStatus.Running;
        Message = message;
        StartedAt = DateTimeOffset.UtcNow;
    }

    public void MarkCompleted(bool success, string message)
    {
        Status = success ? MaintenanceOperationStatus.Succeeded : MaintenanceOperationStatus.Failed;
        Message = message;
        CompletedAt = DateTimeOffset.UtcNow;
    }

    public void UpdateTranscript(ImmutableArray<string> output, ImmutableArray<string> errors)
    {
        Output = output.IsDefault ? ImmutableArray<string>.Empty : output;
        Errors = errors.IsDefault ? ImmutableArray<string>.Empty : errors;
    }

    partial void OnStatusChanged(MaintenanceOperationStatus oldValue, MaintenanceOperationStatus newValue)
    {
        OnPropertyChanged(nameof(IsPendingOrRunning));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(StatusDisplay));
    }

    partial void OnOutputChanged(ImmutableArray<string> oldValue, ImmutableArray<string> newValue)
    {
        OnPropertyChanged(nameof(DisplayLines));
    }

    partial void OnErrorsChanged(ImmutableArray<string> oldValue, ImmutableArray<string> newValue)
    {
        OnPropertyChanged(nameof(DisplayLines));
        OnPropertyChanged(nameof(HasErrors));
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

    public bool CanUpdate => (HasUpdate || !string.IsNullOrWhiteSpace(TargetVersion))
                             && (!string.IsNullOrWhiteSpace(InstallPackageId) || !string.IsNullOrWhiteSpace(PackageIdentifier));

    public bool CanRemove => !string.IsNullOrWhiteSpace(Manager) && !string.IsNullOrWhiteSpace(PackageIdentifier);

    public bool CanForceRemove => CanRemove;

    public string VersionDisplay => HasUpdate && !string.IsNullOrWhiteSpace(AvailableVersion)
        ? $"{InstalledVersion} → {AvailableVersion}"
        : InstalledVersion;

    [ObservableProperty]
    private string? _targetVersion;

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

    partial void OnTargetVersionChanged(string? oldValue, string? newValue)
    {
        var normalized = string.IsNullOrWhiteSpace(newValue) ? null : newValue.Trim();
        if (!string.Equals(newValue, normalized, StringComparison.Ordinal))
        {
            TargetVersion = normalized;
            return;
        }

        OnPropertyChanged(nameof(CanUpdate));
    }

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

    public void ApplyOperationResult(bool success, string message)
    {
        LastOperationSucceeded = success;
        LastOperationMessage = string.IsNullOrWhiteSpace(message) ? (success ? "Operation completed." : "Operation failed.") : message.Trim();
    }
}
