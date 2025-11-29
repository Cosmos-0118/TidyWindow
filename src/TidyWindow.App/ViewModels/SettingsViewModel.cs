using System;
using System.Windows;
using TidyWindow.App.Services;

namespace TidyWindow.App.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly MainViewModel _mainViewModel;
    private readonly UserPreferencesService _preferences;

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

    public SettingsViewModel(
        MainViewModel mainViewModel,
        IPrivilegeService privilegeService,
        UserPreferencesService preferences)
    {
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _currentPrivilegeMode = privilegeService?.CurrentMode ?? PrivilegeMode.Administrator;

        ApplyPreferences(_preferences.Current);
        WeakEventManager<UserPreferencesService, UserPreferencesChangedEventArgs>.AddHandler(_preferences, nameof(UserPreferencesService.PreferencesChanged), OnPreferencesChanged);
    }

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
}
