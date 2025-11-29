# Settings Page Documentation

## Overview

The Settings page centralises global preferences for how TidyWindow behaves in the background: telemetry sharing, startup behaviour, tray presence, notifications, and the PulseGuard watchdog. Changes propagate immediately to supporting services (startup tasks, notification channels, privilege monitors) and are persisted via the `UserPreferencesService`.

## Purpose

-   **Personalise Behaviour**: Toggle background mode, startup registration, and notification verbosity to match user expectations.
-   **Control PulseGuard**: Decide how aggressively PulseGuard surfaces success summaries and action-required alerts.
-   **Surface Privilege Status**: Clearly display whether the current session is running with administrator rights and why elevation is sometimes required.
-   **Manage Telemetry**: Provide opt-in control over anonymised diagnostics that help detect regressions.

## Safety Features

-   **Immediate Feedback**: Every setter calls `MainViewModel.SetStatusMessage` so users get confirmation that a preference changed.
-   **Preference Persistence**: Settings flow through `UserPreferencesService`, guaranteeing they survive restarts.
-   **Guarded Writes**: Internal `_isApplyingPreferences` flag prevents feedback loops when applying stored preferences.
-   **PulseGuard Gating**: PulseGuard notification controls respect `NotificationsEnabled`; dependent toggles disable automatically to avoid inconsistent states.
-   **Privilege Awareness**: Display strings explain why elevation is used and avoid prompting users unexpectedly.

## Architecture

```
┌──────────────────────────────────────────────┐
│              SettingsPage.xaml               │
│             (View – Container)               │
└──────────────┬───────────────────────────────┘
               │
               ▼
┌──────────────────────────────────────────────┐
│           SettingsViewModel.cs               │
│ (Preference bindings + status messaging)     │
└──────────────┬───────────────────────────────┘
               │
               ▼
┌────────────────────┬────────────────────────────┐
│UserPreferencesService│ PrivilegeService         │
│BackgroundPresenceService│ PulseGuardService     │
└────────────────────┴────────────────────────────┘
```

### Key Components

-   **`SettingsViewModel`** (`src/TidyWindow.App/ViewModels/SettingsViewModel.cs`)

    -   Binds toggles to `UserPreferencesService`, publishes status messages, and surfaces privilege mode strings.
    -   Controls dependent properties (`CanAdjustPulseGuardNotifications`).

-   **`UserPreferencesService`** (`src/TidyWindow.App/Services/UserPreferencesService.cs`)

    -   Persists preferences under `%AppData%/TidyWindow/preferences.json` and raises `PreferencesChanged` events.

-   **`BackgroundPresenceService`** & **`AppAutoStartService`**

    -   React to preference changes by registering/unregistering Windows startup entries and toggling tray presence.

-   **`PulseGuardService`** (`src/TidyWindow.App/Services/PulseGuardService.cs`)
    -   Consults notification preferences to decide which toasts/high-friction prompts to surface.

## User Interface

-   **Telemetry Card**: Toggle to opt into anonymised diagnostics, with copy explaining PulseGuard benefits.
-   **Background Mode**: Enable/disable "Run in background" and "Launch at startup".
-   **Notifications**:
    -   Master toggle for PulseGuard notifications plus "Only when inactive" filter.
    -   Dependent toggles for success summaries and action-required alerts (disabled when notifications or PulseGuard off).
-   **PulseGuard Panel**: Describes the watchdog, links to Activity Log, and summarises notification behaviour.
-   **Privilege Indicator**: Shows whether the app is currently running as Administrator or User with rationale text.

## Workflow

1. **Initial Load**

    - `SettingsViewModel` reads persisted preferences and sets backing fields within `_isApplyingPreferences` guard to avoid triggering saves.

2. **User Toggles**

    - Changing a toggle updates the backing field, logs a status message, and hands the value to `UserPreferencesService`.
    - Dependent properties (PulseGuard toggles) recalculate enabling states immediately.

3. **Preference Broadcast**

    - Other services listen to `UserPreferencesService.PreferencesChanged` and adjust runtime behaviour (tray presence, notification channels, etc.).

4. **External Changes**
    - If preferences are adjusted elsewhere (e.g., background automation), the event handler re-applies them through `ApplyPreferences` to keep the UI in sync.

## Best Practices

### For Users

1. **Enable Background Mode** if you rely on automation while the app is minimised; otherwise TidyWindow exits when the window closes.
2. **Tune Notifications** to match your tolerance—leave action alerts enabled to catch failures quickly.
3. **Review Privilege Advice** when toggling high-friction features (registry tweaks, installs) so elevation prompts aren’t surprising.
4. **Opt Into Telemetry** on test machines to help the team catch regressions without exposing personal data.

### For Developers

1. **Guard Recursion**: Wrap preference applications with `_isApplyingPreferences` to prevent infinite loops.
2. **Surface Feedback**: Every new preference should emit a clear status message when toggled.
3. **Respect Dependencies**: When adding new PulseGuard knobs, ensure they disable gracefully when notifications are off.
4. **Update Documentation**: If you introduce new preferences, update this page and README to keep feature descriptions accurate.

## Technical Notes

-   Preferences propagate via weak-event pattern to avoid memory leaks.
-   `PrivilegeService` currently reports the mode captured at startup; re-run checks whenever elevation state changes.
-   PulseGuard toasts route through Windows notifications; ensure the app has an AppUserModelID if integrating new channels.

## Future Enhancements

-   Allow exporting/importing preference profiles.
-   Integrate system dark-mode detection toggles.
-   Provide direct shortcuts to change Windows notification settings.

