using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.Services;
using TidyWindow.Core.PathPilot;

namespace TidyWindow.App.ViewModels;

public sealed partial class PathPilotViewModel : ViewModelBase, IDisposable
{
    private static readonly string MachineScopeMessage = "PathPilot modifies HKLM + system PATH entries, so changes affect every Windows account.";

    private readonly PathPilotInventoryService _inventoryService;
    private readonly ActivityLogService _activityLog;
    private readonly MainViewModel _mainViewModel;
    private readonly IAutomationWorkTracker _workTracker;
    private readonly UserPreferencesService _preferences;
    private readonly int _pageSize = 12;
    private bool _isDisposed;
    private bool _hasAcknowledgedMachineScopeWarning;
    private PathPilotSwitchRequest? _pendingSwitchRequest;
    private int _currentPage = 1;
    private bool _suppressPagingNotifications;

    public PathPilotViewModel(PathPilotInventoryService inventoryService, ActivityLogService activityLogService, MainViewModel mainViewModel, IAutomationWorkTracker workTracker, UserPreferencesService preferences)
    {
        _inventoryService = inventoryService ?? throw new ArgumentNullException(nameof(inventoryService));
        _activityLog = activityLogService ?? throw new ArgumentNullException(nameof(activityLogService));
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        _workTracker = workTracker ?? throw new ArgumentNullException(nameof(workTracker));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));

        Runtimes = new ObservableCollection<PathPilotRuntimeCardViewModel>();
        PagedRuntimes = new ObservableCollection<PathPilotRuntimeCardViewModel>();
        Warnings = new ObservableCollection<string>();

        Runtimes.CollectionChanged += OnRuntimeCollectionChanged;
        Warnings.CollectionChanged += OnWarningsCollectionChanged;

        ShowPathPilotHero = _preferences.Current.ShowPathPilotHero;
    }

    public ObservableCollection<PathPilotRuntimeCardViewModel> Runtimes { get; }

    public ObservableCollection<PathPilotRuntimeCardViewModel> PagedRuntimes { get; }

    public ObservableCollection<string> Warnings { get; }

    public string MachineScopeWarning => MachineScopeMessage;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isInventoryLoading;

    [ObservableProperty]
    private bool _isSwitchingPath;

    [ObservableProperty]
    private bool _isMachineScopeWarningOpen;

    [ObservableProperty]
    private DateTimeOffset? _lastRefreshedAt;

    [ObservableProperty]
    private string _headline = "System-wide runtime control";

    [ObservableProperty]
    private bool _showPathPilotHero = true;

    [ObservableProperty]
    private PathPilotRuntimeCardViewModel? _installationsDialogRuntime;

    [ObservableProperty]
    private PathPilotRuntimeCardViewModel? _detailsDialogRuntime;

    [ObservableProperty]
    private PathPilotRuntimeCardViewModel? _resolutionDialogRuntime;

    public bool HasRuntimeData => Runtimes.Count > 0;

    public bool HasWarnings => Warnings.Count > 0;

    public bool IsInstallationsDialogOpen => InstallationsDialogRuntime is not null;

    public bool IsDetailsDialogOpen => DetailsDialogRuntime is not null;

    public bool IsResolutionDialogOpen => ResolutionDialogRuntime is not null;

    public int CurrentPage => _currentPage;

    public int TotalPages => ComputeTotalPages(Runtimes.Count, _pageSize);

    public string PageDisplay => Runtimes.Count == 0
        ? "Page 0 of 0"
        : $"Page {_currentPage} of {TotalPages}";

    public bool CanGoToPreviousPage => _currentPage > 1;

    public bool CanGoToNextPage => _currentPage < TotalPages;

    public bool HasMultiplePages => Runtimes.Count > _pageSize;

    public string Summary => BuildSummary();

    public string LastRefreshedDisplay => LastRefreshedAt is null
        ? "Inventory has not been collected yet."
        : $"Inventory updated {FormatRelativeTime(LastRefreshedAt.Value)}";

    public event EventHandler? PageChanged;

    public void ResetCachedInteractionState()
    {
        _pendingSwitchRequest = null;
        _hasAcknowledgedMachineScopeWarning = false;
        if (IsMachineScopeWarningOpen)
        {
            IsMachineScopeWarningOpen = false;
        }

        InstallationsDialogRuntime = null;
        DetailsDialogRuntime = null;
        ResolutionDialogRuntime = null;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        IsInventoryLoading = true;
        _mainViewModel.SetStatusMessage("Scanning runtimes...");

        try
        {
            var inventoryTask = _inventoryService.GetInventoryAsync();
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(45));

            var completed = await Task.WhenAny(inventoryTask, timeoutTask).ConfigureAwait(false);
            if (completed == inventoryTask)
            {
                var snapshot = await inventoryTask.ConfigureAwait(false);

                await RunOnUiThreadAsync(() => ApplySnapshot(snapshot)).ConfigureAwait(false);

                var message = $"Runtime inventory ready • {snapshot.Runtimes.Length} runtime(s).";
                _activityLog.LogSuccess("PathPilot", message, BuildSnapshotDetails(snapshot));
                _mainViewModel.SetStatusMessage(message);
            }
            else
            {
                _activityLog.LogWarning("PathPilot", "Runtime inventory scan is taking unusually long; continuing in background.", Array.Empty<string>());
                await RunOnUiThreadAsync(() =>
                {
                    _mainViewModel.SetStatusMessage("Runtime inventory is taking longer than usual; results will appear once ready.");
                }).ConfigureAwait(false);

                _ = inventoryTask.ContinueWith(t =>
                {
                    if (t.IsFaulted && t.Exception is not null)
                    {
                        _activityLog.LogError("PathPilot", "Background runtime inventory failed after timeout.", new[] { t.Exception.ToString() });
                    }
                }, TaskScheduler.Default);
            }
        }
        catch (Exception ex)
        {
            var error = string.IsNullOrWhiteSpace(ex.Message) ? "Runtime inventory failed." : ex.Message.Trim();
            await RunOnUiThreadAsync(() => _mainViewModel.SetStatusMessage(error)).ConfigureAwait(false);
            _activityLog.LogError("PathPilot", error, new[] { ex.ToString() });
        }
        finally
        {
            await RunOnUiThreadAsync(() =>
            {
                IsBusy = false;
                IsInventoryLoading = false;
            }).ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private async Task DismissMachineScopeWarning()
    {
        _hasAcknowledgedMachineScopeWarning = true;
        IsMachineScopeWarningOpen = false;

        if (_pendingSwitchRequest is { } pendingRequest)
        {
            _pendingSwitchRequest = null;
            await ExecuteSwitchRuntimeAsync(pendingRequest).ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private void CancelMachineScopeWarning()
    {
        _pendingSwitchRequest = null;
        IsMachineScopeWarningOpen = false;
    }

    [RelayCommand]
    private void GoToPreviousPage()
    {
        if (!CanGoToPreviousPage)
        {
            return;
        }

        _currentPage--;
        RefreshPagedRuntimes(resetPage: false, raisePageChanged: true);
    }

    [RelayCommand]
    private void GoToNextPage()
    {
        if (!CanGoToNextPage)
        {
            return;
        }

        _currentPage++;
        RefreshPagedRuntimes(resetPage: false, raisePageChanged: true);
    }

    [RelayCommand]
    private async Task SwitchRuntimeAsync(PathPilotSwitchRequest? request)
    {
        if (request is null || IsBusy)
        {
            return;
        }

        if (!_hasAcknowledgedMachineScopeWarning)
        {
            _pendingSwitchRequest = request;
            IsMachineScopeWarningOpen = true;
            return;
        }

        await ExecuteSwitchRuntimeAsync(request).ConfigureAwait(false);
    }

    private async Task ExecuteSwitchRuntimeAsync(PathPilotSwitchRequest request)
    {
        if (IsBusy)
        {
            return;
        }

        _pendingSwitchRequest = null;
        IsBusy = true;
        IsSwitchingPath = true;
        var runtimeName = string.IsNullOrWhiteSpace(request.RuntimeName) ? request.RuntimeId : request.RuntimeName;
        _mainViewModel.SetStatusMessage($"Switching {runtimeName}...");

        Guid workToken = Guid.Empty;

        try
        {
            var workDescription = string.IsNullOrWhiteSpace(runtimeName)
                ? "PathPilot runtime switch"
                : $"PathPilot switch to {runtimeName}";
            workToken = _workTracker.BeginWork(AutomationWorkType.Maintenance, workDescription);

            var result = await _inventoryService.SwitchRuntimeAsync(request).ConfigureAwait(false);
            var snapshotToApply = result.Snapshot;

            const int refreshAttempts = 3;
            for (var attempt = 0; attempt < refreshAttempts; attempt++)
            {
                try
                {
                    var refreshedSnapshot = await _inventoryService.GetInventoryAsync().ConfigureAwait(false);
                    snapshotToApply = refreshedSnapshot;

                    if (SwitchResultReflected(refreshedSnapshot, result.SwitchResult))
                    {
                        break;
                    }
                }
                catch (Exception refreshEx)
                {
                    _activityLog.LogWarning("PathPilot", "Switch completed but refresh failed; showing immediate snapshot instead.", new[] { refreshEx.ToString() });
                    break;
                }

                if (attempt < refreshAttempts - 1)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(750)).ConfigureAwait(false);
                }
            }

            snapshotToApply = ApplySwitchResultSnapshot(snapshotToApply, result.SwitchResult);

            await RunOnUiThreadAsync(() => ApplySnapshot(snapshotToApply)).ConfigureAwait(false);

            var message = string.IsNullOrWhiteSpace(result.SwitchResult.Message)
                ? $"PATH now prioritizes {runtimeName}."
                : result.SwitchResult.Message!;
            _activityLog.LogSuccess("PathPilot", message, BuildSwitchDetails(result.SwitchResult));
            _mainViewModel.SetStatusMessage(message);
        }
        catch (Exception ex)
        {
            var error = string.IsNullOrWhiteSpace(ex.Message) ? "PathPilot switch failed." : ex.Message.Trim();
            _activityLog.LogError("PathPilot", error, new[] { ex.ToString() });
            await RunOnUiThreadAsync(() => _mainViewModel.SetStatusMessage(error)).ConfigureAwait(false);
        }
        finally
        {
            if (workToken != Guid.Empty)
            {
                _workTracker.CompleteWork(workToken);
            }

            await RunOnUiThreadAsync(() =>
            {
                IsBusy = false;
                IsSwitchingPath = false;
            }).ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private async Task ExportJsonAsync()
    {
        await ExportAsync(PathPilotExportFormat.Json).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task ExportMarkdownAsync()
    {
        await ExportAsync(PathPilotExportFormat.Markdown).ConfigureAwait(false);
    }

    [RelayCommand]
    private void ShowInstallations(PathPilotRuntimeCardViewModel? runtime)
    {
        if (runtime is null || !runtime.HasInstallations)
        {
            return;
        }

        InstallationsDialogRuntime = runtime;
    }

    [RelayCommand]
    private void CloseInstallations()
    {
        InstallationsDialogRuntime = null;
    }

    [RelayCommand]
    private void ShowDetails(PathPilotRuntimeCardViewModel? runtime)
    {
        if (runtime is null)
        {
            return;
        }

        DetailsDialogRuntime = runtime;
    }

    [RelayCommand]
    private void CloseDetails()
    {
        DetailsDialogRuntime = null;
    }

    [RelayCommand]
    private void ShowResolutionOrder(PathPilotRuntimeCardViewModel? runtime)
    {
        if (runtime is null || !runtime.HasResolutionOrder)
        {
            return;
        }

        ResolutionDialogRuntime = runtime;
    }

    [RelayCommand]
    private void CloseResolutionOrder()
    {
        ResolutionDialogRuntime = null;
    }

    private async Task ExportAsync(PathPilotExportFormat format)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        var label = format == PathPilotExportFormat.Json ? "JSON" : "Markdown";
        _mainViewModel.SetStatusMessage($"Preparing {label} PathPilot report...");

        Guid workToken = Guid.Empty;

        try
        {
            var workDescription = $"PathPilot {label} export";
            workToken = _workTracker.BeginWork(AutomationWorkType.Maintenance, workDescription);

            var result = await _inventoryService.ExportInventoryAsync(format).ConfigureAwait(false);
            var message = $"{label} report saved to {result.FilePath}.";
            var details = new List<string>
            {
                $"Generated at: {result.GeneratedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}"
            };
            _activityLog.LogSuccess("PathPilot", message, details);
            _mainViewModel.SetStatusMessage(message);
        }
        catch (Exception ex)
        {
            var error = string.IsNullOrWhiteSpace(ex.Message) ? "PathPilot export failed." : ex.Message.Trim();
            _activityLog.LogError("PathPilot", error, new[] { ex.ToString() });
            await RunOnUiThreadAsync(() => _mainViewModel.SetStatusMessage(error)).ConfigureAwait(false);
        }
        finally
        {
            if (workToken != Guid.Empty)
            {
                _workTracker.CompleteWork(workToken);
            }

            await RunOnUiThreadAsync(() => IsBusy = false).ConfigureAwait(false);
        }
    }

    private void ApplySnapshot(PathPilotInventorySnapshot snapshot)
    {
        _suppressPagingNotifications = true;
        try
        {
            var ordered = snapshot.Runtimes
                .OrderBy(runtime => runtime.Status.IsMissing ? 1 : 0)
                .ThenBy(runtime => runtime.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Runtimes.Clear();
            foreach (var runtime in ordered)
            {
                Runtimes.Add(new PathPilotRuntimeCardViewModel(runtime));
            }

            InstallationsDialogRuntime = null;
            DetailsDialogRuntime = null;
            ResolutionDialogRuntime = null;

            Warnings.Clear();
            foreach (var warning in snapshot.Warnings)
            {
                Warnings.Add(warning);
                _activityLog.LogWarning("PathPilot", warning);
            }

            LastRefreshedAt = snapshot.GeneratedAt.ToLocalTime();
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(HasRuntimeData));
            OnPropertyChanged(nameof(LastRefreshedDisplay));
        }
        finally
        {
            _suppressPagingNotifications = false;
        }

        RefreshPagedRuntimes(resetPage: true, raisePageChanged: true);
    }

    private string BuildSummary()
    {
        if (Runtimes.Count == 0)
        {
            return "No runtimes detected yet.";
        }

        var missing = Runtimes.Count(runtime => runtime.IsMissing);
        var drifted = Runtimes.Count(runtime => runtime.IsDrifted);
        var total = Runtimes.Count;

        return total switch
        {
            1 => drifted > 0
                ? "1 runtime • drift detected"
                : missing > 0
                    ? "1 runtime • missing"
                    : "1 runtime monitored",
            _ => $"{total} runtimes • {missing} missing • {drifted} drift"
        };
    }

    private static IEnumerable<string>? BuildSnapshotDetails(PathPilotInventorySnapshot snapshot)
    {
        if (snapshot.Runtimes.Length == 0 && snapshot.Warnings.Length == 0)
        {
            return null;
        }

        var lines = new List<string>
        {
            $"Generated at: {snapshot.GeneratedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}"
        };

        var missing = snapshot.Runtimes.Count(runtime => runtime.Status.IsMissing);
        var drifted = snapshot.Runtimes.Count(runtime => runtime.Status.IsDrifted);
        if (missing > 0)
        {
            lines.Add($"Missing runtimes: {missing}");
        }

        if (drifted > 0)
        {
            lines.Add($"Runtimes out of desired version: {drifted}");
        }

        return lines;
    }

    private static IEnumerable<string> BuildSwitchDetails(PathPilotSwitchResult result)
    {
        var details = new List<string>();

        if (!string.IsNullOrWhiteSpace(result.TargetDirectory))
        {
            details.Add($"Target directory: {result.TargetDirectory}");
        }

        if (!string.IsNullOrWhiteSpace(result.TargetExecutable))
        {
            details.Add($"Executable: {result.TargetExecutable}");
        }

        details.Add($"PATH updated: {result.PathUpdated}");

        if (!string.IsNullOrWhiteSpace(result.BackupPath))
        {
            details.Add($"Backup: {result.BackupPath}");
        }

        if (!string.IsNullOrWhiteSpace(result.LogPath))
        {
            details.Add($"Log: {result.LogPath}");
        }

        return details;
    }

    private static bool SwitchResultReflected(PathPilotInventorySnapshot snapshot, PathPilotSwitchResult result)
    {
        if (string.IsNullOrWhiteSpace(result.RuntimeId))
        {
            return true;
        }

        var runtime = snapshot.Runtimes.FirstOrDefault(r => string.Equals(r.Id, result.RuntimeId, StringComparison.OrdinalIgnoreCase));
        if (runtime is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(result.InstallationId))
        {
            return runtime.ActiveResolution?.ExecutablePath is not null && string.Equals(runtime.ActiveResolution.ExecutablePath, result.TargetExecutable, StringComparison.OrdinalIgnoreCase);
        }

        return runtime.Installations.Any(install => string.Equals(install.Id, result.InstallationId, StringComparison.OrdinalIgnoreCase) && install.IsActive);
    }

    private static PathPilotInventorySnapshot ApplySwitchResultSnapshot(PathPilotInventorySnapshot snapshot, PathPilotSwitchResult switchResult)
    {
        if (switchResult is null || string.IsNullOrWhiteSpace(switchResult.RuntimeId) || snapshot.Runtimes.Length == 0)
        {
            return snapshot;
        }

        var runtimeIndex = -1;
        for (var i = 0; i < snapshot.Runtimes.Length; i++)
        {
            if (string.Equals(snapshot.Runtimes[i].Id, switchResult.RuntimeId, StringComparison.OrdinalIgnoreCase))
            {
                runtimeIndex = i;
                break;
            }
        }
        if (runtimeIndex < 0)
        {
            return snapshot;
        }

        var runtime = snapshot.Runtimes[runtimeIndex];
        var installations = runtime.Installations;
        if (installations.Length == 0)
        {
            return snapshot;
        }

        PathPilotInstallation? matchedInstallation = null;
        if (!string.IsNullOrWhiteSpace(switchResult.InstallationId))
        {
            matchedInstallation = installations.FirstOrDefault(install => string.Equals(install.Id, switchResult.InstallationId, StringComparison.OrdinalIgnoreCase));
        }

        if (matchedInstallation is null && !string.IsNullOrWhiteSpace(switchResult.TargetExecutable))
        {
            matchedInstallation = installations.FirstOrDefault(install => string.Equals(install.ExecutablePath, switchResult.TargetExecutable, StringComparison.OrdinalIgnoreCase));
        }

        var updatedInstallations = installations;
        if (matchedInstallation is not null)
        {
            updatedInstallations = installations
                .Select(install => install.Id == matchedInstallation.Id
                    ? install with { IsActive = true }
                    : install with { IsActive = false })
                .ToImmutableArray();

            matchedInstallation = updatedInstallations.First(install => install.Id == matchedInstallation.Id);
        }
        else if (installations.Any(install => install.IsActive))
        {
            updatedInstallations = installations
                .Select(install => install with { IsActive = false })
                .ToImmutableArray();
        }

        var activeResolution = new PathPilotActiveResolution(
            switchResult.TargetExecutable,
            string.IsNullOrWhiteSpace(switchResult.TargetDirectory) ? runtime.ActiveResolution?.PathEntry : switchResult.TargetDirectory,
            matchedInstallation is not null,
            matchedInstallation?.Id,
            matchedInstallation?.Source ?? runtime.ActiveResolution?.Source);

        var updatedStatus = runtime.Status with { HasUnknownActive = matchedInstallation is null };

        var updatedRuntime = runtime with
        {
            Installations = updatedInstallations,
            ActiveResolution = activeResolution,
            Status = updatedStatus
        };

        var updatedRuntimes = snapshot.Runtimes.SetItem(runtimeIndex, updatedRuntime);
        return snapshot with { Runtimes = updatedRuntimes };
    }

    private void RefreshPagedRuntimes(bool resetPage, bool raisePageChanged)
    {
        if (resetPage)
        {
            _currentPage = 1;
        }

        var totalPages = TotalPages;
        if (_currentPage > totalPages)
        {
            _currentPage = totalPages;
        }

        var skip = (_currentPage - 1) * _pageSize;
        var pageItems = Runtimes
            .Skip(skip)
            .Take(_pageSize)
            .ToList();

        PagedRuntimes.Clear();
        foreach (var runtime in pageItems)
        {
            PagedRuntimes.Add(runtime);
        }

        RaisePagingProperties();

        if (raisePageChanged)
        {
            PageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RaisePagingProperties()
    {
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(PageDisplay));
        OnPropertyChanged(nameof(CanGoToPreviousPage));
        OnPropertyChanged(nameof(CanGoToNextPage));
        OnPropertyChanged(nameof(HasMultiplePages));
    }

    private static int ComputeTotalPages(int itemCount, int pageSize)
    {
        if (itemCount <= 0)
        {
            return 1;
        }

        var sanitizedPageSize = Math.Max(1, pageSize);
        return (itemCount + sanitizedPageSize - 1) / sanitizedPageSize;
    }

    private static string FormatRelativeTime(DateTimeOffset timestamp)
    {
        var delta = DateTimeOffset.UtcNow - timestamp.ToUniversalTime();
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

    private void OnRuntimeCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_suppressPagingNotifications)
        {
            return;
        }

        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasRuntimeData));
        RefreshPagedRuntimes(resetPage: false, raisePageChanged: true);
    }

    private void OnWarningsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasWarnings));
    }

    private static Task RunOnUiThreadAsync(Action action)
    {
        if (action is null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
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
        Runtimes.CollectionChanged -= OnRuntimeCollectionChanged;
        Warnings.CollectionChanged -= OnWarningsCollectionChanged;
    }

    partial void OnInstallationsDialogRuntimeChanged(PathPilotRuntimeCardViewModel? value)
    {
        OnPropertyChanged(nameof(IsInstallationsDialogOpen));
    }

    partial void OnDetailsDialogRuntimeChanged(PathPilotRuntimeCardViewModel? value)
    {
        OnPropertyChanged(nameof(IsDetailsDialogOpen));
    }

    partial void OnResolutionDialogRuntimeChanged(PathPilotRuntimeCardViewModel? value)
    {
        OnPropertyChanged(nameof(IsResolutionDialogOpen));
    }

    partial void OnShowPathPilotHeroChanged(bool value)
    {
        _preferences.SetShowPathPilotHero(value);
    }
}

