using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.Services;
using TidyWindow.Core.Install;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using WpfApplication = System.Windows.Application;

namespace TidyWindow.App.ViewModels;

public sealed partial class InstallHubViewModel : ViewModelBase, IDisposable
{
    private readonly InstallCatalogService _catalogService;
    private readonly InstallQueue _installQueue;
    private readonly BundlePresetService _presetService;
    private readonly MainViewModel _mainViewModel;
    private readonly ActivityLogService _activityLog;
    private readonly Dictionary<string, InstallPackageItemViewModel> _packageLookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, InstallOperationItemViewModel> _operationLookup = new();
    private readonly Dictionary<Guid, InstallQueueOperationSnapshot> _snapshotCache = new();
    private readonly Dictionary<string, int> _activePackageCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _loadSemaphore = new(1, 1);
    private bool _isDisposed;
    private Task? _initializationTask;
    private bool _catalogInitialized;
    private bool _suppressFilters;

    public InstallHubViewModel(InstallCatalogService catalogService, InstallQueue installQueue, BundlePresetService presetService, MainViewModel mainViewModel, ActivityLogService activityLogService)
    {
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _installQueue = installQueue ?? throw new ArgumentNullException(nameof(installQueue));
        _presetService = presetService ?? throw new ArgumentNullException(nameof(presetService));
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        _activityLog = activityLogService ?? throw new ArgumentNullException(nameof(activityLogService));

        Bundles = new ObservableCollection<InstallBundleItemViewModel>();
        Packages = new ObservableCollection<InstallPackageItemViewModel>();
        Operations = new ObservableCollection<InstallOperationItemViewModel>();

        foreach (var snapshot in _installQueue.GetSnapshot())
        {
            _snapshotCache[snapshot.Id] = snapshot;
            if (snapshot.IsActive)
            {
                IncrementActive(snapshot.Package.Id);
            }

            var operationVm = new InstallOperationItemViewModel(snapshot);
            _operationLookup[snapshot.Id] = operationVm;
            Operations.Add(operationVm);

            if (snapshot.Status != InstallQueueStatus.Pending)
            {
                LogSnapshotChange(snapshot, null);
            }
        }

        _installQueue.OperationChanged += OnInstallQueueChanged;

        UpdatePackageQueueStates();
    }

    public ObservableCollection<InstallBundleItemViewModel> Bundles { get; }

    public ObservableCollection<InstallPackageItemViewModel> Packages { get; }

    public ObservableCollection<InstallOperationItemViewModel> Operations { get; }

    [ObservableProperty]
    private InstallBundleItemViewModel? _selectedBundle;

    [ObservableProperty]
    private string? _searchText;

    [ObservableProperty]
    private bool _hasActiveOperations;

    [ObservableProperty]
    private string _headline = "Curate developer bundles";

    [ObservableProperty]
    private bool _isLoading;

    public Task EnsureLoadedAsync()
    {
        if (_catalogInitialized)
        {
            return Task.CompletedTask;
        }

        return _initializationTask ??= LoadCatalogAsync();
    }

    public bool IsInitialized => _catalogInitialized;

