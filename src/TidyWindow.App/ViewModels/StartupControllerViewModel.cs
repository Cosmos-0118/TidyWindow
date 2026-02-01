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

    /// <summary>
    /// Creates a backup-only entry from a backup record.
    /// Used when the startup item no longer exists in the live inventory.
    /// </summary>
    public StartupEntryItemViewModel(StartupEntryBackup backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        IsBackupOnly = true;
        BackupRecord = backup;

        var syntheticItem = new StartupItem(
            Id: backup.Id,
            Name: backup.RegistryValueName ?? backup.ServiceName ?? backup.TaskPath ?? "Unknown",
            ExecutablePath: backup.FileOriginalPath ?? backup.RegistryValueData ?? string.Empty,
            SourceKind: backup.SourceKind,
            SourceTag: $"Backup ({backup.SourceKind})",
            Arguments: null,
            RawCommand: backup.RegistryValueData,
            IsEnabled: false,
            EntryLocation: BuildEntryLocation(backup),
            Publisher: null,
            SignatureStatus: StartupSignatureStatus.Unknown,
            Impact: StartupImpact.Unknown,
            FileSizeBytes: null,
            LastModifiedUtc: backup.CreatedAtUtc,
            UserContext: null);

        UpdateFrom(syntheticItem);
    }

    private static string? BuildEntryLocation(StartupEntryBackup backup)
    {
        // For Run keys: combine registry root and subkey
        if (!string.IsNullOrWhiteSpace(backup.RegistryRoot) && !string.IsNullOrWhiteSpace(backup.RegistrySubKey))
        {
            return $"{backup.RegistryRoot}\\{backup.RegistrySubKey}";
        }

        // For StartupFolder entries: RegistrySubKey contains the folder path, use FileOriginalPath or RegistrySubKey
        if (backup.SourceKind == StartupItemSourceKind.StartupFolder)
        {
            return backup.FileOriginalPath ?? backup.RegistrySubKey;
        }

        // For other types (Tasks, Services, etc.)
        return backup.TaskPath ?? backup.ServiceName ?? backup.RegistrySubKey;
    }

    public StartupItem Item { get; private set; } = null!;

    /// <summary>
    /// True if this entry exists only in the backup store (not found in live inventory).
    /// </summary>
    [ObservableProperty]
    private bool isBackupOnly;

    /// <summary>
    /// The backup record for backup-only entries.
    /// </summary>
    public StartupEntryBackup? BackupRecord { get; private set; }

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

    [ObservableProperty]
    private bool isSystemCritical;

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
        IsSystemCritical = StartupSafetyClassifier.IsSystemCritical(item);
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
    private readonly IUserConfirmationService _confirmationService;
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

    [ObservableProperty]
    private int backupOnlyCount;

    /// <summary>
    /// Number of entries that re-enabled themselves since the last refresh.
    /// </summary>
    [ObservableProperty]
    private int reenabledAlertCount;

    /// <summary>
    /// Message to display in the re-enable alert bar.
    /// </summary>
    [ObservableProperty]
    private string reenabledAlertMessage = string.Empty;

    /// <summary>
    /// Whether the re-enable alert bar should be visible.
    /// </summary>
    [ObservableProperty]
    private bool showReenabledAlert;

    /// <summary>
    /// Entries that exist only in the backup store (not found in live inventory).
    /// These are merged into <see cref="Entries"/> after refresh.
    /// </summary>
    public ObservableCollection<StartupEntryItemViewModel> BackupOnlyEntries { get; } = new();

    private readonly int _pageSize = 24;
    private int _currentPage = 1;

    private const int DefaultDelaySeconds = 45;
    private static readonly TimeSpan MinimumBusyDuration = TimeSpan.FromMilliseconds(1000);

    public StartupControllerViewModel(
        StartupInventoryService inventory,
        StartupControlService control,
        StartupDelayService delay,
        ActivityLogService activityLog,
        UserPreferencesService preferences,
        IUserConfirmationService confirmationService,
        StartupGuardService? guardService = null)
    {
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _activityLog = activityLog ?? throw new ArgumentNullException(nameof(activityLog));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _confirmationService = confirmationService ?? throw new ArgumentNullException(nameof(confirmationService));
        _guardService = guardService ?? new StartupGuardService();
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ToggleCommand = new AsyncRelayCommand<StartupEntryItemViewModel>(ToggleAsync, CanToggle);
        EnableCommand = new AsyncRelayCommand<StartupEntryItemViewModel>(EnableAsync, CanEnable);
        DisableCommand = new AsyncRelayCommand<StartupEntryItemViewModel>(DisableAsync, CanDisable);
        DisableAndStopCommand = new AsyncRelayCommand<StartupEntryItemViewModel>(DisableAndStopAsync, CanDisable);
        DelayCommand = new AsyncRelayCommand<StartupEntryItemViewModel>(DelayAsync, CanDelay);
        RestoreFromBackupCommand = new AsyncRelayCommand<StartupEntryItemViewModel>(RestoreFromBackupAsync, CanRestoreFromBackup);
        DeleteBackupCommand = new AsyncRelayCommand<StartupEntryItemViewModel>(DeleteBackupAsync, CanDeleteBackup);

        StartupGuardEnabled = _preferences.Current.StartupGuardEnabled;
        ShowStartupHero = _preferences.Current.ShowStartupHero;
    }

    [ObservableProperty]
    private bool startupGuardEnabled;

    [ObservableProperty]
    private bool showStartupHero = true;

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

    public IAsyncRelayCommand<StartupEntryItemViewModel> DisableAndStopCommand { get; }

    public IAsyncRelayCommand<StartupEntryItemViewModel> DelayCommand { get; }

    public IAsyncRelayCommand<StartupEntryItemViewModel> RestoreFromBackupCommand { get; }

    public IAsyncRelayCommand<StartupEntryItemViewModel> DeleteBackupCommand { get; }

    [RelayCommand]
    private void DismissReenabledAlert()
    {
        ShowReenabledAlert = false;
        ReenabledAlertCount = 0;
        ReenabledAlertMessage = string.Empty;
    }

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

    private bool CanToggle(StartupEntryItemViewModel? item) => item is not null && !IsBusy && !item.IsBusy && !item.IsBackupOnly;

    private bool CanEnable(StartupEntryItemViewModel? item) => item is not null && !IsBusy && !item.IsBusy && !item.IsEnabled && !item.IsBackupOnly;

    private bool CanDisable(StartupEntryItemViewModel? item) => item is not null && !IsBusy && !item.IsBusy && item.IsEnabled && !item.IsBackupOnly;

    private bool CanRestoreFromBackup(StartupEntryItemViewModel? item) => item is not null && !IsBusy && !item.IsBusy && item.IsBackupOnly && item.BackupRecord is not null;

    partial void OnIsBusyChanged(bool value)
    {
        ToggleCommand.NotifyCanExecuteChanged();
        EnableCommand.NotifyCanExecuteChanged();
        DisableCommand.NotifyCanExecuteChanged();
        DisableAndStopCommand.NotifyCanExecuteChanged();
        DelayCommand.NotifyCanExecuteChanged();
        RestoreFromBackupCommand.NotifyCanExecuteChanged();
        DeleteBackupCommand.NotifyCanExecuteChanged();
    }

    private async Task RefreshAsync()
    {
        var busyStartedAt = DateTime.UtcNow;
        IsBusy = true;
        try
        {
            await Task.Yield(); // Let the UI paint the busy indicator before heavy work begins.

            var (mapped, baselineDisabled, backupOnly, reenabledInfo) = await Task.Run(async () =>
            {
                var snapshot = await _inventory.GetInventoryAsync().ConfigureAwait(false);
                var mappedEntries = snapshot.Items.Select(startup => new StartupEntryItemViewModel(startup)).ToList();
                var guarded = _guardService.GetAll();
                foreach (var entry in mappedEntries)
                {
                    entry.IsAutoGuardEnabled = guarded.Contains(entry.Item.Id, StringComparer.OrdinalIgnoreCase);
                }

                CacheRunBackups(mappedEntries);
                await AutoReDisableAsync(mappedEntries).ConfigureAwait(false);

                var disabledBaseline = mappedEntries.Count(item => !item.IsEnabled);

                // Clean up stale/ghost backups (files deleted externally)
                _control.BackupStore.CleanupStaleBackups();

                // Detect and warn about apps that re-enabled themselves
                var liveIds = new HashSet<string>(mappedEntries.Select(e => e.Item.Id), StringComparer.OrdinalIgnoreCase);
                var reenabled = DetectReenabledEntries(mappedEntries, liveIds);

                var seenBackupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var backupOnlyEntries = _control.BackupStore.GetAll()
                    .Where(backup => StartupBackupStore.IsValidBackup(backup))
                    .Where(backup => !liveIds.Contains(backup.Id))
                    .Where(backup => seenBackupIds.Add(backup.Id)) // Deduplication
                    .Select(backup => new StartupEntryItemViewModel(backup))
                    .ToList();

                return (mappedEntries, disabledBaseline, backupOnlyEntries, reenabled);
            }).ConfigureAwait(true);

            Entries = new ObservableCollection<StartupEntryItemViewModel>(mapped);
            BaselineDisabledCount = baselineDisabled;

            BackupOnlyEntries.Clear();
            foreach (var backupEntry in backupOnly)
            {
                BackupOnlyEntries.Add(backupEntry);
            }
            BackupOnlyCount = backupOnly.Count;

            // Update the re-enabled alert if any non-guarded entries re-enabled themselves
            if (reenabledInfo.Count > 0)
            {
                ReenabledAlertCount = reenabledInfo.Count;
                if (reenabledInfo.Count == 1)
                {
                    ReenabledAlertMessage = $"'{reenabledInfo.Names[0]}' re-enabled itself after you disabled it. Consider enabling Auto-Guard for this entry.";
                }
                else
                {
                    var displayNames = string.Join(", ", reenabledInfo.Names.Take(3));
                    if (reenabledInfo.Names.Count > 3)
                    {
                        displayNames += $" and {reenabledInfo.Names.Count - 3} more";
                    }
                    ReenabledAlertMessage = $"{reenabledInfo.Count} startup entries re-enabled themselves: {displayNames}. Consider enabling Auto-Guard.";
                }
                ShowReenabledAlert = true;
            }
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
        // Note: We only cache backup data for DISABLED entries.
        // This backup data is needed to restore the registry value when re-enabling.
        // We do NOT create backups for ENABLED entries - that would cause false
        // "re-enabled itself" warnings because DetectReenabledEntries checks if
        // a backup exists AND the entry is enabled.
        foreach (var entry in entries)
        {
            var item = entry.Item;
            if (item.SourceKind is not (StartupItemSourceKind.RunKey or StartupItemSourceKind.RunOnce))
            {
                continue;
            }

            // Only cache for disabled entries that don't already have a backup
            if (item.IsEnabled)
            {
                continue; // Skip enabled entries - backup only needed for disabled ones
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

    /// <summary>
    /// Detects and handles entries where the application re-created its startup after we disabled it.
    /// For guarded entries: automatically re-disables and logs a warning.
    /// For non-guarded entries: logs a warning to alert the user.
    /// Does NOT silently delete backups - the user should be aware and decide.
    /// Returns a tuple of (nonGuardedCount, nonGuardedNames) for UI notification.
    /// </summary>
    private (int Count, List<string> Names) DetectReenabledEntries(IReadOnlyCollection<StartupEntryItemViewModel> mappedEntries, HashSet<string> liveIds)
    {
        var allBackups = _control.BackupStore.GetAll();
        var guards = _guardService.GetAll();
        var nonGuardedReenabledNames = new List<string>();

        foreach (var backup in allBackups)
        {
            if (!liveIds.Contains(backup.Id))
            {
                continue; // Backup is still valid (entry not in live inventory)
            }

            // Find the live entry that matches this backup
            var liveEntry = mappedEntries.FirstOrDefault(e =>
                string.Equals(e.Item.Id, backup.Id, StringComparison.OrdinalIgnoreCase));

            if (liveEntry is null || !liveEntry.IsEnabled)
            {
                continue; // Entry is disabled, no conflict
            }

            // The app has re-created its startup entry!
            var entryName = backup.RegistryValueName ?? backup.ServiceName ?? backup.TaskPath ?? backup.Id;
            var isGuarded = guards.Contains(backup.Id, StringComparer.OrdinalIgnoreCase);

            if (isGuarded)
            {
                // Auto-Guard will handle re-disabling via AutoReDisableAsync
                // Just log a prominent warning
                _activityLog.LogWarning(
                    "StartupController",
                    $"⚠️ '{entryName}' re-enabled itself! Auto-Guard will re-disable it.",
                    new object[]
                    {
                        $"The application re-created its startup entry after you disabled it.",
                        $"Because Auto-Guard is enabled, TidyWindow will disable it again.",
                        $"ID: {backup.Id}",
                        $"Source: {backup.SourceKind}"
                    });
            }
            else
            {
                // NOT guarded - warn the user prominently and track for UI alert
                nonGuardedReenabledNames.Add(entryName);
                _activityLog.LogWarning(
                    "StartupController",
                    $"⚠️ '{entryName}' re-enabled itself without your permission!",
                    new object[]
                    {
                        $"The application re-created its startup entry after you disabled it.",
                        $"This is often done by apps that want to run at startup regardless of your preference.",
                        $"Consider enabling 'Auto-keep disabled' (Auto-Guard) to prevent this.",
                        $"ID: {backup.Id}",
                        $"Source: {backup.SourceKind}",
                        $"You can find this entry and disable it again from the Startup Controller."
                    });
            }

            // Clean up the OLD backup file (if any) since app created a NEW file
            // But keep the backup RECORD so we have history
            if (backup.SourceKind == StartupItemSourceKind.StartupFolder &&
                !string.IsNullOrWhiteSpace(backup.FileBackupPath) &&
                System.IO.File.Exists(backup.FileBackupPath))
            {
                try
                {
                    System.IO.File.Delete(backup.FileBackupPath);
                }
                catch
                {
                    // Non-fatal
                }
            }

            // Remove the backup record ONLY after handling
            // For guarded items, AutoReDisableAsync will create a new backup
            // For non-guarded items, we remove the obsolete record
            _control.BackupStore.Remove(backup.Id);
        }

        return (nonGuardedReenabledNames.Count, nonGuardedReenabledNames);
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
                if (!ConfirmDisableIfSystemCritical(item.Item))
                {
                    return;
                }
                result = await Task.Run(async () => await _control.DisableAsync(item.Item).ConfigureAwait(false)).ConfigureAwait(true);
            }
            else
            {
                result = await Task.Run(async () => await _control.EnableAsync(item.Item).ConfigureAwait(false)).ConfigureAwait(true);
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
            var result = await Task.Run(async () => await _control.EnableAsync(item.Item).ConfigureAwait(false)).ConfigureAwait(true);
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
        await DisableCoreAsync(item, terminateRunningProcesses: false).ConfigureAwait(true);
    }

    private async Task DisableAndStopAsync(StartupEntryItemViewModel? item)
    {
        await DisableCoreAsync(item, terminateRunningProcesses: true).ConfigureAwait(true);
    }

    private async Task DisableCoreAsync(StartupEntryItemViewModel? item, bool terminateRunningProcesses)
    {
        if (item is null || !item.IsEnabled)
        {
            return;
        }

        if (!ConfirmDisableIfSystemCritical(item.Item))
        {
            return;
        }

        item.IsBusy = true;
        try
        {
            var result = await Task.Run(async () => await _control.DisableAsync(item.Item, terminateRunningProcesses).ConfigureAwait(false)).ConfigureAwait(true);
            if (result.Succeeded)
            {
                item.UpdateFrom(result.Item);
                var action = terminateRunningProcesses ? "Disabled and stopped" : "Disabled";
                _activityLog.LogSuccess(
                    "StartupController",
                    BuildActionMessage(result.Item, action),
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

    private async Task RestoreFromBackupAsync(StartupEntryItemViewModel? item)
    {
        if (item is null || !item.IsBackupOnly || item.BackupRecord is null)
        {
            return;
        }

        item.IsBusy = true;
        try
        {
            var backup = item.BackupRecord;
            var syntheticItem = item.Item;

            var result = await Task.Run(async () => await _control.EnableAsync(syntheticItem).ConfigureAwait(false)).ConfigureAwait(true);

            if (result.Succeeded)
            {
                _activityLog.LogSuccess(
                    "StartupController",
                    $"Restored startup entry from backup: {item.Name}",
                    BuildRestoreDetails(backup));

                // Remove from backup-only list and trigger full refresh
                BackupOnlyEntries.Remove(item);
                BackupOnlyCount = BackupOnlyEntries.Count;
                await RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
            }
            else
            {
                _activityLog.LogWarning(
                    "StartupController",
                    $"Failed to restore {item.Name} from backup: {result.ErrorMessage}",
                    BuildRestoreDetails(backup));
            }
        }
        catch (Exception ex)
        {
            _activityLog.LogError(
                "StartupController",
                $"Error restoring {item.Name} from backup: {ex.Message}",
                BuildRestoreErrorDetails(item.BackupRecord, ex));
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private static IEnumerable<object?> BuildRestoreDetails(StartupEntryBackup backup)
    {
        yield return $"Source kind: {backup.SourceKind}";
        if (!string.IsNullOrWhiteSpace(backup.RegistryRoot))
        {
            yield return $"Registry: {backup.RegistryRoot}\\{backup.RegistrySubKey}";
        }
        if (!string.IsNullOrWhiteSpace(backup.RegistryValueName))
        {
            yield return $"Value name: {backup.RegistryValueName}";
        }
        if (!string.IsNullOrWhiteSpace(backup.RegistryValueData))
        {
            yield return $"Command: {backup.RegistryValueData}";
        }
        if (!string.IsNullOrWhiteSpace(backup.TaskPath))
        {
            yield return $"Task: {backup.TaskPath}";
        }
        if (!string.IsNullOrWhiteSpace(backup.ServiceName))
        {
            yield return $"Service: {backup.ServiceName}";
        }
        yield return $"Backup created: {backup.CreatedAtUtc:u}";
    }

    private static IEnumerable<object?> BuildRestoreErrorDetails(StartupEntryBackup? backup, Exception ex)
    {
        if (backup is not null)
        {
            foreach (var detail in BuildRestoreDetails(backup))
            {
                yield return detail;
            }
        }
        yield return $"Error: {ex.Message}";
        yield return $"Stack trace: {ex.StackTrace}";
    }

    private static bool CanDeleteBackup(StartupEntryItemViewModel? item) => item?.IsBackupOnly == true && item.BackupRecord is not null;

    private async Task DeleteBackupAsync(StartupEntryItemViewModel? item)
    {
        if (item is null || !item.IsBackupOnly || item.BackupRecord is null)
        {
            return;
        }

        var backup = item.BackupRecord;

        // Build a detailed warning message
        var hasBackupFile = !string.IsNullOrWhiteSpace(backup.FileBackupPath) && System.IO.File.Exists(backup.FileBackupPath);
        var warningMessage = BuildDeleteWarningMessage(item, backup, hasBackupFile);

        if (!_confirmationService.Confirm("Permanently delete backup?", warningMessage))
        {
            return;
        }

        item.IsBusy = true;
        try
        {
            await Task.Run(() =>
            {
                // Delete the backup file if it exists
                if (hasBackupFile)
                {
                    try
                    {
                        System.IO.File.Delete(backup.FileBackupPath!);
                    }
                    catch
                    {
                        // Non-fatal: file cleanup failed
                    }
                }

                // Remove from backup store
                _control.BackupStore.Remove(backup.Id);
            }).ConfigureAwait(true);

            _activityLog.LogSuccess(
                "StartupController",
                $"Permanently deleted backup: {item.Name}",
                BuildDeleteDetails(backup, hasBackupFile));

            // Remove from backup-only list
            BackupOnlyEntries.Remove(item);
            BackupOnlyCount = BackupOnlyEntries.Count;

            // Remove from visible entries if currently shown
            if (_filteredEntries.Contains(item))
            {
                _filteredEntries.Remove(item);
                RefreshPagedEntries(raisePageChanged: false);
            }
        }
        catch (Exception ex)
        {
            _activityLog.LogError(
                "StartupController",
                $"Error deleting backup for {item.Name}: {ex.Message}",
                BuildDeleteDetails(backup, hasBackupFile));
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private static string BuildDeleteWarningMessage(StartupEntryItemViewModel item, StartupEntryBackup backup, bool hasBackupFile)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("⚠️ This action is PERMANENT and cannot be undone.");
        sb.AppendLine();
        sb.AppendLine($"Entry: {item.Name}");
        sb.AppendLine($"Type: {backup.SourceKind}");

        if (!string.IsNullOrWhiteSpace(backup.RegistryValueData))
        {
            sb.AppendLine($"Command: {backup.RegistryValueData}");
        }
        else if (!string.IsNullOrWhiteSpace(backup.FileOriginalPath))
        {
            sb.AppendLine($"Original path: {backup.FileOriginalPath}");
        }

        sb.AppendLine($"Backup created: {backup.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
        sb.AppendLine();

        if (hasBackupFile)
        {
            sb.AppendLine("🗑️ The backed-up startup file will be permanently deleted.");
            sb.AppendLine();
        }

        sb.AppendLine("After deletion, you will NOT be able to restore this startup entry.");
        sb.AppendLine("You would need to reinstall the application to get it back.");
        sb.AppendLine();
        sb.AppendLine("Are you sure you want to continue?");

        return sb.ToString();
    }

    private static IEnumerable<object?> BuildDeleteDetails(StartupEntryBackup backup, bool hadBackupFile)
    {
        yield return $"ID: {backup.Id}";
        yield return $"Source kind: {backup.SourceKind}";
        if (!string.IsNullOrWhiteSpace(backup.RegistryValueName))
        {
            yield return $"Value name: {backup.RegistryValueName}";
        }
        if (!string.IsNullOrWhiteSpace(backup.FileOriginalPath))
        {
            yield return $"Original path: {backup.FileOriginalPath}";
        }
        if (hadBackupFile)
        {
            yield return $"Backup file deleted: {backup.FileBackupPath}";
        }
        yield return $"Backup was created: {backup.CreatedAtUtc:u}";
    }

    private bool ConfirmDisableIfSystemCritical(StartupItem item)
    {
        if (!StartupSafetyClassifier.IsSystemCritical(item))
        {
            return true;
        }

        var title = "System startup item";
        var message =
            $"You are about to disable a startup item that looks system-critical.\n\n" +
            $"Name: {item.Name}\n" +
            $"Publisher: {item.Publisher ?? "Unknown"}\n" +
            $"Source: {item.SourceKind} ({item.SourceTag})\n" +
            $"Path: {item.ExecutablePath}\n\n" +
            "Are you absolutely sure you want to continue?";

        return _confirmationService.Confirm(title, message);
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

    partial void OnShowStartupHeroChanged(bool value)
    {
        _preferences.SetShowStartupHero(value);
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
            var result = await Task.Run(async () => await _delay.DelayAsync(item.Item, TimeSpan.FromSeconds(delaySeconds)).ConfigureAwait(false)).ConfigureAwait(true);
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
                var result = await Task.Run(async () => await _control.DisableAsync(entry.Item).ConfigureAwait(false)).ConfigureAwait(true);
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