public sealed partial class PathPilotRuntimeCardViewModel : ObservableObject
{
    public PathPilotRuntimeCardViewModel(PathPilotRuntime runtime)
    {
        if (runtime is null)
        {
            throw new ArgumentNullException(nameof(runtime));
        }

        RuntimeId = runtime.Id;
        DisplayName = runtime.Name;
        ExecutableName = runtime.ExecutableName;
        DesiredVersion = runtime.DesiredVersion;
        Description = runtime.Description;
        FriendlyName = BuildFriendlyRuntimeName(runtime);
        ActiveExecutablePath = runtime.ActiveResolution?.ExecutablePath ?? string.Empty;
        PathEntry = runtime.ActiveResolution?.PathEntry ?? string.Empty;
        MatchesKnownActive = runtime.ActiveResolution?.MatchesKnownInstallation ?? false;
        Status = runtime.Status;
        Installations = new ObservableCollection<PathPilotInstallationViewModel>(
            runtime.Installations.Select(install => new PathPilotInstallationViewModel(runtime, install)));
        Installations.CollectionChanged += (_, __) =>
        {
            OnPropertyChanged(nameof(HasInstallations));
        };
        ActiveVersionLabel = BuildActiveVersionLabel(runtime, Installations);
        StatusBadges = new ObservableCollection<PathPilotStatusBadgeViewModel>(BuildStatusBadges(runtime));
        ResolutionOrder = runtime.ResolutionOrder.IsDefaultOrEmpty
            ? Array.Empty<string>()
            : runtime.ResolutionOrder.ToArray();
        Summary = BuildSummaryText(runtime, Installations);

    }

