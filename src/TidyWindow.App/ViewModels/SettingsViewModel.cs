using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.Services;

namespace TidyWindow.App.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly MainViewModel _mainViewModel;
    private readonly UserPreferencesService _preferences;
    private readonly IUpdateService _updateService;
    private readonly IUpdateInstallerService _updateInstallerService;
    private readonly ITrayService _trayService;
    private readonly string _currentVersion;

    private bool _telemetryEnabled;
    private bool _runInBackground;
    private bool _launchAtStartup;
    private bool _notificationsEnabled;
    private bool _notifyOnlyWhenInactive;
    private bool _pulseGuardEnabled;
    private bool _pulseGuardShowSuccessSummaries;
    private bool _pulseGuardShowActionAlerts;
    private PrivilegeMode _currentPrivilegeMode;
    private bool _isApplyingPreferences;
    private UpdateCheckResult? _updateResult;
    private bool _isCheckingForUpdates;
    private bool _hasAttemptedUpdateCheck;
    private string _updateStatusMessage = "Updates have not been checked yet.";
    private static readonly TimeSpan MinimumCheckDuration = TimeSpan.FromMilliseconds(1200);
    private bool _isInstallingUpdate;
    private long _installerBytesReceived;
    private long? _installerTotalBytes;

    public SettingsViewModel(
        MainViewModel mainViewModel,
        IPrivilegeService privilegeService,
        UserPreferencesService preferences,
        IUpdateService updateService,
        IUpdateInstallerService updateInstallerService,
        ITrayService trayService)
    {
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));
        _updateInstallerService = updateInstallerService ?? throw new ArgumentNullException(nameof(updateInstallerService));
        _trayService = trayService ?? throw new ArgumentNullException(nameof(trayService));
        _currentPrivilegeMode = privilegeService?.CurrentMode ?? PrivilegeMode.Administrator;
        _currentVersion = string.IsNullOrWhiteSpace(_updateService.CurrentVersion)
            ? "0.0.0"
            : _updateService.CurrentVersion;

        ApplyPreferences(_preferences.Current);
        WeakEventManager<UserPreferencesService, UserPreferencesChangedEventArgs>.AddHandler(_preferences, nameof(UserPreferencesService.PreferencesChanged), OnPreferencesChanged);

        CheckForUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync, CanCheckForUpdates);
        InstallUpdateCommand = new AsyncRelayCommand(InstallUpdateAsync, CanInstallUpdate);
    }

    public IAsyncRelayCommand CheckForUpdatesCommand { get; }
    public IAsyncRelayCommand InstallUpdateCommand { get; }

    public bool TelemetryEnabled
    {
        get => _telemetryEnabled;
        set
        {
            if (SetProperty(ref _telemetryEnabled, value))
            {
                PublishStatus(value
                    ? "Telemetry sharing is enabled."
                    : "Telemetry sharing is disabled.");
            }
        }
    }

    public bool IsRunningElevated => CurrentPrivilegeMode == PrivilegeMode.Administrator;

    public string CurrentPrivilegeDisplay => IsRunningElevated
        ? "Current session: Administrator mode"
        : "Current session: User mode";

    public string CurrentPrivilegeAdvice => "TidyWindow relaunches with administrative rights so installs, registry updates, and repairs can finish without interruptions.";

    public bool RunInBackground
    {
        get => _runInBackground;
        set
        {
            if (SetProperty(ref _runInBackground, value))
            {
                if (_isApplyingPreferences)
                {
                    return;
                }

                PublishStatus(value
                    ? "Background mode enabled. TidyWindow will minimize to the tray."
                    : "Background mode disabled. TidyWindow will close when you exit.");
                _preferences.SetRunInBackground(value);
            }
        }
    }

    public bool NotificationsEnabled
    {
        get => _notificationsEnabled;
        set
        {
            if (SetProperty(ref _notificationsEnabled, value))
            {
                if (_isApplyingPreferences)
                {
                    return;
                }

                PublishStatus(value
                    ? "PulseGuard notifications will resume."
                    : "PulseGuard notifications are paused.");
                _preferences.SetNotificationsEnabled(value);
                OnPropertyChanged(nameof(CanAdjustPulseGuardNotifications));
            }
        }
    }

    public bool LaunchAtStartup
    {
        get => _launchAtStartup;
        set
        {
            if (SetProperty(ref _launchAtStartup, value))
            {
                if (_isApplyingPreferences)
                {
                    return;
                }

                PublishStatus(value
                    ? "TidyWindow will now register a Task Scheduler entry to launch at sign-in."
                    : "TidyWindow will no longer auto-launch at sign-in.");
                _preferences.SetLaunchAtStartup(value);
            }
        }
    }

    public bool NotifyOnlyWhenInactive
    {
        get => _notifyOnlyWhenInactive;
        set
        {
            if (SetProperty(ref _notifyOnlyWhenInactive, value))
            {
                if (_isApplyingPreferences)
                {
                    return;
                }

                PublishStatus(value
                    ? "Toasts will only appear when TidyWindow is not focused."
                    : "Toasts may appear even while you are using the app.");
                _preferences.SetNotifyOnlyWhenInactive(value);
            }
        }
    }

    public bool PulseGuardEnabled
    {
        get => _pulseGuardEnabled;
        set
        {
            if (SetProperty(ref _pulseGuardEnabled, value))
            {
                if (_isApplyingPreferences)
                {
                    return;
                }

                PublishStatus(value
                    ? "PulseGuard is standing watch."
                    : "PulseGuard is taking a break.");
                _preferences.SetPulseGuardEnabled(value);
                OnPropertyChanged(nameof(CanAdjustPulseGuardNotifications));
            }
        }
    }

    public bool PulseGuardShowSuccessSummaries
    {
        get => _pulseGuardShowSuccessSummaries;
        set
        {
            if (SetProperty(ref _pulseGuardShowSuccessSummaries, value))
            {
                if (_isApplyingPreferences)
                {
                    return;
                }

                PublishStatus(value
                    ? "Completion digests will be surfaced."
                    : "Completion digests are muted.");
                _preferences.SetShowSuccessSummaries(value);
            }
        }
    }

    public bool PulseGuardShowActionAlerts
    {
        get => _pulseGuardShowActionAlerts;
        set
        {
            if (SetProperty(ref _pulseGuardShowActionAlerts, value))
            {
                if (_isApplyingPreferences)
                {
                    return;
                }

                PublishStatus(value
                    ? "Action-required alerts are enabled."
                    : "Action-required alerts are muted.");
                _preferences.SetShowActionAlerts(value);
            }
        }
    }

    public bool CanAdjustPulseGuardNotifications => NotificationsEnabled && PulseGuardEnabled;

    public string CurrentVersionDisplay => _currentVersion;

    public string LatestVersionDisplay => _updateResult?.LatestVersion ?? "Unknown";

    public bool IsUpdateAvailable => _updateResult?.IsUpdateAvailable ?? false;

    public string UpdateChannelDisplay => _updateResult?.Channel ?? "stable";

    public string LatestReleaseSummary => _updateResult is { Summary: { Length: > 0 } summary }
        ? summary
        : "Release summary will appear after the first check.";

    public string LatestReleasePublishedDisplay => FormatTimestamp(_updateResult?.PublishedAtUtc);

    public string InstallerSizeDisplay => FormatSize(_updateResult?.InstallerSizeBytes);

    public string LatestIntegrityDisplay => string.IsNullOrWhiteSpace(_updateResult?.Sha256)
        ? "Not provided"
        : _updateResult!.Sha256!;

    public Uri? LatestReleaseNotesUri => _updateResult?.ReleaseNotesUri;

    public Uri? LatestDownloadUri => _updateResult?.DownloadUri;

    public bool HasReleaseNotesLink => LatestReleaseNotesUri is not null;

    public bool HasDownloadLink => LatestDownloadUri is not null;

    public bool HasAttemptedUpdateCheck
    {
        get => _hasAttemptedUpdateCheck;
        private set
        {
            if (SetProperty(ref _hasAttemptedUpdateCheck, value))
            {
                OnPropertyChanged(nameof(LastUpdateCheckDisplay));
            }
        }
    }

    public string LastUpdateCheckDisplay => _updateResult is null
        ? (HasAttemptedUpdateCheck ? "Latest attempt did not complete." : "Never checked")
        : FormatTimestamp(_updateResult.CheckedAtUtc);

    public string UpdateAvailabilitySummary
    {
        get
        {
            if (_updateResult is null)
            {
                return "Updates have not been checked yet.";
            }

            return _updateResult.IsUpdateAvailable
                ? $"Update available: {_updateResult.LatestVersion}"
                : $"You're running the latest release ({_updateResult.CurrentVersion}).";
        }
    }

    public string UpdateStatusMessage
    {
        get => _updateStatusMessage;
        private set => SetProperty(ref _updateStatusMessage, value);
    }

    public bool IsCheckingForUpdates
    {
        get => _isCheckingForUpdates;
        private set
        {
            if (SetProperty(ref _isCheckingForUpdates, value))
            {
                OnPropertyChanged(nameof(CheckForUpdatesButtonLabel));
                OnPropertyChanged(nameof(IsUpdateActionsEnabled));
                CheckForUpdatesCommand.NotifyCanExecuteChanged();
                InstallUpdateCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string CheckForUpdatesButtonLabel => IsCheckingForUpdates ? "Checking..." : "Check now";

    public bool IsInstallingUpdate
    {
        get => _isInstallingUpdate;
        private set
        {
            if (SetProperty(ref _isInstallingUpdate, value))
            {
                OnPropertyChanged(nameof(InstallUpdateButtonLabel));
                OnPropertyChanged(nameof(IsInstallUpdateVisible));
                OnPropertyChanged(nameof(ShowInstallerProgress));
                OnPropertyChanged(nameof(IsUpdateActionsEnabled));
                CheckForUpdatesCommand.NotifyCanExecuteChanged();
                InstallUpdateCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string InstallUpdateButtonLabel => IsInstallingUpdate ? "Applying..." : "Install update";

    public bool IsInstallUpdateVisible => IsUpdateAvailable || IsInstallingUpdate;

    public bool ShowInstallerProgress => IsInstallingUpdate;

    public bool IsInstallerProgressIndeterminate => !(_installerTotalBytes.HasValue && _installerTotalBytes > 0);

    public double InstallerDownloadProgress =>
        _installerTotalBytes.HasValue && _installerTotalBytes > 0
            ? Math.Clamp((double)_installerBytesReceived / _installerTotalBytes.Value * 100d, 0d, 100d)
            : 0d;

    public string InstallerDownloadStatus
    {
        get
        {
            if (_installerBytesReceived <= 0)
            {
                return "Waiting to start download...";
            }

            if (_installerTotalBytes.HasValue && _installerTotalBytes > 0)
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    "{0} of {1}",
                    FormatSize(_installerBytesReceived),
                    FormatSize(_installerTotalBytes));
            }

            return string.Format(
                CultureInfo.CurrentCulture,
                "{0} downloaded",
                FormatSize(_installerBytesReceived));
        }
    }

    public bool IsUpdateActionsEnabled => !IsCheckingForUpdates && !IsInstallingUpdate;

    private PrivilegeMode CurrentPrivilegeMode
    {
        get => _currentPrivilegeMode;
        set
        {
            if (SetProperty(ref _currentPrivilegeMode, value))
            {
                OnPropertyChanged(nameof(IsRunningElevated));
                OnPropertyChanged(nameof(CurrentPrivilegeDisplay));
                OnPropertyChanged(nameof(CurrentPrivilegeAdvice));
            }
        }
    }

    private void PublishStatus(string message)
    {
        _mainViewModel.SetStatusMessage(message);
    }

    private void ApplyPreferences(UserPreferences preferences)
    {
        _isApplyingPreferences = true;
        try
        {
            RunInBackground = preferences.RunInBackground;
            LaunchAtStartup = preferences.LaunchAtStartup;
            PulseGuardEnabled = preferences.PulseGuardEnabled;
            NotificationsEnabled = preferences.NotificationsEnabled;
            NotifyOnlyWhenInactive = preferences.NotifyOnlyWhenInactive;
            PulseGuardShowSuccessSummaries = preferences.PulseGuardShowSuccessSummaries;
            PulseGuardShowActionAlerts = preferences.PulseGuardShowActionAlerts;
            OnPropertyChanged(nameof(CanAdjustPulseGuardNotifications));
        }
        finally
        {
            _isApplyingPreferences = false;
        }
    }

    private void OnPreferencesChanged(object? sender, UserPreferencesChangedEventArgs args)
    {
        ApplyPreferences(args.Preferences);
    }

    private async Task CheckForUpdatesAsync()
    {
        if (IsCheckingForUpdates)
        {
            return;
        }

        HasAttemptedUpdateCheck = true;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            IsCheckingForUpdates = true;
            UpdateStatusMessage = "Checking for updates...";

            var result = await _updateService.CheckForUpdatesAsync().ConfigureAwait(true);
            ApplyUpdateResult(result);
        }
        catch (OperationCanceledException)
        {
            UpdateStatusMessage = "Update check was cancelled.";
        }
        catch (Exception ex)
        {
            UpdateStatusMessage = "Unable to contact the update service. Please try again.";
            _mainViewModel.LogActivityInformation("Updates", $"Update check failed: {ex.Message}");
        }
        finally
        {
            var remaining = MinimumCheckDuration - stopwatch.Elapsed;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining).ConfigureAwait(true);
            }

            IsCheckingForUpdates = false;
        }
    }

    private bool CanCheckForUpdates() => !IsCheckingForUpdates && !IsInstallingUpdate;

    private bool CanInstallUpdate() => IsUpdateAvailable && !IsCheckingForUpdates && !IsInstallingUpdate;

    private async Task InstallUpdateAsync()
    {
        if (_updateResult is null || !_updateResult.IsUpdateAvailable)
        {
            UpdateStatusMessage = "No update is available to install.";
            return;
        }

        try
        {
            IsInstallingUpdate = true;
            UpdateStatusMessage = "Downloading update package...";
            ResetInstallerProgress();

            var progress = new Progress<UpdateDownloadProgress>(p =>
            {
                var total = p.TotalBytes ?? _updateResult.InstallerSizeBytes;
                _installerBytesReceived = Math.Max(0, p.BytesReceived);
                _installerTotalBytes = total;
                RaiseInstallerProgressProperties();
            });

            var result = await _updateInstallerService.DownloadAndInstallAsync(_updateResult, progress).ConfigureAwait(true);

            UpdateStatusMessage = "Installer launched. TidyWindow will close so the upgrade can finish.";
            _mainViewModel.LogActivityInformation(
                "Updates",
                $"Installer launched at {result.InstallerPath}. Hash verified: {result.HashVerified}.");

            await Task.Delay(1500).ConfigureAwait(true);
            ShutdownForInstaller();
        }
        catch (OperationCanceledException)
        {
            UpdateStatusMessage = "Update installation was cancelled.";
        }
        catch (Exception ex)
        {
            UpdateStatusMessage = "Update installation failed. Please try again.";
            _mainViewModel.LogActivityInformation("Updates", $"Update installation failed: {ex.Message}");
        }
        finally
        {
            IsInstallingUpdate = false;
            ResetInstallerProgress();
        }
    }

    private void ShutdownForInstaller()
    {
        try
        {
            _trayService.PrepareForExit();
        }
        catch
        {
            // Non-fatal; continue shutdown
        }

        var app = System.Windows.Application.Current;
        app?.Dispatcher.BeginInvoke(new Action(() => app.Shutdown()));
    }

    private void ApplyUpdateResult(UpdateCheckResult result)
    {
        _updateResult = result ?? throw new ArgumentNullException(nameof(result));

        UpdateStatusMessage = result.IsUpdateAvailable
            ? $"TidyWindow {result.LatestVersion} is available."
            : $"You're already running the latest release ({result.CurrentVersion}).";

        PublishStatus(result.IsUpdateAvailable
            ? $"Update available: {result.LatestVersion}"
            : "No updates available.");

        _mainViewModel.LogActivityInformation(
            "Updates",
            result.IsUpdateAvailable
                ? $"Update available: {result.LatestVersion}"
                : $"No updates available (current {result.CurrentVersion}).",
            BuildUpdateDetails(result));

        RaiseUpdateProperties();
    }

    private void RaiseUpdateProperties()
    {
        OnPropertyChanged(nameof(LatestVersionDisplay));
        OnPropertyChanged(nameof(UpdateAvailabilitySummary));
        OnPropertyChanged(nameof(IsUpdateAvailable));
        OnPropertyChanged(nameof(IsInstallUpdateVisible));
        OnPropertyChanged(nameof(UpdateChannelDisplay));
        OnPropertyChanged(nameof(LatestReleaseSummary));
        OnPropertyChanged(nameof(LatestReleasePublishedDisplay));
        OnPropertyChanged(nameof(InstallerSizeDisplay));
        OnPropertyChanged(nameof(LatestIntegrityDisplay));
        OnPropertyChanged(nameof(LatestReleaseNotesUri));
        OnPropertyChanged(nameof(LatestDownloadUri));
        OnPropertyChanged(nameof(HasReleaseNotesLink));
        OnPropertyChanged(nameof(HasDownloadLink));
        OnPropertyChanged(nameof(LastUpdateCheckDisplay));
        InstallUpdateCommand.NotifyCanExecuteChanged();
    }

    private static IEnumerable<string> BuildUpdateDetails(UpdateCheckResult result)
    {
        var details = new List<string>
        {
            $"Channel: {result.Channel}",
            $"Checked at (UTC): {result.CheckedAtUtc:u}"
        };

        if (result.PublishedAtUtc.HasValue)
        {
            details.Add($"Published: {result.PublishedAtUtc:yyyy-MM-dd HH:mm}Z");
        }

        if (result.ReleaseNotesUri is not null)
        {
            details.Add($"Release notes: {result.ReleaseNotesUri}");
        }

        if (result.DownloadUri is not null)
        {
            details.Add($"Installer: {result.DownloadUri}");
        }

        return details;
    }

    private static string FormatTimestamp(DateTimeOffset? timestamp)
    {
        return timestamp?.LocalDateTime.ToString("g", CultureInfo.CurrentCulture) ?? "Unknown";
    }

    private static string FormatSize(long? bytes)
    {
        if (bytes is null || bytes <= 0)
        {
            return "Unknown";
        }

        const long OneMegabyte = 1024 * 1024;
        const long OneGigabyte = 1024L * 1024 * 1024;

        if (bytes >= OneGigabyte)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0:F1} GB", bytes.Value / (double)OneGigabyte);
        }

        if (bytes >= OneMegabyte)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0:F1} MB", bytes.Value / (double)OneMegabyte);
        }

        return string.Format(CultureInfo.CurrentCulture, "{0:N0} KB", bytes.Value / 1024d);
    }

    private void ResetInstallerProgress()
    {
        _installerBytesReceived = 0;
        _installerTotalBytes = _updateResult?.InstallerSizeBytes;
        RaiseInstallerProgressProperties();
    }

    private void RaiseInstallerProgressProperties()
    {
        OnPropertyChanged(nameof(InstallerDownloadProgress));
        OnPropertyChanged(nameof(IsInstallerProgressIndeterminate));
        OnPropertyChanged(nameof(InstallerDownloadStatus));
        OnPropertyChanged(nameof(ShowInstallerProgress));
    }
}
