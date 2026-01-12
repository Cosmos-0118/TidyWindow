using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.Services;
using TidyWindow.Core.Startup;

namespace TidyWindow.App.ViewModels;

public sealed partial class StartupEntryItemViewModel : ObservableObject
{
    public StartupEntryItemViewModel(StartupItem item)
    {
        UpdateFrom(item ?? throw new ArgumentNullException(nameof(item)));
    }

    public StartupItem Item { get; private set; } = null!;

    [ObservableProperty]
    private bool isEnabled;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string name = string.Empty;

    [ObservableProperty]
    private string? publisher;

    [ObservableProperty]
    private StartupImpact impact;

    [ObservableProperty]
    private string source = string.Empty;

    [ObservableProperty]
    private string userContext = string.Empty;

    [ObservableProperty]
    private string lastModifiedDisplay = string.Empty;

    [ObservableProperty]
    private bool canDelay;

    [ObservableProperty]
    private bool isAutoGuardEnabled;

    public void UpdateFrom(StartupItem item)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        IsEnabled = item.IsEnabled;
        Name = item.Name;
        Publisher = item.Publisher;
        Impact = item.Impact;
        Source = string.IsNullOrWhiteSpace(item.SourceTag)
            ? item.SourceKind.ToString()
            : item.SourceTag;
        UserContext = string.IsNullOrWhiteSpace(item.UserContext)
            ? "User"
            : item.UserContext;
        LastModifiedDisplay = FormatLastModified(item.LastModifiedUtc);
        CanDelay = ComputeCanDelay(item);
    }

    private static bool ComputeCanDelay(StartupItem item)
    {
        if (item.SourceKind is StartupItemSourceKind.RunKey or StartupItemSourceKind.RunOnce or StartupItemSourceKind.StartupFolder)
        {
            return string.Equals(item.UserContext, "CurrentUser", StringComparison.OrdinalIgnoreCase) || (item.EntryLocation?.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase) == true);
        }

        return false;
    }

    private static string FormatLastModified(DateTimeOffset? timestamp)
    {
        if (timestamp is null)
        {
            return "Modified: unknown";
        }

        var local = timestamp.Value.ToLocalTime();
        return $"Modified: {local:yyyy-MM-dd HH:mm}";
    }
}

public sealed partial class StartupControllerViewModel : ObservableObject
{
    private readonly StartupInventoryService _inventory;
    private readonly StartupControlService _control;
    private readonly StartupDelayService _delay;
    private readonly ActivityLogService _activityLog;
    private readonly UserPreferencesService _preferences;
    private readonly StartupGuardService _guardService;
    private readonly List<StartupEntryItemViewModel> _filteredEntries = new();

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private ObservableCollection<StartupEntryItemViewModel> entries = new();

    public ObservableCollection<StartupEntryItemViewModel> PagedEntries { get; } = new();

    [ObservableProperty]
    private int visibleCount;

    [ObservableProperty]
    private int disabledVisibleCount;

    [ObservableProperty]
    private int enabledVisibleCount;

    [ObservableProperty]
    private int unsignedVisibleCount;

    [ObservableProperty]
    private int highImpactVisibleCount;

    [ObservableProperty]
    private int baselineDisabledCount;

    private readonly int _pageSize = 24;
    private int _currentPage = 1;

    private const int DefaultDelaySeconds = 45;
    private static readonly TimeSpan MinimumBusyDuration = TimeSpan.FromMilliseconds(1000);

    public StartupControllerViewModel(StartupInventoryService inventory, StartupControlService control, StartupDelayService delay, ActivityLogService activityLog, UserPreferencesService preferences, StartupGuardService? guardService = null)
    {
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _activityLog = activityLog ?? throw new ArgumentNullException(nameof(activityLog));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _guardService = guardService ?? new StartupGuardService();
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ToggleCommand = new AsyncRelayCommand<StartupEntryItemViewModel>(ToggleAsync, CanToggle);
        EnableCommand = new AsyncRelayCommand<StartupEntryItemViewModel>(EnableAsync, CanEnable);
        DisableCommand = new AsyncRelayCommand<StartupEntryItemViewModel>(DisableAsync, CanDisable);
        DelayCommand = new AsyncRelayCommand<StartupEntryItemViewModel>(DelayAsync, CanDelay);

        StartupGuardEnabled = _preferences.Current.StartupGuardEnabled;
    }

