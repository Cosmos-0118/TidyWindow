using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.Services;
using TidyWindow.Core.Maintenance;
using WpfApplication = System.Windows.Application;

namespace TidyWindow.App.ViewModels;

public sealed partial class MaintenanceAutomationViewModel : ViewModelBase, IDisposable
{
    private readonly MaintenanceAutoUpdateScheduler _scheduler;
    private readonly PackageInventoryService _inventoryService;
    private readonly ActivityLogService _activityLog;
    private readonly MainViewModel _mainViewModel;
    private readonly IRelativeTimeTicker _relativeTimeTicker;
    private readonly ObservableCollection<MaintenanceAutomationPackageOptionViewModel> _options = new();
    private ReadOnlyObservableCollection<MaintenanceAutomationPackageOptionViewModel>? _optionsView;
    private readonly ObservableCollection<MaintenanceAutomationIntervalOption> _intervalOptions;
    private readonly ReadOnlyObservableCollection<MaintenanceAutomationIntervalOption> _intervalOptionsView;
    private static readonly MaintenanceAutomationIntervalOption[] DefaultIntervalOptions =
    {
        new(180, "Every 3 hours"),
        new(360, "Every 6 hours"),
        new(720, "Every 12 hours"),
        new(1440, "Every day"),
        new(4320, "Every 3 days"),
        new(10080, "Every week")
    };
    private bool _isInitialized;
    private bool _suspendStateUpdates;
    private bool _disposed;

    public MaintenanceAutomationViewModel(
        MaintenanceAutoUpdateScheduler scheduler,
        PackageInventoryService inventoryService,
        ActivityLogService activityLog,
        MainViewModel mainViewModel,
        IRelativeTimeTicker relativeTimeTicker)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _inventoryService = inventoryService ?? throw new ArgumentNullException(nameof(inventoryService));
        _activityLog = activityLog ?? throw new ArgumentNullException(nameof(activityLog));
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        _relativeTimeTicker = relativeTimeTicker ?? throw new ArgumentNullException(nameof(relativeTimeTicker));

        _options.CollectionChanged += (_, _) => UpdateOptionState();
        _optionsView = new ReadOnlyObservableCollection<MaintenanceAutomationPackageOptionViewModel>(_options);

        _intervalOptions = new ObservableCollection<MaintenanceAutomationIntervalOption>(DefaultIntervalOptions);
        _intervalOptionsView = new ReadOnlyObservableCollection<MaintenanceAutomationIntervalOption>(_intervalOptions);

