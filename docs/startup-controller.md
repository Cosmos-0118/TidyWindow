# Startup Controller Guardrails

## Scope and goals

-   Surface every startup source in one place: HKCU/HKLM Run & RunOnce, Startup folders (per-user and common), logon-scheduled tasks, and autostart services.
-   Enable/disable and delay operations with reversible backups and clear status (enabled/disabled chips) that reflect Task Manager via StartupApproved state.
-   Keep users safe with elevation checks, backups, and documented rollback paths if vendors re-add entries.

## Prerequisites

-   App runs elevated for HKLM writes; Run/StartupFolder delay is user-scope unless elevated.
-   .NET 8, Windows-only features (registry, services, Task Scheduler). Non-Windows is unsupported (CA1416 warnings acceptable).
-   Inventory matches Task Manager using Explorer StartupApproved keys for Run/StartupFolder entries and task/service enabled flags.

## Operations and guardrails

-   **Inventory**: `StartupInventoryService` enumerates sources with signing info, impact score, last modified, and enabled state. Explorer StartupApproved values are honored so "disabled" matches Task Manager.
-   **Toggle enable/disable**: `StartupControlService` requires elevation for HKLM/Services. Each change writes a reversible `StartupEntryBackup` (registry values, file moves, task enabled state, service start type/delayed start). Errors are logged to activity log; toggles are disabled during in-flight operations.
-   **Delay launch (user entries only)**: `StartupDelayService` creates a logon task per entry; we do not delay services or machine-scope tasks. Delay is default 45s with backup/plan recorded; warns if vendor re-adds entries.
-   **Filters and search**: Quick filters for safe/unsigned/high-impact and source-type toggles. Enabled/disabled visibility filters default on, wired to `IsEnabled` and refreshed on change.
-   **Telemetry counters**: Visible/disabled/enabled/unsigned/high-impact counts refresh with view filters applied; baseline disabled count tracks pre-change state.

## Backup and rollback

-   **Registry entries**: Before deletion, value name, data, root, subkey captured into `StartupEntryBackup`; enable restores from backup and removes the record.
-   **Startup files**: Moved to `%ProgramData%/TidyWindow/StartupBackups/files` with original path recorded. Restore moves back and drops backup record.
-   **Scheduled tasks**: Captures `Task.Enabled`; restore re-applies and clears backup.
-   **Services**: Captures `Start` and `DelayedAutoStart`; restore re-applies and clears backup.
-   **Delay plans**: `StartupDelayPlanStore` tracks replacement task path; self-heal warnings surface if vendor re-adds.

## Restoring defaults

-   Use the Toggle action to re-enable items; it applies the recorded backup where present.
-   If a backup is missing or corrupted, re-run inventory and manually re-create the entry (use vendor installer) or re-enable via Task Scheduler/Services MMC.
-   For delayed entries that reappeared, disable/delete the re-added startup entry or re-run Delay; warnings surface in activity log.

## Safety notes

-   Guard against non-elevated HKLM writes; `EnsureElevated` throws if admin role is missing.
-   Unsigned/high-impact filters are OR-combined to avoid hiding risky items.
-   Service toggles intentionally avoid delay; only disable/enable with backup of start values.
-   File moves and registry writes are best-effort; errors are returned in `StartupToggleResult` and logged.

## Troubleshooting

-   **Mismatch with Task Manager enabled/disabled**: Confirm StartupApproved keys exist; inventory now reads those flags. If still mismatched, refresh inventory and ensure Task Manager state was applied for the same user scope.
-   **Cannot toggle service/task**: Requires elevation; ensure app is running as admin. Task Scheduler items must exist at recorded path.
-   **Delay task missing**: Warning in activity log; re-run Delay to recreate; vendor re-adds can remove our replacement task.

## Testing hints

-   Run `dotnet test tests/TidyWindow.App.Tests/TidyWindow.App.Tests.csproj --filter "StartupControllerPageTests"` for UI filter logic.
-   Add integration checks for Run/StartupFolder disabled states by editing Explorer StartupApproved entries and verifying `IsEnabled` reflects them.