    [ObservableProperty]
    private bool startupGuardEnabled;

    public int CurrentPage => _currentPage;

    public int TotalPages => ComputeTotalPages(_filteredEntries.Count, _pageSize);

    public string PageDisplay => _filteredEntries.Count == 0
        ? "Page 0 of 0"
        : $"Page {_currentPage} of {TotalPages}";

    public bool CanGoToPreviousPage => _currentPage > 1;

    public bool CanGoToNextPage => _currentPage < TotalPages;

    public bool HasMultiplePages => _filteredEntries.Count > _pageSize;

    public event EventHandler? PageChanged;

    public IAsyncRelayCommand RefreshCommand { get; }

    public IAsyncRelayCommand<StartupEntryItemViewModel> ToggleCommand { get; }

    public IAsyncRelayCommand<StartupEntryItemViewModel> EnableCommand { get; }

    public IAsyncRelayCommand<StartupEntryItemViewModel> DisableCommand { get; }

    public IAsyncRelayCommand<StartupEntryItemViewModel> DelayCommand { get; }

    [RelayCommand]
    private void GoToPreviousPage()
    {
        if (!CanGoToPreviousPage)
        {
            return;
        }

        _currentPage--;
        RefreshPagedEntries(raisePageChanged: true);
    }

    [RelayCommand]
    private void GoToNextPage()
    {
        if (!CanGoToNextPage)
        {
            return;
        }

        _currentPage++;
        RefreshPagedEntries(raisePageChanged: true);
    }

    private bool CanToggle(StartupEntryItemViewModel? item) => item is not null && !IsBusy && !item.IsBusy;

    private bool CanEnable(StartupEntryItemViewModel? item) => item is not null && !IsBusy && !item.IsBusy && !item.IsEnabled;

    private bool CanDisable(StartupEntryItemViewModel? item) => item is not null && !IsBusy && !item.IsBusy && item.IsEnabled;

    partial void OnIsBusyChanged(bool value)
    {
        ToggleCommand.NotifyCanExecuteChanged();
        EnableCommand.NotifyCanExecuteChanged();
        DisableCommand.NotifyCanExecuteChanged();
        DelayCommand.NotifyCanExecuteChanged();
    }