        LoadFromSettings(_scheduler.CurrentSettings);
        _scheduler.SettingsChanged += OnSchedulerSettingsChanged;
        _relativeTimeTicker.Tick += OnRelativeTimeTick;
    }

    public ReadOnlyObservableCollection<MaintenanceAutomationPackageOptionViewModel> PackageOptions => _optionsView ??= new ReadOnlyObservableCollection<MaintenanceAutomationPackageOptionViewModel>(_options);

    public ReadOnlyObservableCollection<MaintenanceAutomationIntervalOption> IntervalOptions => _intervalOptionsView;

    public string LastRunSummary => LastRunUtc is null
        ? "Automation has not run yet."
        : $"Last run {FormatRelative(LastRunUtc.Value)}.";

    [ObservableProperty]
    private bool _isAutomationEnabled;

    [ObservableProperty]
    private bool _updateAllPackages;

    [ObservableProperty]
    private int _intervalMinutes = MaintenanceAutomationSettings.MinimumIntervalMinutes;

    [ObservableProperty]
    private DateTimeOffset? _lastRunUtc;

    [ObservableProperty]
    private bool _hasPendingChanges;

    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "Automation is idle.";

    [ObservableProperty]
    private string _inventorySummary = "Inventory not loaded yet.";

    [ObservableProperty]
    private string _selectionSummary = "Select the packages you want to update automatically.";

    [ObservableProperty]
    private bool _hasPackageOptions;

    [ObservableProperty]
    private bool _hasSelectedPackages;

    [ObservableProperty]
    private bool _hasInventory;

    public void EnsureInitialized()
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        _ = RefreshAutomationPackagesAsync();
    }

    partial void OnIsAutomationEnabledChanged(bool value)
    {
        if (_suspendStateUpdates)
        {
            return;
        }

        HasPendingChanges = true;
        UpdateStatusMessage();
        RunAutomationNowCommand.NotifyCanExecuteChanged();
        ApplyAutomationCommand.NotifyCanExecuteChanged();
    }

    partial void OnUpdateAllPackagesChanged(bool value)
    {
        if (_suspendStateUpdates)
        {
            return;
        }

        HasPendingChanges = true;
        UpdateSelectionSummary();
        UpdateStatusMessage();
        ApplyAutomationCommand.NotifyCanExecuteChanged();
        RunAutomationNowCommand.NotifyCanExecuteChanged();
    }

    partial void OnIntervalMinutesChanged(int value)
    {
        if (_suspendStateUpdates)
        {
            return;
        }

        var normalized = NormalizeIntervalMinutes(value);
        if (normalized != value)
        {
            _suspendStateUpdates = true;
            IntervalMinutes = normalized;
            _suspendStateUpdates = false;
            return;
        }

        HasPendingChanges = true;
        UpdateStatusMessage();
        ApplyAutomationCommand.NotifyCanExecuteChanged();
    }

    partial void OnLastRunUtcChanged(DateTimeOffset? value)
    {
        OnPropertyChanged(nameof(LastRunSummary));
        UpdateStatusMessage();
    }

    partial void OnHasPendingChangesChanged(bool value)
    {
        ApplyAutomationCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsRefreshingChanged(bool value)
    {
        ApplyAutomationCommand.NotifyCanExecuteChanged();
        RefreshAutomationPackagesCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        ApplyAutomationCommand.NotifyCanExecuteChanged();
        RunAutomationNowCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRefreshPackages))]
    private async Task RefreshAutomationPackagesAsync()
    {
        if (IsRefreshing)
        {
            return;
        }

        await RunOnUiThreadAsync(() =>
        {
            IsRefreshing = true;
            _mainViewModel.SetStatusMessage("Refreshing maintenance automation list...");
        }).ConfigureAwait(false);

        try
        {
            var snapshot = await _inventoryService.GetInventoryAsync().ConfigureAwait(false);
            await RunOnUiThreadAsync(() => ApplyInventorySnapshot(snapshot)).ConfigureAwait(false);
            _activityLog.LogInformation("Maintenance automation", "Automation catalog refreshed.");
        }
        catch (Exception ex)
        {
            await RunOnUiThreadAsync(() => InventorySummary = "Unable to load maintenance inventory.").ConfigureAwait(false);
            _activityLog.LogError("Maintenance automation", $"Failed to refresh automation packages: {ex.Message}");
        }
        finally
        {
            await RunOnUiThreadAsync(() =>
            {
                IsRefreshing = false;
                _mainViewModel.SetStatusMessage("Ready");
            }).ConfigureAwait(false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanApplySettings))]
    private async Task ApplyAutomationAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            _mainViewModel.SetStatusMessage("Saving maintenance automation settings...");
            var snapshot = BuildSettingsSnapshot();
            await _scheduler.ApplySettingsAsync(snapshot, runImmediately: false).ConfigureAwait(false);
            HasPendingChanges = false;
        }
        catch (Exception ex)
        {
            _activityLog.LogError("Maintenance automation", $"Failed to save settings: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            _mainViewModel.SetStatusMessage("Ready");
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunNow))]
    private async Task RunAutomationNowAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            _mainViewModel.SetStatusMessage("Running maintenance automation...");
            var result = await _scheduler.RunOnceAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _activityLog.LogError("Maintenance automation", $"Failed to run automation: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            _mainViewModel.SetStatusMessage("Ready");
        }
    }

    private bool CanRefreshPackages() => !IsRefreshing;

    private bool CanApplySettings() => !IsBusy && !IsRefreshing && HasPendingChanges;

    private bool CanRunNow()
    {
        if (IsBusy || IsRefreshing)
        {
            return false;
        }

        if (!IsAutomationEnabled)
        {
            return false;
        }

        return UpdateAllPackages || HasSelectedPackages;
    }

    private void ApplyInventorySnapshot(PackageInventorySnapshot snapshot)
    {
        var latestKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in snapshot.Packages)
        {
            var key = MaintenanceAutomationTarget.BuildKey(package.Manager, package.PackageIdentifier);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            latestKeys.Add(key);
            var existing = _options.FirstOrDefault(option => string.Equals(option.Key, key, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                var option = MaintenanceAutomationPackageOptionViewModel.FromInventory(package);
                option.PropertyChanged += OnOptionPropertyChanged;
                _options.Add(option);
            }
            else
            {
                existing.UpdateFromInventory(package);
            }
        }

        var staleOptions = _options.Where(option => !latestKeys.Contains(option.Key)).ToList();
        foreach (var stale in staleOptions)
        {
            stale.PropertyChanged -= OnOptionPropertyChanged;
            _options.Remove(stale);
        }

        HasInventory = true;
        InventorySummary = snapshot.Packages.Length == 0
            ? "No installed packages detected yet."
            : snapshot.Packages.Count(package => package.IsUpdateAvailable) switch
            {
                0 => $"{snapshot.Packages.Length} packages detected • no updates right now",
                1 => $"{snapshot.Packages.Length} packages detected • 1 update available",
                var updates => $"{snapshot.Packages.Length} packages detected • {updates} updates available"
            };

        if (!HasPendingChanges)
        {
            ApplySelectionFromSettings(_scheduler.CurrentSettings);
        }

        UpdateOptionState();
    }

    private void UpdateOptionState()
    {
        HasPackageOptions = _options.Count > 0;
        UpdateSelectionState();
    }

    private void UpdateSelectionState()
    {
        var selected = _options.Count(option => option.IsSelected);
        HasSelectedPackages = selected > 0;
        UpdateSelectionSummary(selected);
        UpdateStatusMessage();
        RunAutomationNowCommand.NotifyCanExecuteChanged();
    }

    private void UpdateSelectionSummary(int? selectedCount = null)
    {
        var count = selectedCount ?? _options.Count(option => option.IsSelected);
        if (UpdateAllPackages)
        {
            SelectionSummary = "All packages with updates will be queued automatically.";
            return;
        }

        if (!HasPackageOptions)
        {
            SelectionSummary = "Automation list will populate after the first inventory refresh.";
            return;
        }

        SelectionSummary = count switch
        {
            0 => "Select at least one package to keep updated automatically.",
            1 => "1 package will be updated when a new version is detected.",
            _ => $"{count} packages will be updated when new versions are detected."
        };
    }

    private void UpdateStatusMessage()
    {
        if (!IsAutomationEnabled)
        {
            StatusMessage = "Automation is disabled.";
            return;
        }

        if (!UpdateAllPackages && !HasSelectedPackages)
        {
            StatusMessage = "Select the packages you want to keep updated or enable update-all.";
            return;
        }

        var intervalLabel = FormatInterval(IntervalMinutes);
        var lastRun = LastRunUtc is null
            ? "First run has not executed yet."
            : $"{FormatRelative(LastRunUtc.Value)}.";
        StatusMessage = UpdateAllPackages
            ? $"All packages with updates will run every {intervalLabel}. Last run {lastRun}"
            : $"Selected packages will run every {intervalLabel}. Last run {lastRun}";
    }

    private MaintenanceAutomationSettings BuildSettingsSnapshot()
    {
        IEnumerable<MaintenanceAutomationTarget> targets = _options
            .Where(option => option.IsSelected)
            .Select(option => new MaintenanceAutomationTarget(option.Manager, option.PackageId, option.DisplayName));

        return new MaintenanceAutomationSettings(
            IsAutomationEnabled,
            UpdateAllPackages,
            IntervalMinutes,
            LastRunUtc,
            targets);
    }

    private void ApplySelectionFromSettings(MaintenanceAutomationSettings settings)
    {
        _suspendStateUpdates = true;
        try
        {
            var map = settings.Targets.IsDefault
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : settings.Targets.Select(target => target.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var option in _options)
            {
                option.IsSelected = map.Contains(option.Key);
            }
        }
        finally
        {
            _suspendStateUpdates = false;
            UpdateSelectionState();
        }
    }

    private void LoadFromSettings(MaintenanceAutomationSettings settings)
    {
        _suspendStateUpdates = true;
        try
        {
            IsAutomationEnabled = settings.AutomationEnabled;
            UpdateAllPackages = settings.UpdateAllPackages;
            IntervalMinutes = NormalizeIntervalMinutes(settings.IntervalMinutes);
            LastRunUtc = settings.LastRunUtc;
            HasPendingChanges = false;
        }
        finally
        {
            _suspendStateUpdates = false;
            UpdateStatusMessage();
            UpdateSelectionSummary();
        }
    }

    private int NormalizeIntervalMinutes(int minutes)
    {
        var clamped = Math.Clamp(minutes <= 0 ? MaintenanceAutomationSettings.MinimumIntervalMinutes : minutes, MaintenanceAutomationSettings.MinimumIntervalMinutes, MaintenanceAutomationSettings.MaximumIntervalMinutes);
        EnsureIntervalOptionExists(clamped);
        return clamped;
    }

    private void EnsureIntervalOptionExists(int minutes)
    {
        if (_intervalOptions.Any(option => option.Minutes == minutes))
        {
            return;
        }

        var label = GetIntervalLabel(minutes);
        var insertIndex = 0;
        while (insertIndex < _intervalOptions.Count && _intervalOptions[insertIndex].Minutes < minutes)
        {
            insertIndex++;
        }

        _intervalOptions.Insert(insertIndex, new MaintenanceAutomationIntervalOption(minutes, label));
    }

    private static string GetIntervalLabel(int minutes)
    {
        var preset = Array.Find(DefaultIntervalOptions, option => option.Minutes == minutes);
        if (preset is not null)
        {
            return preset.Label;
        }

        return $"Every {FormatInterval(minutes)}";
    }

    private void ApplySchedulerSettingsUpdate(MaintenanceAutomationSettings settings)
    {
        LastRunUtc = settings.LastRunUtc;

        if (HasPendingChanges)
        {
            return;
        }

        LoadFromSettings(settings);
        ApplySelectionFromSettings(settings);
    }

    private void OnSchedulerSettingsChanged(object? sender, MaintenanceAutomationSettings settings)
    {
        if (_disposed)
        {
            return;
        }

        if (WpfApplication.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => ApplySchedulerSettingsUpdate(settings)));
        }
        else
        {
            ApplySchedulerSettingsUpdate(settings);
        }
    }

    private void OnOptionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(MaintenanceAutomationPackageOptionViewModel.IsSelected), StringComparison.Ordinal))
        {
            return;
        }

        if (_suspendStateUpdates)
        {
            return;
        }

        HasPendingChanges = true;
        UpdateSelectionState();
    }

    private void OnRelativeTimeTick(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        OnPropertyChanged(nameof(LastRunSummary));
        UpdateStatusMessage();
    }

    private static string FormatInterval(int minutes)
    {
        if (minutes >= 1440 && minutes % 1440 == 0)
        {
            var days = minutes / 1440;
            return days == 1 ? "1 day" : $"{days} days";
        }

        if (minutes >= 60 && minutes % 60 == 0)
        {
            var hours = minutes / 60;
            return hours == 1 ? "1 hour" : $"{hours} hours";
        }

        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
    }

    private static string FormatRelative(DateTimeOffset timestamp)
    {
        var delta = DateTimeOffset.UtcNow - timestamp;
        if (delta < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (delta < TimeSpan.FromHours(1))
        {
            var minutes = Math.Max(1, (int)Math.Round(delta.TotalMinutes));
            return minutes == 1 ? "1 minute ago" : $"{minutes} minutes ago";
        }

        if (delta < TimeSpan.FromDays(1))
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

        var dispatcher = WpfApplication.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _scheduler.SettingsChanged -= OnSchedulerSettingsChanged;
        _relativeTimeTicker.Tick -= OnRelativeTimeTick;
        foreach (var option in _options)
        {
            option.PropertyChanged -= OnOptionPropertyChanged;
        }
    }
}

