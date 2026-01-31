using System;
using System.Windows;

namespace TidyWindow.App.Services;

/// <summary>
/// Manages TidyWindow's background presence including auto-start registration
/// and startup synchronization.
/// </summary>
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

        // Verify and synchronize startup state on initialization
        VerifyAndSyncStartupState(_preferences.Current.LaunchAtStartup);
    }

    /// <summary>
    /// Gets whether TidyWindow is currently registered to start automatically.
    /// </summary>
    public bool IsStartupRegistered => _autoStartService.IsEnabled;

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

    /// <summary>
    /// Verifies the startup registration matches the preference and fixes if needed.
    /// </summary>
    private void VerifyAndSyncStartupState(bool shouldBeEnabled)
    {
        var isActuallyEnabled = _autoStartService.IsEnabled;

        if (isActuallyEnabled == shouldBeEnabled)
        {
            // State is consistent
            if (shouldBeEnabled)
            {
                _activityLog.LogInformation("Startup", "TidyWindow startup registration verified; will launch at sign-in.");
            }
            return;
        }

        // State is out of sync, attempt to fix
        _activityLog.LogWarning("Startup", $"Startup registration mismatch detected. Preference: {shouldBeEnabled}, Actual: {isActuallyEnabled}. Attempting to synchronize...");
        ApplyAutoStart(shouldBeEnabled);
    }

    private void ApplyAutoStart(bool enable)
    {
        if (_autoStartService.TrySetEnabled(enable, out var error))
        {
            // Verify the change took effect
            var actuallyEnabled = _autoStartService.IsEnabled;
            if (actuallyEnabled == enable)
            {
                _activityLog.LogInformation("Startup", enable
                    ? "Successfully registered TidyWindow to launch at sign-in."
                    : "Successfully removed TidyWindow from startup.");
            }
            else
            {
                _activityLog.LogWarning("Startup", $"Startup registration may not have fully applied. Expected: {enable}, Actual: {actuallyEnabled}");
            }
            return;
        }

        var message = enable
            ? $"Failed to register TidyWindow for startup: {error}"
            : $"Failed to remove TidyWindow from startup: {error}";

        _activityLog.LogWarning("Startup", message);
    }
}
