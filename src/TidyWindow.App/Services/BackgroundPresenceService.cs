using System;
using System.Windows;

namespace TidyWindow.App.Services;

public sealed class BackgroundPresenceService : IDisposable
{
    private readonly UserPreferencesService _preferences;
    private readonly AppAutoStartService _autoStartService;
    private readonly ActivityLogService _activityLog;

    public BackgroundPresenceService(UserPreferencesService preferences, AppAutoStartService autoStartService, ActivityLogService activityLog)
    {
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _autoStartService = autoStartService ?? throw new ArgumentNullException(nameof(autoStartService));
        _activityLog = activityLog ?? throw new ArgumentNullException(nameof(activityLog));

        WeakEventManager<UserPreferencesService, UserPreferencesChangedEventArgs>.AddHandler(_preferences, nameof(UserPreferencesService.PreferencesChanged), OnPreferencesChanged);
        ApplyAutoStart(_preferences.Current.LaunchAtStartup);
    }

    public void Dispose()
    {
        WeakEventManager<UserPreferencesService, UserPreferencesChangedEventArgs>.RemoveHandler(_preferences, nameof(UserPreferencesService.PreferencesChanged), OnPreferencesChanged);
    }

    private void OnPreferencesChanged(object? sender, UserPreferencesChangedEventArgs args)
    {
        if (args.Preferences.LaunchAtStartup == args.Previous.LaunchAtStartup)
        {
            return;
        }

        ApplyAutoStart(args.Preferences.LaunchAtStartup);
    }

    private void ApplyAutoStart(bool enable)
    {
        if (_autoStartService.TrySetEnabled(enable, out var error))
        {
            _activityLog.LogInformation("Startup", enable
                ? "Registered elevated Task Scheduler entry so TidyWindow launches quietly at sign-in."
                : "Removed the Task Scheduler entry; TidyWindow will no longer auto-launch.");
            return;
        }

        var message = enable
            ? $"Failed to register TidyWindow for startup: {error}"
            : $"Failed to remove TidyWindow from startup: {error}";

        _activityLog.LogWarning("Startup", message);
    }
}