    private PathPilotRuntimeStatus Status { get; }

    public string RuntimeId { get; }

    public string DisplayName { get; }

    public string ExecutableName { get; }

    public string FriendlyName { get; }

    public string? DesiredVersion { get; }

    public string? Description { get; }

    public string ActiveExecutablePath { get; }

    public string ActiveVersionLabel { get; }

    public string PathEntry { get; }

    public bool MatchesKnownActive { get; }

    public string Summary { get; }

    public ObservableCollection<PathPilotInstallationViewModel> Installations { get; }

    public ObservableCollection<PathPilotStatusBadgeViewModel> StatusBadges { get; }

    public IReadOnlyList<string> ResolutionOrder { get; }

    public bool IsMissing => Status.IsMissing;

    public bool IsDrifted => Status.IsDrifted;

    public bool HasDuplicates => Status.HasDuplicates;

    public bool HasUnknownActive => Status.HasUnknownActive;

    public bool HasActivePath => !string.IsNullOrWhiteSpace(ActiveExecutablePath);

    public bool HasDesiredVersion => !string.IsNullOrWhiteSpace(DesiredVersion);

    public bool HasActiveVersion => !string.IsNullOrWhiteSpace(ActiveVersionLabel);

    public string VersionChipLabel => HasActiveVersion
        ? $"Active {ActiveVersionLabel}"
        : $"Version {(HasDesiredVersion ? DesiredVersion : "unknown")}";

