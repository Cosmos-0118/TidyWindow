# Startup Controller

Manage all startup sources in one place with reversible toggles, delay options for user-scope items, and backups that mirror Task Manager’s enabled/disabled state.

## Page Anatomy

-   **Inventory**: Consolidated list across Run/RunOnce (HKCU/HKLM), Startup folders (per-user and common), logon scheduled tasks, and autostart services. Shows signing info, impact, last modified, and enabled state (StartupApproved-aware).
-   **Actions**: Enable/disable with per-entry backup, delay launch for user-scope entries (default ~45s via replacement logon task), and restore from backup. Toggles disable while operations run.
-   **Filters and counters**: Quick filters for unsigned/high-impact and source types; enabled/disabled visibility toggles and counters that respect current filters.

## Guardrails

-   **Elevation checks**: HKLM and service operations require admin; attempts without elevation are blocked with clear errors.
-   **Backups for every change**: Registry values, startup files, scheduled task enablement, and service start/delayed-start settings are captured before modification so restores can be applied per entry.
-   **Task Manager parity**: Explorer StartupApproved values are read to keep enabled/disabled chips consistent with Task Manager.
-   **Scope limits on delay**: Delay is only offered for user-scope Run/StartupFolder entries; services and machine-scope tasks are excluded to avoid breaking boot flows.
-   **Logging and state refresh**: All operations log to the Activity Log; inventory refresh updates counters and filter results after changes.

## Backup and Restore

-   **Registry entries**: Backup stores name, data, root, and subkey; restore rewrites the value and drops the backup record.
-   **Startup files**: Moved to `%ProgramData%/TidyWindow/StartupBackups/files` with the original path retained; restore moves it back.
-   **Scheduled tasks**: Captures `Enabled` state and reapplies on restore.
-   **Services**: Captures `Start` and `DelayedAutoStart` and reapplies on restore; no delay scheduling is attempted.
-   **Delay plans**: Replacement task paths are tracked; warnings surface if a vendor re-adds the original entry.

## Troubleshooting

-   **Task Manager mismatch**: Ensure StartupApproved entries exist for the account; refresh inventory to sync states.
-   **Cannot toggle service/task**: Requires elevation and a valid Task Scheduler path; rerun as admin if blocked.
-   **Missing delay task**: Check Activity Log warnings; rerun Delay to recreate if a vendor removed it.

## Testing Hints

-   Run targeted tests for startup controller views and filters in the app test suite.
-   Validate disabled-state parity by editing StartupApproved entries and confirming the UI mirrors Task Manager.