public sealed partial class MaintenanceAutomationPackageOptionViewModel : ObservableObject
{
    private MaintenanceAutomationPackageOptionViewModel(
        string manager,
        string packageId,
        string displayName,
        string installedVersion,
        string? availableVersion,
        bool requiresAdministrator)
    {
        Manager = manager;
        PackageId = packageId;
        DisplayName = displayName;
        InstalledVersion = string.IsNullOrWhiteSpace(installedVersion) ? "Unknown" : installedVersion;
        AvailableVersion = string.IsNullOrWhiteSpace(availableVersion) ? null : availableVersion;
        RequiresAdministrator = requiresAdministrator;
    }

    public string Manager { get; }

    public string PackageId { get; }

    public string DisplayName { get; private set; }

    public string InstalledVersion { get; private set; }

    public string? AvailableVersion { get; private set; }

    public bool RequiresAdministrator { get; private set; }

    public string Key => MaintenanceAutomationTarget.BuildKey(Manager, PackageId);

    public string VersionSummary => AvailableVersion is null
        ? InstalledVersion
        : InstalledVersion.Equals(AvailableVersion, StringComparison.OrdinalIgnoreCase)
            ? InstalledVersion
            : $"{InstalledVersion} → {AvailableVersion}";

    [ObservableProperty]
    private bool _isSelected;

