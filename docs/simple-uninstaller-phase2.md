# Simple Uninstaller – Phase 2 (Orchestration & Maintenance UI)

Phase 2 delivers the first end-to-end uninstall experience that the roadmap calls the internal MVP: we now enumerate applications, build structured uninstall plans, and expose those plans through a maintenance-style page in the WPF shell.

## Core Service Additions

-   `AppUninstallService` implements `IAppUninstallService` and encapsulates the full decision tree:
    -   Prefer MSI packages by parsing ProductCode and invoking `msiexec /x {GUID} /qn /norestart`.
    -   Fall back to quiet uninstall strings and inject installer-specific silent switches (Inno/NSIS/InstallShield) when needed.
    -   Use the default uninstall string if a quiet path is missing, respecting elevation hints taken from registry scope/source tags.
    -   Honor dry-run mode by logging sandboxed steps instead of launching processes.
    -   Append optional `winget uninstall --id <Id> --silent --accept-*` commands whenever metadata exists and a user toggles fallback or winget-only modes.
-   Each plan is executed inside `UninstallSandbox`, which provides telemetry (timestamps, stdout/stderr capture, exit codes) surfaced to the UI via `AppUninstallResult`.
-   `SimpleUninstallerViewModel` orchestrates refreshes (`IAppInventoryService`), batching, dry-run toggles, cancellation, and UI-facing status transitions (Idle → Running → Succeeded/Failed/Cancelled).

## WPF Maintenance Page

-   `SimpleUninstallerPage` is a cacheable page registered with the navigation shell and bound to the new view model.
-   Layout hits every roadmap requirement:
    -   Hero header with dry-run toggle and action bar (Refresh, Uninstall selected, Cancel, plus selection helpers).
    -   Segmented card containing the installed-app list with checkboxes, badges, and live status pills; keyboard focus and selection are retained via cached view model state.
    -   Detail pane that surfaces install path, status, and winget controls without secondary dialogs.
    -   Bottom drawer exposing the activity log; users can expand/collapse it and clear entries inline.
-   Activity entries mirror `ActivityLogService` messages so troubleshooting stays consistent between the shell footer and the page-level log drawer.

## User Flow

1. Navigate to **Simple uninstaller** from the shell sidebar (new nav item next to Maintenance).
2. Refresh inventory (auto-runs on first load) to hydrate the table and hero summary.
3. Select one or more apps, toggle dry-run/winget options, and choose **Uninstall selected**.
4. Monitor status pills and the activity drawer; cancel in-flight batches through the action bar.

## Validation & Telemetry

-   `dotnet build src/TidyWindow.sln` exercises the new registrations and XAML before shipping.
-   ActivityLogService captures information/success/warning/error events for every major operation (inventory refresh, batch execution, per-app failures).
-   Dry-run traces record what command would execute, fulfilling the guardrails defined during Phase 0.

## Next Steps

Phase 3 focuses on optional cleanup prompts—no code paths have been wired yet, but the new service/view model boundaries purposely leave space for opt-in cleanup suggestions, telemetry enrichment, and integration tests once uninstall telemetry starts flowing.