    public bool HasPathEntry => !string.IsNullOrWhiteSpace(PathEntry);

    public bool HasResolutionOrder => ResolutionOrder.Count > 0;

    public bool HasInstallations => Installations.Count > 0;


    private static IEnumerable<PathPilotStatusBadgeViewModel> BuildStatusBadges(PathPilotRuntime runtime)
    {
        if (runtime.Status.IsMissing)
        {
            yield return new PathPilotStatusBadgeViewModel("Missing", PathPilotStatusSeverity.Danger);
            yield break;
        }

        if (runtime.ActiveResolution?.ExecutablePath is not null)
        {
            if (runtime.Status.HasUnknownActive)
            {
                yield return new PathPilotStatusBadgeViewModel("Unknown PATH winner", PathPilotStatusSeverity.Warning);
            }
            else
            {
                yield return new PathPilotStatusBadgeViewModel("Active", PathPilotStatusSeverity.Success);
            }
        }
        else
        {
            yield return new PathPilotStatusBadgeViewModel("Detected", PathPilotStatusSeverity.Info);
        }

        if (runtime.Status.IsDrifted)
        {
            yield return new PathPilotStatusBadgeViewModel("Drift", PathPilotStatusSeverity.Warning);
        }

        if (runtime.Status.HasDuplicates)
        {
            yield return new PathPilotStatusBadgeViewModel("Duplicates", PathPilotStatusSeverity.Info);
        }
    }

