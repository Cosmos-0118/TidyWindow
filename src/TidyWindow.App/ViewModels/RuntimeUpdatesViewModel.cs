using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.Core.Install;
using TidyWindow.Core.Updates;

namespace TidyWindow.App.ViewModels;

public sealed partial class RuntimeUpdatesViewModel : ViewModelBase
{
    private readonly RuntimeCatalogService _runtimeCatalogService;
    private readonly InstallCatalogService _installCatalogService;
    private readonly InstallQueue _installQueue;
    private readonly MainViewModel _mainViewModel;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private DateTimeOffset? _lastRefreshed;

    [ObservableProperty]
    private string _headline = "Monitor runtime dependencies";

    public RuntimeUpdatesViewModel(RuntimeCatalogService runtimeCatalogService, InstallCatalogService installCatalogService, InstallQueue installQueue, MainViewModel mainViewModel)
    {
        _runtimeCatalogService = runtimeCatalogService;
        _installCatalogService = installCatalogService;
        _installQueue = installQueue;
        _mainViewModel = mainViewModel;

        Runtimes.CollectionChanged += OnCollectionChanged;
    }

    public ObservableCollection<RuntimeUpdateItemViewModel> Runtimes { get; } = new();

    public bool HasResults => Runtimes.Count > 0;

    public int UpdatesAvailableCount => Runtimes.Count(static runtime => runtime.IsUpdateAvailable);

    public int MissingRuntimesCount => Runtimes.Count(static runtime => runtime.IsMissing);

    public string SummaryText => HasResults
        ? $"{UpdatesAvailableCount} update(s) • {MissingRuntimesCount} missing"
        : "Refresh to check for runtime updates.";

    public string LastRefreshedDisplay => LastRefreshed is DateTimeOffset timestamp
        ? $"Last checked {timestamp.LocalDateTime:G}"
        : "No checks run yet.";

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        RuntimeUpdateCheckResult? scanResult = null;
        IReadOnlyList<RuntimeCatalogEntry> catalog;

        try
        {
            catalog = await _runtimeCatalogService.GetCatalogAsync();
        }
        catch (Exception ex)
        {
            _mainViewModel.SetStatusMessage($"Failed to load runtime catalog: {ex.Message}");
            return;
        }

        Exception? scanFailure = null;