    private async Task LoadCatalogAsync()
    {
        var success = false;
        await _loadSemaphore.WaitAsync();

        try
        {
            if (_catalogInitialized)
            {
                success = true;
                return;
            }

            IsLoading = true;

            IReadOnlyList<InstallPackageDefinition> packages = Array.Empty<InstallPackageDefinition>();
            IReadOnlyList<InstallBundleDefinition> bundles = Array.Empty<InstallBundleDefinition>();
            Exception? failure = null;

            await Task.Run(() =>
            {
                try
                {
                    packages = _catalogService.Packages;
                    bundles = _catalogService.Bundles;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });

            if (failure is not null)
            {
                _mainViewModel.SetStatusMessage($"Install catalog failed to load: {failure.Message}");
                return;
            }

            if (WpfApplication.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            {
                await dispatcher.InvokeAsync(() => ApplyCatalog(packages, bundles));
            }
            else
            {
                ApplyCatalog(packages, bundles);
            }

            _catalogInitialized = true;
            success = true;
        }
        finally
        {
            if (!success)
            {
                _initializationTask = null;
            }

            IsLoading = false;
            _loadSemaphore.Release();
        }

        UpdatePackageQueueStates();
    }

    private void ApplyCatalog(IReadOnlyList<InstallPackageDefinition> packages, IReadOnlyList<InstallBundleDefinition> bundles)
    {
        _suppressFilters = true;

        try
        {
            Bundles.Clear();
            Packages.Clear();
            _packageLookup.Clear();

            var allBundle = InstallBundleItemViewModel.CreateAll(packages.Count);
            Bundles.Add(allBundle);

            foreach (var package in packages)
            {
                var vm = new InstallPackageItemViewModel(package);
                _packageLookup[package.Id] = vm;
            }

            foreach (var bundle in bundles)
            {
                Bundles.Add(new InstallBundleItemViewModel(
                    bundle.Id,
                    bundle.Name,
                    bundle.Description,
                    bundle.PackageIds));
            }

            SelectedBundle = Bundles.FirstOrDefault();
        }
        finally
        {
            _suppressFilters = false;
        }

        ApplyBundleFilter();
    }

    partial void OnSelectedBundleChanged(InstallBundleItemViewModel? oldValue, InstallBundleItemViewModel? newValue)
    {
        if (_suppressFilters)
        {
            return;
        }

        ApplyBundleFilter();
    }

    partial void OnSearchTextChanged(string? oldValue, string? newValue)
    {
        if (_suppressFilters)
        {
            return;
        }

        ApplyBundleFilter();
    }

    [RelayCommand]
    private void QueuePackage(InstallPackageItemViewModel? package)
    {
        if (package is null)
        {
            return;
        }

        var snapshot = _installQueue.Enqueue(package.Definition);
        _mainViewModel.SetStatusMessage($"Queued install for {package.Definition.Name}.");
        _activityLog.LogInformation("Install hub", $"Queued install for {package.Definition.Name}.");
        _snapshotCache[snapshot.Id] = snapshot;
        UpdateActiveCount(snapshot);
        UpdatePackageQueueStates();
    }

    [RelayCommand]
    private void QueueBundle(InstallBundleItemViewModel? bundle)
    {
        if (bundle is null)
        {
            return;
        }

        var packages = bundle.IsSyntheticAll
            ? _catalogService.Packages
            : _catalogService.GetPackagesForBundle(bundle.Id);

        if (packages.Count == 0)
        {
            _mainViewModel.SetStatusMessage($"Bundle '{bundle.Name}' has no packages yet.");
            return;
        }

        var snapshots = _installQueue.EnqueueRange(packages);
        _mainViewModel.SetStatusMessage($"Queued {snapshots.Count} install(s) from '{bundle.Name}'.");
        _activityLog.LogInformation("Install hub", $"Queued {snapshots.Count} install(s) from '{bundle.Name}'.");

        foreach (var snapshot in snapshots)
        {
            _snapshotCache[snapshot.Id] = snapshot;
            UpdateActiveCount(snapshot);
        }

        UpdatePackageQueueStates();
    }

    [RelayCommand]
    private void QueueSelection()
    {
        var selected = Packages.Where(p => p.IsSelected).Select(p => p.Definition).ToList();
        if (selected.Count == 0)
        {
            _mainViewModel.SetStatusMessage("Select packages to queue.");
            return;
        }

        var snapshots = _installQueue.EnqueueRange(selected);
        _mainViewModel.SetStatusMessage($"Queued {snapshots.Count} selected install(s).");
        _activityLog.LogInformation("Install hub", $"Queued {snapshots.Count} selected install(s).");

        foreach (var vm in Packages)
        {
            vm.IsSelected = false;
        }

        foreach (var snapshot in snapshots)
        {
            _snapshotCache[snapshot.Id] = snapshot;
            UpdateActiveCount(snapshot);
        }

        UpdatePackageQueueStates();
    }

    [RelayCommand]
    private void ClearCompleted()
    {
        var removed = _installQueue.ClearCompleted();
        if (removed.Count == 0)
        {
            return;
        }

        foreach (var snapshot in removed)
        {
            _snapshotCache.Remove(snapshot.Id);
            _operationLookup.Remove(snapshot.Id);
            var item = Operations.FirstOrDefault(op => op.Id == snapshot.Id);
            if (item is not null)
            {
                Operations.Remove(item);
            }
        }

        _activityLog.LogInformation("Install hub", $"Cleared {removed.Count} completed operation(s).");

        UpdatePackageQueueStates();
    }

    [RelayCommand]
    private void RetryFailed()
    {
        var snapshots = _installQueue.RetryFailed();
        if (snapshots.Count == 0)
        {
            _mainViewModel.SetStatusMessage("No failed installs to retry.");
            return;
        }

        _mainViewModel.SetStatusMessage($"Retrying {snapshots.Count} install(s).");
        _activityLog.LogInformation("Install hub", $"Retrying {snapshots.Count} install(s).");

        foreach (var snapshot in snapshots)
        {
            _snapshotCache[snapshot.Id] = snapshot;
        }

        UpdatePackageQueueStates();
    }

    [RelayCommand]
    private void CancelOperation(InstallOperationItemViewModel? operation)
    {
        if (operation is null)
        {
            return;
        }

        var snapshot = _installQueue.Cancel(operation.Id);
        if (snapshot is null)
        {
            return;
        }

        _mainViewModel.SetStatusMessage($"Cancellation requested for {snapshot.Package.Name}.");
        _activityLog.LogWarning("Install hub", $"Cancellation requested for {snapshot.Package.Name}.");
        _snapshotCache[snapshot.Id] = snapshot;
        UpdateActiveCount(snapshot);
        UpdatePackageQueueStates();
    }

    [RelayCommand]
    private async Task ExportSelectionAsync()
    {
        var selected = Packages.Where(p => p.IsSelected).Select(p => p.Definition.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        if (selected.Length == 0)
        {
            _mainViewModel.SetStatusMessage("Select packages to export.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "TidyWindow preset (*.yml)|*.yml|All files (*.*)|*.*",
            FileName = "tidywindow-preset.yml",
            AddExtension = true,
            DefaultExt = ".yml"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var preset = new BundlePreset("Custom selection", $"Exported on {DateTime.Now:yyyy-MM-dd}", selected);
        try
        {
            await _presetService.SavePresetAsync(dialog.FileName, preset);
            _mainViewModel.SetStatusMessage($"Saved preset with {selected.Length} package(s).");
            _activityLog.LogSuccess("Install hub", $"Saved preset with {selected.Length} package(s).");
        }
        catch (Exception ex)
        {
            _mainViewModel.SetStatusMessage($"Failed to save preset: {ex.Message}");
            _activityLog.LogError("Install hub", $"Failed to save preset: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ImportPresetAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "TidyWindow preset (*.yml)|*.yml|All files (*.*)|*.*",
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var preset = await _presetService.LoadPresetAsync(dialog.FileName);
            var resolution = _presetService.ResolvePackages(preset);

            if (WpfApplication.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            {
                await dispatcher.InvokeAsync(() => ApplyPreset(resolution, preset.Name));
            }
            else
            {
                ApplyPreset(resolution, preset.Name);
            }
        }
        catch (Exception ex)
        {
            _mainViewModel.SetStatusMessage($"Import failed: {ex.Message}");
            _activityLog.LogError("Install hub", $"Import failed: {ex.Message}");
        }
    }

    private void ApplyPreset(BundlePresetResolution resolution, string presetName)
    {
        foreach (var vm in Packages)
        {
            vm.IsSelected = false;
        }

        SelectedBundle = Bundles.FirstOrDefault();
        ApplyBundleFilter();

        foreach (var package in resolution.Packages)
        {
            if (_packageLookup.TryGetValue(package.Id, out var vm))
            {
                vm.IsSelected = true;
            }
        }

        if (resolution.Missing.Length > 0)
        {
            _mainViewModel.SetStatusMessage($"Imported '{presetName}' with missing packages: {string.Join(", ", resolution.Missing)}.");
            _activityLog.LogWarning("Install hub", $"Imported '{presetName}' with missing packages: {string.Join(", ", resolution.Missing)}.");
        }
        else
        {
            _mainViewModel.SetStatusMessage($"Imported '{presetName}' with {resolution.Packages.Length} package(s).");
            _activityLog.LogSuccess("Install hub", $"Imported '{presetName}' with {resolution.Packages.Length} package(s).");
        }
    }

    private void ApplyBundleFilter()
    {
        if (_suppressFilters)
        {
            return;
        }

        if (_packageLookup.Count == 0)
        {
            Packages.Clear();
            return;
        }

        IEnumerable<InstallPackageItemViewModel> items;

        if (SelectedBundle is null || SelectedBundle.IsSyntheticAll)
        {
            items = _packageLookup.Values;
        }
        else
        {
            items = SelectedBundle.PackageIds
                .Where(id => _packageLookup.ContainsKey(id))
                .Select(id => _packageLookup[id]);
        }

        var filter = SearchText;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            items = items.Where(item => item.Matches(filter));
        }

        var ordered = items
            .OrderBy(item => item.Definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        SynchronizeCollection(Packages, ordered);
        UpdatePackageQueueStates();
    }

    private void SynchronizeCollection(ObservableCollection<InstallPackageItemViewModel> target, IList<InstallPackageItemViewModel> source)
    {
        for (var index = target.Count - 1; index >= 0; index--)
        {
            var vm = target[index];
            if (!source.Contains(vm))
            {
                target.RemoveAt(index);
            }
        }

        for (var insertionIndex = 0; insertionIndex < source.Count; insertionIndex++)
        {
            var vm = source[insertionIndex];
            if (insertionIndex < target.Count)
            {
                if (!ReferenceEquals(target[insertionIndex], vm))
                {
                    if (target.Contains(vm))
                    {
                        var currentIndex = target.IndexOf(vm);
                        target.Move(currentIndex, insertionIndex);
                    }
                    else
                    {
                        target.Insert(insertionIndex, vm);
                    }
                }
            }
            else
            {
                target.Add(vm);
            }
        }
    }

    private void OnInstallQueueChanged(object? sender, InstallQueueChangedEventArgs e)
    {
        if (WpfApplication.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(() => ApplySnapshot(e.Snapshot));
        }
        else
        {
            ApplySnapshot(e.Snapshot);
        }
    }

    private void ApplySnapshot(InstallQueueOperationSnapshot snapshot)
    {
        if (_isDisposed)
        {
            return;
        }

        _snapshotCache.TryGetValue(snapshot.Id, out var previous);

        UpdateActiveCount(snapshot);
        LogSnapshotChange(snapshot, previous);

        if (!_operationLookup.TryGetValue(snapshot.Id, out var viewModel))
        {
            viewModel = new InstallOperationItemViewModel(snapshot);
            _operationLookup[snapshot.Id] = viewModel;
            Operations.Insert(0, viewModel);
        }

        viewModel.Update(snapshot);
        _snapshotCache[snapshot.Id] = snapshot;

        UpdatePackageQueueStates();
    }

    private void LogSnapshotChange(InstallQueueOperationSnapshot snapshot, InstallQueueOperationSnapshot? previous)
    {
        if (previous is not null
            && previous.Status == snapshot.Status
            && previous.AttemptCount == snapshot.AttemptCount
            && string.Equals(previous.LastMessage ?? string.Empty, snapshot.LastMessage ?? string.Empty, StringComparison.Ordinal))
        {
            return;
        }

        switch (snapshot.Status)
        {
            case InstallQueueStatus.Pending:
                if (previous is not null && snapshot.AttemptCount > previous.AttemptCount)
                {
                    _activityLog.LogInformation("Install hub", $"{snapshot.Package.Name} queued for retry (attempt {snapshot.AttemptCount}).");
                }
                else if (previous is not null && !string.IsNullOrWhiteSpace(snapshot.LastMessage) && !string.Equals(previous.LastMessage, snapshot.LastMessage, StringComparison.Ordinal))
                {
                    _activityLog.LogInformation("Install hub", $"{snapshot.Package.Name}: {snapshot.LastMessage}");
                }
                break;

            case InstallQueueStatus.Running:
                if (previous is null || previous.Status != InstallQueueStatus.Running)
                {
                    _activityLog.LogInformation("Install hub", $"{snapshot.Package.Name} installing...");
                }
                break;

            case InstallQueueStatus.Succeeded:
                if (previous is null || previous.Status != InstallQueueStatus.Succeeded)
                {
                    _activityLog.LogSuccess("Install hub", $"{snapshot.Package.Name} installed.", BuildDetails(snapshot));
                }
                break;

            case InstallQueueStatus.Failed:
                if (previous is null || previous.Status != InstallQueueStatus.Failed || snapshot.AttemptCount != previous.AttemptCount)
                {
                    var failureMessage = string.IsNullOrWhiteSpace(snapshot.LastMessage) ? "Installation failed." : snapshot.LastMessage.Trim();
                    _activityLog.LogError("Install hub", $"{snapshot.Package.Name} failed: {failureMessage}", BuildDetails(snapshot));
                }
                break;

            case InstallQueueStatus.Cancelled:
                if (previous is null || previous.Status != InstallQueueStatus.Cancelled)
                {
                    var cancelMessage = string.IsNullOrWhiteSpace(snapshot.LastMessage) ? "Cancelled." : snapshot.LastMessage.Trim();
                    _activityLog.LogWarning("Install hub", $"{snapshot.Package.Name} cancelled: {cancelMessage}", BuildDetails(snapshot));
                }
                break;
        }
    }

    private IEnumerable<string>? BuildDetails(InstallQueueOperationSnapshot snapshot)
    {
        var lines = new List<string>();

        if (!snapshot.Output.IsDefaultOrEmpty && snapshot.Output.Length > 0)
        {
            lines.Add("--- Output ---");
            foreach (var line in snapshot.Output)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    lines.Add(line);
                }
            }
        }

        if (!snapshot.Errors.IsDefaultOrEmpty && snapshot.Errors.Length > 0)
        {
            lines.Add("--- Errors ---");
            foreach (var line in snapshot.Errors)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    lines.Add(line);
                }
            }
        }

        return lines.Count == 0 ? null : lines;
    }

    private void UpdateActiveCount(InstallQueueOperationSnapshot snapshot)
    {
        if (_snapshotCache.TryGetValue(snapshot.Id, out var previous))
        {
            if (!string.Equals(previous.Package.Id, snapshot.Package.Id, StringComparison.OrdinalIgnoreCase))
            {
                if (previous.IsActive)
                {
                    DecrementActive(previous.Package.Id);
                }

                if (snapshot.IsActive)
                {
                    IncrementActive(snapshot.Package.Id);
                }
            }
            else
            {
                if (previous.IsActive && !snapshot.IsActive)
                {
                    DecrementActive(snapshot.Package.Id);
                }
                else if (!previous.IsActive && snapshot.IsActive)
                {
                    IncrementActive(snapshot.Package.Id);
                }
            }
        }
        else if (snapshot.IsActive)
        {
            IncrementActive(snapshot.Package.Id);
        }

        if (!snapshot.IsActive)
        {
            _snapshotCache[snapshot.Id] = snapshot;
        }
    }

    private void IncrementActive(string packageId)
    {
        if (!_activePackageCounts.TryGetValue(packageId, out var value))
        {
            _activePackageCounts[packageId] = 1;
        }
        else
        {
            _activePackageCounts[packageId] = value + 1;
        }
    }

    private void DecrementActive(string packageId)
    {
        if (!_activePackageCounts.TryGetValue(packageId, out var value))
        {
            return;
        }

        value--;
        if (value <= 0)
        {
            _activePackageCounts.Remove(packageId);
        }
        else
        {
            _activePackageCounts[packageId] = value;
        }
    }

    private void UpdatePackageQueueStates()
    {
        foreach (var kvp in _packageLookup)
        {
            var activeCount = _activePackageCounts.TryGetValue(kvp.Key, out var value) ? value : 0;
            var status = ResolveLatestStatus(kvp.Key);
            kvp.Value.UpdateQueueState(activeCount, status);
        }

        HasActiveOperations = _activePackageCounts.Values.Any(count => count > 0);
    }

    private string? ResolveLatestStatus(string packageId)
    {
        var snapshot = _snapshotCache.Values
            .Where(s => string.Equals(s.Package.Id, packageId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.CompletedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(s => s.StartedAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault();

        return snapshot?.LastMessage;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _installQueue.OperationChanged -= OnInstallQueueChanged;
    }
}

public sealed class InstallBundleItemViewModel
{
    public InstallBundleItemViewModel(string id, string name, string description, ImmutableArray<string> packageIds, bool isSyntheticAll = false)
    {
        Id = id;
        Name = name;
        Description = description;
        PackageIds = packageIds;
        IsSyntheticAll = isSyntheticAll;
    }

    public string Id { get; }

    public string Name { get; }

    public string Description { get; }

    public ImmutableArray<string> PackageIds { get; }

    public bool IsSyntheticAll { get; }

    public string PackageCountDisplay => PackageIds.Length == 1 ? "1 package" : $"{PackageIds.Length} packages";

    public static InstallBundleItemViewModel CreateAll(int packageCount)
    {
        return new InstallBundleItemViewModel("__all__", "All packages", "View every available package in the catalog.", ImmutableArray<string>.Empty, true)
        {
            _cachedCount = packageCount
        };
    }

    private int _cachedCount;

    public int BundlePackageCount => IsSyntheticAll ? _cachedCount : PackageIds.Length;
}

public sealed partial class InstallPackageItemViewModel : ObservableObject
{
    public InstallPackageItemViewModel(InstallPackageDefinition definition)
    {
        Definition = definition;
        RequiresAdmin = definition.RequiresAdmin;
        TagDisplay = definition.Tags.Length > 0 ? string.Join(" • ", definition.Tags) : string.Empty;
        ManagerLabel = definition.Manager.ToUpperInvariant();
    }

    public InstallPackageDefinition Definition { get; }

    public string ManagerLabel { get; }

    public string TagDisplay { get; }

    public bool RequiresAdmin { get; }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isQueued;

    [ObservableProperty]
    private string? _lastStatus;

    public string Summary => string.IsNullOrWhiteSpace(Definition.Summary) ? "" : Definition.Summary;

    public bool Matches(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        return Definition.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || Summary.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || Definition.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(TagDisplay) && TagDisplay.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    public void UpdateQueueState(int activeCount, string? status)
    {
        IsQueued = activeCount > 0;
        if (!string.IsNullOrWhiteSpace(status))
        {
            LastStatus = status;
        }
    }
}

public sealed partial class InstallOperationItemViewModel : ObservableObject
{
    public InstallOperationItemViewModel(InstallQueueOperationSnapshot snapshot)
    {
        Id = snapshot.Id;
        PackageName = snapshot.Package.Name;
        Update(snapshot);
    }

    public Guid Id { get; }

    public string PackageName { get; }

    [ObservableProperty]
    private string _statusLabel = "Pending";

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private string? _attempts;

    [ObservableProperty]
    private DateTimeOffset? _completedAt;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _canRetry;

    public void Update(InstallQueueOperationSnapshot snapshot)
    {
        StatusLabel = snapshot.Status switch
        {
            InstallQueueStatus.Pending => "Queued",
            InstallQueueStatus.Running => "Installing",
            InstallQueueStatus.Succeeded => "Installed",
            InstallQueueStatus.Failed => "Failed",
            InstallQueueStatus.Cancelled => "Cancelled",
            _ => snapshot.Status.ToString()
        };

        Message = snapshot.LastMessage;
        Attempts = snapshot.AttemptCount > 1 ? $"Attempts: {snapshot.AttemptCount}" : null;
        CompletedAt = snapshot.CompletedAt;
        IsActive = snapshot.IsActive;
        CanRetry = snapshot.CanRetry;
    }
}