    private static string BuildSummaryText(PathPilotRuntime runtime, IEnumerable<PathPilotInstallationViewModel> installations)
    {
        if (runtime.Status.IsMissing)
        {
            return "No installations detected.";
        }

        var active = installations.FirstOrDefault(install => install.IsActive);
        if (active is not null)
        {
            return $"Active {active.VersionDisplay} • {active.Directory}";
        }

        var count = installations.Count();
        return count switch
        {
            0 => "Inventory available.",
            1 => $"1 installation • {installations.First().Directory}",
            _ => $"{count} installations detected"
        };
    }

    private static string BuildActiveVersionLabel(PathPilotRuntime runtime, IEnumerable<PathPilotInstallationViewModel> installations)
    {
        if (runtime.Status.IsMissing)
        {
            return string.Empty;
        }

        var active = installations.FirstOrDefault(install => install.IsActive);
        if (active is not null)
        {
            return active.VersionDisplay;
        }

        var fallbackVersion = PathPilotVersionHeuristics.InferFromExecutable(runtime.ActiveResolution?.ExecutablePath);
        if (!string.IsNullOrWhiteSpace(fallbackVersion))
        {
            var friendlyName = BuildFriendlyRuntimeName(runtime);
            return string.IsNullOrWhiteSpace(friendlyName)
                ? fallbackVersion
                : $"{friendlyName} {fallbackVersion}";
        }

        return string.Empty;
    }