    public static MaintenanceAutomationPackageOptionViewModel FromInventory(PackageInventoryItem item)
    {
        if (item is null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        var display = string.IsNullOrWhiteSpace(item.Catalog?.DisplayName) ? item.Name : item.Catalog!.DisplayName;
        return new MaintenanceAutomationPackageOptionViewModel(
            item.Manager,
            item.PackageIdentifier,
            display,
            item.InstalledVersion,
            item.AvailableVersion,
            item.Catalog?.RequiresAdmin ?? false);
    }

    public void UpdateFromInventory(PackageInventoryItem item)
    {
        if (item is null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        DisplayName = string.IsNullOrWhiteSpace(item.Catalog?.DisplayName) ? item.Name : item.Catalog!.DisplayName;
        InstalledVersion = string.IsNullOrWhiteSpace(item.InstalledVersion) ? InstalledVersion : item.InstalledVersion;
        AvailableVersion = string.IsNullOrWhiteSpace(item.AvailableVersion) ? null : item.AvailableVersion;
        RequiresAdministrator = item.Catalog?.RequiresAdmin ?? RequiresAdministrator;
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(InstalledVersion));
        OnPropertyChanged(nameof(AvailableVersion));
        OnPropertyChanged(nameof(VersionSummary));
    }
}

public sealed record MaintenanceAutomationIntervalOption(int Minutes, string Label);