        try
        {
            IsBusy = true;

            scanResult = await _runtimeCatalogService.CheckForUpdatesAsync();
        }
        catch (Exception ex)
        {
            scanFailure = ex;
            _mainViewModel.SetStatusMessage($"Runtime update check failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }

        var statusLookup = scanResult?.Runtimes
            .GroupBy(static status => status.CatalogEntry.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, RuntimeUpdateStatus>(StringComparer.OrdinalIgnoreCase);

        Runtimes.Clear();

        foreach (var entry in catalog.OrderBy(static entry => entry.DisplayName))
        {
            if (statusLookup.TryGetValue(entry.Id, out var matched))
            {
                Runtimes.Add(new RuntimeUpdateItemViewModel(matched));
                continue;
            }

            var fallbackStatus = new RuntimeUpdateStatus(
                catalogEntry: entry,
                state: RuntimeUpdateState.Unknown,
                installedVersion: "Not detected",
                latestVersion: "Unknown",
                downloadUrl: entry.DownloadUrl,
                notes: scanFailure is null ? "No scan data yet." : "Scan failed. See status bar.");

            Runtimes.Add(new RuntimeUpdateItemViewModel(fallbackStatus));
        }

        ApplyQueueState();
        QueueRuntimeUpdateCommand.NotifyCanExecuteChanged();

        if (scanResult is not null && scanFailure is null)
        {
            LastRefreshed = scanResult.GeneratedAt;
            RaiseSummaryProperties();
            _mainViewModel.SetStatusMessage($"Runtime scan complete: {UpdatesAvailableCount} updates, {MissingRuntimesCount} missing.");
        }
        else if (scanFailure is not null)
        {
            RaiseSummaryProperties();
        }
    }

    partial void OnLastRefreshedChanged(DateTimeOffset? oldValue, DateTimeOffset? newValue)
    {
        OnPropertyChanged(nameof(LastRefreshedDisplay));
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RaiseSummaryProperties();
        QueueRuntimeUpdateCommand.NotifyCanExecuteChanged();
    }

    private void RaiseSummaryProperties()
    {
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(UpdatesAvailableCount));
        OnPropertyChanged(nameof(MissingRuntimesCount));
        OnPropertyChanged(nameof(SummaryText));
    }

    private void ApplyQueueState()
    {
        var activePackageIds = _installQueue.GetSnapshot()
            .Where(static snapshot => snapshot.IsActive)
            .Select(static snapshot => snapshot.Package.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var runtime in Runtimes)
        {
            if (runtime.InstallPackageId is { } packageId && activePackageIds.Contains(packageId))
            {
                runtime.IsQueued = true;
            }
            else
            {
                runtime.IsQueued = false;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanQueueRuntimeUpdate))]
    private void QueueRuntimeUpdate(RuntimeUpdateItemViewModel? runtime)
    {
        if (runtime is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(runtime.InstallPackageId))
        {
            _mainViewModel.SetStatusMessage($"No installer mapping defined for {runtime.DisplayName}.");
            return;
        }

        if (!_installCatalogService.TryGetPackage(runtime.InstallPackageId, out var package))
        {
            _mainViewModel.SetStatusMessage($"Package '{runtime.InstallPackageId}' is missing from the install catalog.");
            return;
        }

        var snapshot = _installQueue.Enqueue(package);
        runtime.IsQueued = true;
        QueueRuntimeUpdateCommand.NotifyCanExecuteChanged();
        _mainViewModel.SetStatusMessage($"Queued update for {snapshot.Package.Name}.");
    }

    private bool CanQueueRuntimeUpdate(RuntimeUpdateItemViewModel? runtime)
    {
        return runtime?.CanQueueUpdate ?? false;
    }
}

public sealed partial class RuntimeUpdateItemViewModel : ObservableObject
{
    public RuntimeUpdateItemViewModel(RuntimeUpdateStatus status)
    {
        Status = status ?? throw new ArgumentNullException(nameof(status));
    }

    public RuntimeUpdateStatus Status { get; }

    public string DisplayName => Status.CatalogEntry.DisplayName;

    public string Vendor => Status.CatalogEntry.Vendor;

    public string Description => Status.CatalogEntry.Description;

    public RuntimeUpdateState State => Status.State;

    public string InstalledVersion => Status.InstalledVersion;

    public string LatestVersion => Status.LatestVersion;

    public string DownloadUrl => Status.DownloadUrl;

    public string Notes => Status.Notes;

    public bool IsUpdateAvailable => Status.IsUpdateAvailable;

    public bool IsMissing => Status.IsMissing;

    public string? InstallPackageId => Status.CatalogEntry.InstallPackageId;

    public bool HasQueueAction => Status.HasInstaller;

    [ObservableProperty]
    private bool _isQueued;

    public bool CanQueueUpdate => IsUpdateAvailable && HasQueueAction && !IsQueued;

    public string QueueButtonLabel => IsQueued ? "Queued" : "Queue update";

    public string StateDisplay => State switch
    {
        RuntimeUpdateState.UpToDate => "Up to date",
        RuntimeUpdateState.UpdateAvailable => "Update available",
        RuntimeUpdateState.NotInstalled => "Not installed",
        _ => "Unknown"
    };

    partial void OnIsQueuedChanged(bool oldValue, bool newValue)
    {
        OnPropertyChanged(nameof(CanQueueUpdate));
        OnPropertyChanged(nameof(QueueButtonLabel));
    }
}
