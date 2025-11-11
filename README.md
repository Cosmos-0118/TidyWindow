# TidyWindow

v1.0.0-alpha

TidyWindow is a Windows desktop companion for setting up, cleaning, and maintaining developer machines. The WPF shell surfaces smart automation flows, while a .NET service layer executes PowerShell 7 scripts in the background so tasks stay responsive and auditable.

## Highlights

-   Unified dashboard covering bootstrap, cleanup, diagnostics, installs, registry tuning, and essentials automation.
-   Background PowerShell execution via managed runspaces and a resilient external `pwsh` fallback (`src/TidyWindow.Core/Automation/PowerShellInvoker.cs`).
-   PulseGuard notifications and high-friction prompts that watch automation logs and surface actionable toasts (`src/TidyWindow.App/Services/PulseGuardService.cs`).
-   Activity Log workspace with filtering, search, and clipboard export for every task (`src/TidyWindow.App/ViewModels/LogsViewModel.cs`).
-   Registry Optimizer with live state probes, presets, custom values, and restore points (`src/TidyWindow.Core/Maintenance/RegistryOptimizerService.cs`).
-   Driver update intelligence that normalises hardware IDs and optional filters before presenting updates (`src/TidyWindow.Core/Updates/DriverUpdateService.cs`).
-   Crash log capture and background-mode auto start so the assistant can run quietly from the tray (`src/TidyWindow.App/Services/CrashLogService.cs`).

## Quick Start

```powershell
# Clone and enter the repo
git clone https://github.com/Cosmos-0118/TidyWindow.git
cd TidyWindow

# Restore and build
dotnet restore src/TidyWindow.sln
dotnet build src/TidyWindow.sln

# Launch the WPF shell
dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj
```

### Prerequisites

-   Windows 10 or later
-   .NET SDK 8.0 or newer
-   PowerShell 7 (`pwsh`)
-   Optional: winget, Chocolatey, Scoop (installed via `automation/scripts/install-package-manager.ps1` or manually)

## Project Layout

-   `src/TidyWindow.App/` – WPF MVVM client with navigation, tray integration, PulseGuard, and the Activity Log.
-   `src/TidyWindow.Core/` – Background services for cleanup, deep scan, installs, registry state, package maintenance, and driver updates.
-   `automation/` – PowerShell module plus task scripts (bootstrap, cleanup, deep scan, registry probes, package installs, essentials repairs).
-   `data/catalog/` – YAML metadata describing bundles, packages, and guidance shown in the UI.
-   `tests/` – .NET unit tests that exercise the MVVM services and automation bridge.
-   `docs/` – Additional guides covering architecture, automation conventions, performance notes, and onboarding.

## Desktop Experience

-   **Navigation Hub** – `MainWindow` hosts modular pages wired via `NavigationService` so features can be shown or cached on demand.
-   **Activity Log + PulseGuard** – `ActivityLogService` records structured entries; `PulseGuardService` turns important entries into tray notifications and guided prompts.
-   **Background Presence** – `BackgroundPresenceService` syncs the "run in background" preference with Windows auto-start and logs the outcome.
-   **Crash Resilience** – `CrashLogService` attaches to dispatcher, task, and AppDomain handlers to write crash reports under `%LocalAppData%/TidyWindow/logs`.
-   **Tray Controls** – `TrayService` lets users tuck the app away, receive PulseGuard alerts, and relaunch pages without leaving background mode.

## Feature Overview

-   **Environment Bootstrapper** – Detects and installs winget, Chocolatey, and Scoop using safe elevation and retry (`automation/scripts/install-package-manager.ps1`).
-   **Cleanup Suite** – Previews reclaimable space and deletes safely with recycle-bin fallback, hidden/system skips, and retry logic (`src/TidyWindow.Core/Cleanup/CleanupService.cs`).
-   **Deep Scan Diagnostics** – Walks the filesystem with category heuristics, parallel IO, and live progress snapshots (`src/TidyWindow.Core/Diagnostics/DeepScanService.cs`).
-   **Smart Install Hub** – Installs curated bundles driven by YAML metadata and configurable install queues (`src/TidyWindow.Core/Install/InstallQueue.cs`).
-   **Package Maintenance** – Updates or removes catalog-managed software through PowerShell scripts with structured JSON payloads (`src/TidyWindow.Core/Maintenance/PackageMaintenanceService.cs`).
-   **Essentials Library** – One-click repairs covering networking, storage, Windows Update, Defender and more (see `automation/essentials/*.ps1`).
-   **Driver Updates** – Detects actionable drivers, deduplicates hardware IDs, and reports optional filters before offering installs.
-   **Registry Optimizer** – Bundles curated registry tweaks, supports presets, validates custom values, and writes restore points that can be reapplied later.
-   **Activity Observability** – The Activity Log keeps every automation transcript searchable; PulseGuard and High-Friction prompts flag risky conditions (e.g., legacy PowerShell).

## Automation and Background Services

-   **Runspace Execution** – `PowerShellInvoker` keeps UI threads free, normalises parameters, and falls back to spawning `pwsh.exe` if intrinsic modules are missing.
-   **Registry State Watcher** – `RegistryStateWatcher` probes tweak states concurrently with cancellation-aware channels.
-   **Package Inventory** – `PackageInventoryService` merges winget, Chocolatey, and Scoop listings so maintenance pages know what is installed.
-   **Driver Update Pipeline** – `DriverUpdateService` consumes JSON output from `automation/essentials/driver-update-detect.ps1`, normalises versions, and enriches with friendly status text.
-   **Preferences + Background Mode** – `UserPreferencesService` persists UI settings while `BackgroundPresenceService` toggles Windows startup tasks and logs results.
-   **Crash Guard** – `CrashLogService` captures unhandled exceptions across dispatcher, TaskScheduler, and AppDomain to aid support escalations.

## Building and Testing

-   `dotnet build src/TidyWindow.sln`
-   `dotnet test tests/TidyWindow.Core.Tests/TidyWindow.Core.Tests.csproj`
-   `dotnet test tests/TidyWindow.Automation.Tests/TidyWindow.Automation.Tests.csproj`
-   `dotnet test tests/TidyWindow.App.Tests/TidyWindow.App.Tests.csproj`
-   Optional: run `automation/scripts/test-bootstrap.ps1` to validate package manager flows end-to-end.

## Running in Background

-   Launch with `--minimized` to start directly in tray background mode.
-   Use the Settings page to toggle "Run in background"; `BackgroundPresenceService` will add or remove the scheduled startup entry and log the outcome.
-   PulseGuard notifications can be muted or filtered via user preferences.

## Automation Conventions

-   Scripts import `automation/modules/TidyWindow.Automation.psm1` for consistent logging and elevation handling.
-   Parameters must be named, and scripts should emit structured objects for consumption by `PowerShellInvoker`.
-   Terminating errors bubble back to the .NET layer for user-friendly reporting in the dashboard.
-   Long-running scripts should emit JSON payloads so services like `DriverUpdateService` and `PackageMaintenanceService` can parse structured results.

## Further Reading

-   `docs/getting-started.md` – Detailed setup walkthrough.
-   `docs/architecture.md` – High-level component interactions.
-   `docs/automation.md` – Script authoring and invocation guidelines.
-   `docs/perf/deep-scan-benchmarks.md` – Performance characteristics for diagnostics tooling.
-   `roadmap.md` – Delivery milestones and future enhancements.

## License

TidyWindow is distributed under the MIT License. See `LICENSE` for details.