    private async Task RefreshAsync()
    {
        var busyStartedAt = DateTime.UtcNow;
        IsBusy = true;
        try
        {
            await Task.Yield(); // Let the UI paint the busy indicator before heavy work begins.

            var snapshot = await _inventory.GetInventoryAsync();
            var mapped = snapshot.Items.Select(startup => new StartupEntryItemViewModel(startup)).ToList();
            var guarded = _guardService.GetAll();
            foreach (var entry in mapped)
            {
                entry.IsAutoGuardEnabled = guarded.Contains(entry.Item.Id, StringComparer.OrdinalIgnoreCase);
            }

            CacheRunBackups(mapped);
            await AutoReDisableAsync(mapped).ConfigureAwait(true);
            Entries = new ObservableCollection<StartupEntryItemViewModel>(mapped);
            BaselineDisabledCount = mapped.Count(item => !item.IsEnabled);
        }
        finally
        {
            var remaining = MinimumBusyDuration - (DateTime.UtcNow - busyStartedAt);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining); // Keep the busy overlay visible long enough to read.
            }
            IsBusy = false;
        }
    }

    private void CacheRunBackups(IEnumerable<StartupEntryItemViewModel> entries)
    {
        foreach (var entry in entries)
        {
            var item = entry.Item;
            if (item.SourceKind is not (StartupItemSourceKind.RunKey or StartupItemSourceKind.RunOnce))
            {
                continue;
            }

            if (_control.BackupStore.Get(item.Id) is not null)
            {
                continue; // Backup already exists (likely from a prior disable action).
            }

            if (!TryParseRegistryLocation(item.EntryLocation, out var root, out var subKey))
            {
                continue;
            }

            var valueName = ExtractValueName(item);
            var data = item.RawCommand ?? BuildCommand(item.ExecutablePath, item.Arguments);

            if (string.IsNullOrWhiteSpace(valueName) || string.IsNullOrWhiteSpace(data))
            {
                continue;
            }

            var backup = new StartupEntryBackup(
                item.Id,
                item.SourceKind,
                root,
                subKey,
                valueName,
                data,
                FileOriginalPath: null,
                FileBackupPath: null,
                TaskPath: null,
                TaskEnabled: null,
                ServiceName: null,
                ServiceStartValue: null,
                ServiceDelayedAutoStart: null,
                CreatedAtUtc: DateTimeOffset.UtcNow);

            _control.BackupStore.Save(backup);
        }
    }

    private static bool TryParseRegistryLocation(string? location, out string rootName, out string subKey)
    {
        rootName = string.Empty;
        subKey = string.Empty;

        if (string.IsNullOrWhiteSpace(location))
        {
            return false;
        }

        var parts = location.Split(new[] { '\\' }, 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        rootName = parts[0].ToUpperInvariant() switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => "HKCU",
            "HKLM" or "HKEY_LOCAL_MACHINE" => "HKLM",
            _ => parts[0]
        };

        subKey = parts[1];
        return true;
    }

    private static string ExtractValueName(StartupItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Id) && item.Id.Contains(':', StringComparison.Ordinal))
        {
            return item.Id[(item.Id.LastIndexOf(':') + 1)..];
        }

        if (!string.IsNullOrWhiteSpace(item.Name))
        {
            return item.Name;
        }

        return "StartupItem";
    }

    private static string BuildCommand(string executablePath, string? arguments)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(arguments))
        {
            return executablePath;
        }

        return executablePath.Contains(' ', StringComparison.Ordinal)
            ? $"\"{executablePath}\" {arguments}"
            : $"{executablePath} {arguments}";
    }

    private async Task ToggleAsync(StartupEntryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        item.IsBusy = true;
        try
        {
            StartupToggleResult result;
            if (item.IsEnabled)
            {
                result = await _control.DisableAsync(item.Item);
            }
            else
            {
                result = await _control.EnableAsync(item.Item);
            }

            if (result.Succeeded)
            {
                item.UpdateFrom(result.Item);
                _activityLog.LogSuccess(
                    "StartupController",
                    BuildActionMessage(result.Item, result.Item.IsEnabled ? "Enabled" : "Disabled"),
                    BuildUserFacingDetails(result));
                RefreshAfterStateChange();
            }
            else
            {
                _activityLog.LogWarning(
                    "StartupController",
                    $"Failed to toggle {item.Name}: {result.ErrorMessage}",
                    BuildToggleDetails(result));
            }
        }
        catch (Exception ex)
        {
            _activityLog.LogError(
                "StartupController",
                $"Error toggling {item?.Name}: {ex.Message}",
                BuildErrorDetails(item?.Item, ex));
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private async Task EnableAsync(StartupEntryItemViewModel? item)
    {
        if (item is null || item.IsEnabled)
        {
            return;
        }

        item.IsBusy = true;
        try
        {
            var result = await _control.EnableAsync(item.Item);
            if (result.Succeeded)
            {
                item.UpdateFrom(result.Item);
                _activityLog.LogSuccess(
                    "StartupController",
                    BuildActionMessage(result.Item, "Enabled"),
                    BuildUserFacingDetails(result));
                RefreshAfterStateChange();
            }
            else
            {
                _activityLog.LogWarning(
                    "StartupController",
                    $"Failed to enable {item.Name}: {result.ErrorMessage}",
                    BuildToggleDetails(result));
            }
        }
        catch (Exception ex)
        {
            _activityLog.LogError(
                "StartupController",
                $"Error enabling {item.Name}: {ex.Message}",
                BuildErrorDetails(item.Item, ex));
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private async Task DisableAsync(StartupEntryItemViewModel? item)
    {
        if (item is null || !item.IsEnabled)
        {
            return;
        }

        item.IsBusy = true;
        try
        {
            var result = await _control.DisableAsync(item.Item);
            if (result.Succeeded)
            {
                item.UpdateFrom(result.Item);
                _activityLog.LogSuccess(
                    "StartupController",
                    BuildActionMessage(result.Item, "Disabled"),
                    BuildUserFacingDetails(result));
                RefreshAfterStateChange();
            }
            else
            {
                _activityLog.LogWarning(
                    "StartupController",
                    $"Failed to disable {item.Name}: {result.ErrorMessage}",
                    BuildToggleDetails(result));
            }
        }
        catch (Exception ex)
        {
            _activityLog.LogError(
                "StartupController",
                $"Error disabling {item.Name}: {ex.Message}",
                BuildErrorDetails(item.Item, ex));
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private void RefreshAfterStateChange()
    {
        RefreshVisibleCounters();
        RefreshCommandStates();
        RefreshPagedEntries(raisePageChanged: false);
    }

    private static string BuildActionMessage(StartupItem item, string action)
    {
        var name = string.IsNullOrWhiteSpace(item.Name) ? "(unnamed)" : item.Name;
        var source = string.IsNullOrWhiteSpace(item.SourceTag) ? item.SourceKind.ToString() : item.SourceTag;
        var context = string.IsNullOrWhiteSpace(item.UserContext) ? "User" : item.UserContext;
        var location = string.IsNullOrWhiteSpace(item.EntryLocation) ? "(no location)" : item.EntryLocation;

        return $"{action} {name}\nSource: {source} • {context}\nLocation: {location}";
    }

    private static IEnumerable<object?> BuildUserFacingDetails(StartupToggleResult result)
    {
        var item = result.Item;
        var source = string.IsNullOrWhiteSpace(item.SourceTag) ? item.SourceKind.ToString() : item.SourceTag;
        var context = string.IsNullOrWhiteSpace(item.UserContext) ? "User" : item.UserContext;
        var location = string.IsNullOrWhiteSpace(item.EntryLocation) ? "(no location)" : item.EntryLocation;
        var command = item.RawCommand ?? BuildCommand(item.ExecutablePath, item.Arguments);

        yield return $"Action: {(item.IsEnabled ? "Enabled" : "Disabled")}";
        yield return $"Name: {(!string.IsNullOrWhiteSpace(item.Name) ? item.Name : "(unnamed)")}";
        yield return $"Source: {source}";
        yield return $"User: {context}";
        yield return $"Location: {location}";
        if (!string.IsNullOrWhiteSpace(command))
        {
            yield return $"Command: {command}";
        }

        foreach (var detail in BuildToggleDetails(result))
        {
            yield return detail;
        }
    }

    private bool CanDelay(StartupEntryItemViewModel? item) => item is not null && item.CanDelay && !IsBusy && !item.IsBusy;

    partial void OnStartupGuardEnabledChanged(bool value)
    {
        _preferences.SetStartupGuardEnabled(value);
    }

    private async Task DelayAsync(StartupEntryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        item.IsBusy = true;
        try
        {
            var delaySeconds = DefaultDelaySeconds;
            var result = await _delay.DelayAsync(item.Item, TimeSpan.FromSeconds(delaySeconds));
            if (result.Succeeded)
            {
                _activityLog.LogSuccess("StartupController", $"Delayed {item.Name} by {delaySeconds}s", new object?[] { result.ReplacementTaskPath });
                await RefreshAsync();
            }
            else
            {
                _activityLog.LogWarning("StartupController", $"Failed to delay {item.Name}: {result.ErrorMessage}", new object?[] { item.Item.Id, item.Item.SourceKind, item.Item.EntryLocation, result.ErrorMessage, result.ReplacementTaskPath });
            }
        }
        catch (Exception ex)
        {
            _activityLog.LogError("StartupController", $"Error delaying {item.Name}: {ex.Message}", BuildErrorDetails(item.Item, ex));
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private static IEnumerable<object?> BuildToggleDetails(StartupToggleResult result)
    {
        var item = result.Item;
        yield return new
        {
            item.Id,
            item.Name,
            item.SourceKind,
            item.SourceTag,
            item.EntryLocation,
            item.ExecutablePath,
            item.RawCommand,
            item.Arguments,
            item.UserContext,
            item.IsEnabled,
            BackupCreatedUtc = result.Backup?.CreatedAtUtc,
            BackupRegistry = result.Backup is null ? null : new { result.Backup.RegistryRoot, result.Backup.RegistrySubKey, result.Backup.RegistryValueName },
            BackupTask = result.Backup?.TaskPath,
            BackupService = result.Backup?.ServiceName,
            BackupFile = result.Backup?.FileOriginalPath,
            result.ErrorMessage
        };
    }

    private static IEnumerable<object?> BuildErrorDetails(StartupItem? item, Exception ex)
    {
        if (item is not null)
        {
            yield return new
            {
                item.Id,
                item.Name,
                item.SourceKind,
                item.SourceTag,
                item.EntryLocation,
                item.ExecutablePath,
                item.RawCommand,
                item.Arguments,
                item.UserContext,
                item.IsEnabled
            };
        }

        yield return ex;
    }

    public async Task SetGuardAsync(StartupEntryItemViewModel entry, bool enabled)
    {
        if (entry is null)
        {
            return;
        }

        _guardService.SetGuard(entry.Item.Id, enabled);
        entry.IsAutoGuardEnabled = enabled;

        if (enabled && entry.IsEnabled)
        {
            await DisableAsync(entry);
        }
    }

    private async Task AutoReDisableAsync(IReadOnlyCollection<StartupEntryItemViewModel> mapped)
    {
        var guards = _guardService.GetAll();
        if (guards.Count == 0)
        {
            return;
        }

        foreach (var entry in mapped)
        {
            var guarded = guards.Contains(entry.Item.Id, StringComparer.OrdinalIgnoreCase);
            if (!guarded || !entry.IsEnabled)
            {
                continue;
            }

            try
            {
                var result = await _control.DisableAsync(entry.Item).ConfigureAwait(true);
                if (result.Succeeded)
                {
                    entry.UpdateFrom(result.Item);
                    _activityLog.LogWarning(
                        "StartupController",
                        $"Re-disabled {entry.Name} because it was re-enabled externally.",
                        BuildToggleDetails(result));
                }
                else
                {
                    _activityLog.LogWarning(
                        "StartupController",
                        $"Attempted to re-disable {entry.Name} but failed: {result.ErrorMessage}",
                        BuildToggleDetails(result));
                }
            }
            catch (Exception ex)
            {
                _activityLog.LogError(
                    "StartupController",
                    $"Error re-disabling {entry.Name}: {ex.Message}",
                    BuildErrorDetails(entry.Item, ex));
            }
        }
    }

    public void ApplyVisibleEntries(IReadOnlyList<StartupEntryItemViewModel> visibleEntries, bool resetPage)
    {
        _filteredEntries.Clear();

        if (visibleEntries is not null)
        {
            _filteredEntries.AddRange(visibleEntries);
        }

        if (resetPage)
        {
            ResetToFirstPage();
        }

        RefreshPagedEntries(raisePageChanged: resetPage);
        RefreshVisibleCounters();
        RefreshCommandStates();
    }

    public void RefreshVisibleCounters()
    {
        UpdateCounters(_filteredEntries);
    }

    public void RefreshCommandStates()
    {
        ToggleCommand.NotifyCanExecuteChanged();
        EnableCommand.NotifyCanExecuteChanged();
        DisableCommand.NotifyCanExecuteChanged();
        DelayCommand.NotifyCanExecuteChanged();
    }

    private void RefreshPagedEntries(bool raisePageChanged)
    {
        var totalPages = TotalPages;
        if (_currentPage > totalPages)
        {
            _currentPage = totalPages;
        }

        var skip = (_currentPage - 1) * _pageSize;
        var pageItems = _filteredEntries
            .Skip(skip)
            .Take(_pageSize)
            .ToList();

        PagedEntries.Clear();
        foreach (var item in pageItems)
        {
            PagedEntries.Add(item);
        }

        RaisePagingProperties();

        if (raisePageChanged)
        {
            PageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdateCounters(IReadOnlyList<StartupEntryItemViewModel> items)
    {
        var list = items ?? Array.Empty<StartupEntryItemViewModel>();

        VisibleCount = list.Count;
        DisabledVisibleCount = list.Count(item => !item.IsEnabled);
        EnabledVisibleCount = list.Count(item => item.IsEnabled);
        UnsignedVisibleCount = list.Count(item => item.Item.SignatureStatus == StartupSignatureStatus.Unsigned);
        HighImpactVisibleCount = list.Count(item => item.Impact == StartupImpact.High);
    }

    private void ResetToFirstPage()
    {
        _currentPage = 1;
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
}
