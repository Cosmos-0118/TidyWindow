# Simple Uninstaller – Phase 1 Inventory & Data Services

_Phase status: Completed 2025-11-27_

## Coverage & Guardrails

-   Registry surfaces: `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall`, `HKLM\SOFTWARE\WOW6432Node\...`, and (by default) `HKCU\SOFTWARE\...` via `Get-TidyInstalledAppInventory` inside `automation/modules/TidyWindow.Automation/apps.ps1`.
-   Default filters skip entries with `SystemComponent=1` or a `ReleaseType` containing `Update/Hotfix/Security`; callers can opt back in via `AppInventoryOptions`.
-   Winget data is merged in-process by reusing the existing `get-package-inventory.ps1` infrastructure, so source tags include `Winget` only when a catalog match exists. Winget-only entries are surfaced as lightweight DTOs without registry metadata.
-   All fields stay read-only: the module never deletes or edits registry keys—Phase 1 is enumeration only.

## DTO Contract (`InstalledApp`)

The shared DTO (in `src/TidyWindow.Core/Uninstall/InstalledApp.cs`) now includes everything Phase 2 orchestration will need:

-   Identity: `Name`, `Version`, `Publisher`, `ProductCode`, `InstallerType`, `InstallerHints`, `SourceTags`.
-   Execution info: `UninstallString`, `QuietUninstallString`, `IsWindowsInstaller`, `Winget*` properties for fallback toggles, `HasQuietUninstall`, `HasWingetMetadata` helpers.
-   Context: `InstallLocation`, `RegistryKey`, `Metadata` (hive/scope/etc.), `EstimatedSizeBytes`, `InstallDate`, `DisplayIcon`, `Language`, `ReleaseType`, `SystemComponent` flag.

## Services & Scripts

-   **PowerShell module**: `Get-TidyInstalledAppInventory` gathers registry entries, merges winget metadata, and returns structured PSCustomObjects plus warnings.
-   **Automation script**: `automation/scripts/get-installed-apps.ps1` imports the module, exposes CLI switches, and emits a JSON payload with plan details + diagnostics. `-PlanOnly` (dry run) returns the sequence of steps without touching the registry/winget.
-   **C# wrapper**: `AppInventoryService` (`src/TidyWindow.Core/Uninstall/AppInventoryService.cs`) runs the script through `PowerShellInvoker`, parses the JSON payload, and returns an `AppInventorySnapshot` with caching, plan text, warnings, and `IsDryRun/IsCacheHit` markers. A simple in-memory cache (default 2 minutes) avoids repeated PowerShell calls unless `ForceRefresh` or `DryRun` is requested.
-   **API surface**: `IAppInventoryService` + `AppInventoryOptions` let the UI request inventory with toggles for system components, updates, winget data, user hives, force refresh, and dry run.

## Diagnostics & Dry Run Behavior

-   Every script invocation returns `warnings` (missing hives, `winget` issues, etc.) so the UI can surface degraded states.
-   Dry run mode (PlanOnly/DryRun) is wired end-to-end: no enumeration occurs, but the JSON still returns the action plan so UX copy can preview what inventory would do.
-   Telemetry hooks: `AppInventorySnapshot` carries the duration, generated timestamp, and the same plan text for later logging.

Phase 1 deliverables unlock the Phase 2 orchestrator: the WPF app can now call `IAppInventoryService` to get cached, merged inventory data with deterministic DTOs and diagnostics.