    internal static string BuildFriendlyRuntimeName(PathPilotRuntime runtime)
    {
        if (runtime is null)
        {
            return "Runtime";
        }

        var fromExecutable = NormalizeExecutableName(runtime.ExecutableName);
        if (!string.IsNullOrWhiteSpace(fromExecutable))
        {
            return fromExecutable!;
        }

        if (!string.IsNullOrWhiteSpace(runtime.Name))
        {
            return runtime.Name.Trim();
        }

        if (!string.IsNullOrWhiteSpace(runtime.Description))
        {
            return runtime.Description.Trim();
        }

        return "Runtime";
    }

    internal static string? NormalizeExecutableName(string? executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(executableName.Trim());
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (name.Equals("pwsh", StringComparison.OrdinalIgnoreCase))
        {
            return "PowerShell";
        }

        if (name.Equals("cmd", StringComparison.OrdinalIgnoreCase))
        {
            return "Command Prompt";
        }

        if (name.StartsWith("python", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = name.Substring("python".Length).Trim();
            return suffix.Length > 0 ? $"Python {suffix}" : "Python";
        }

        if (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return ".NET";
        }

        var normalized = name.Replace('_', ' ').Replace('-', ' ').Trim();
        if (normalized.Length == 0)
        {
            return null;
        }

        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(normalized);
    }
}

public sealed class PathPilotInstallationViewModel
{
    public PathPilotInstallationViewModel(PathPilotRuntime runtime, PathPilotInstallation installation)
    {
        if (runtime is null)
        {
            throw new ArgumentNullException(nameof(runtime));
        }

        if (installation is null)
        {
            throw new ArgumentNullException(nameof(installation));
        }

        RuntimeId = runtime.Id;
        RuntimeName = runtime.Name;
        InstallationId = installation.Id;
        Directory = installation.Directory;
        ExecutablePath = installation.ExecutablePath;
        VersionDisplay = ResolveInstallationLabel(runtime, installation);
        Architecture = string.IsNullOrWhiteSpace(installation.Architecture) ? "unknown" : installation.Architecture.Trim();
        Source = string.IsNullOrWhiteSpace(installation.Source) ? "unknown" : installation.Source.Trim();
        IsActive = installation.IsActive;
        Notes = installation.Notes.IsDefaultOrEmpty
            ? Array.Empty<string>()
            : installation.Notes.Select(note => note.Trim()).Where(note => note.Length > 0).ToArray();

        SwitchRequest = new PathPilotSwitchRequest(RuntimeId, RuntimeName, InstallationId, ExecutablePath);
    }

