using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private readonly Dictionary<string, InstallPackageItemViewModel> _packageLookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, InstallOperationItemViewModel> _operationLookup = new();
    private readonly Dictionary<Guid, InstallQueueOperationSnapshot> _snapshotCache = new();
    private readonly Dictionary<string, int> _activePackageCounts = new(StringComparer.OrdinalIgnoreCase);
    private bool _isDisposed;

    public InstallHubViewModel(InstallCatalogService catalogService, InstallQueue installQueue, BundlePresetService presetService, MainViewModel mainViewModel)
    {
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _installQueue = installQueue ?? throw new ArgumentNullException(nameof(installQueue));
        _presetService = presetService ?? throw new ArgumentNullException(nameof(presetService));
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));

        Bundles = new ObservableCollection<InstallBundleItemViewModel>();
        Packages = new ObservableCollection<InstallPackageItemViewModel>();
        Operations = new ObservableCollection<InstallOperationItemViewModel>();

        InitializeCatalog();

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
        }

        UpdatePackageQueueStates();

        _installQueue.OperationChanged += OnInstallQueueChanged;
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

    private void InitializeCatalog()
    {
        IReadOnlyList<InstallPackageDefinition> allPackages;

        try
        {
            allPackages = _catalogService.Packages;
        }
        catch (Exception ex)
        {
            _mainViewModel.SetStatusMessage($"Install catalog failed to load: {ex.Message}");
            return;
        }

        var allBundle = InstallBundleItemViewModel.CreateAll(allPackages.Count);
        Bundles.Add(allBundle);

        foreach (var package in allPackages)
        {
            var vm = new InstallPackageItemViewModel(package);
            _packageLookup[package.Id] = vm;
        }

        foreach (var bundle in _catalogService.Bundles)
        {
            Bundles.Add(new InstallBundleItemViewModel(
                bundle.Id,
                bundle.Name,
                bundle.Description,
                bundle.PackageIds));
        }

        SelectedBundle = Bundles.FirstOrDefault();
        ApplyBundleFilter();
    }

    partial void OnSelectedBundleChanged(InstallBundleItemViewModel? oldValue, InstallBundleItemViewModel? newValue)
    {
        ApplyBundleFilter();
    }

    partial void OnSearchTextChanged(string? oldValue, string? newValue)
    {
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
        }
        catch (Exception ex)
        {
            _mainViewModel.SetStatusMessage($"Failed to save preset: {ex.Message}");
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
        }
        else
        {
            _mainViewModel.SetStatusMessage($"Imported '{presetName}' with {resolution.Packages.Length} package(s).");
        }
    }

    private void ApplyBundleFilter()
    {
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

        UpdateActiveCount(snapshot);

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