    public string RuntimeId { get; }

    public string RuntimeName { get; }

    public string InstallationId { get; }

    public string Directory { get; }

    public string ExecutablePath { get; }

    public string VersionDisplay { get; }

    public string Architecture { get; }

    public string Source { get; }

    public bool IsActive { get; }

    public IReadOnlyList<string> Notes { get; }

    public bool HasNotes => Notes.Count > 0;

    public string NotesDisplay => HasNotes ? string.Join("; ", Notes) : string.Empty;

    public string Caption => $"{VersionDisplay} • {Architecture}";

    public PathPilotSwitchRequest SwitchRequest { get; }

    public bool CanSwitch => !string.IsNullOrWhiteSpace(ExecutablePath);

    private static string ResolveInstallationLabel(PathPilotRuntime runtime, PathPilotInstallation installation)
    {
        static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        var friendlyRuntimeName = PathPilotRuntimeCardViewModel.BuildFriendlyRuntimeName(runtime);
        var inferredVersion = Clean(installation.Version) ?? PathPilotVersionHeuristics.InferInstallationVersion(installation);

        if (!string.IsNullOrWhiteSpace(inferredVersion))
        {
            return $"{friendlyRuntimeName} {inferredVersion}";
        }

        return Clean(runtime.Description)
            ?? Clean(runtime.Name)
            ?? Clean(Path.GetFileName(installation.Directory))
            ?? Clean(installation.Directory)
            ?? friendlyRuntimeName
            ?? "Unlabeled installation";
    }

}

public sealed record PathPilotStatusBadgeViewModel(string Label, PathPilotStatusSeverity Severity);

public enum PathPilotStatusSeverity
{
    Success,
    Info,
    Warning,
    Danger
}
