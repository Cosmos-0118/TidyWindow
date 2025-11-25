# TidyWindow Tech Stack and Rationale

## 1. Product Snapshot

TidyWindow blends a WPF cockpit with curated PowerShell automation so Windows developer machines stay healthy. The desktop app handles navigation, background services, and user feedback, while `TidyWindow.Core` and the `automation/` scripts do the heavy lifting.

## 2. Layered Architecture

-   **Presentation (`src/TidyWindow.App/`)**

    -   Runs on WPF + .NET 8 with MVVM delivered by CommunityToolkit.Mvvm.
    -   `App.xaml.cs` wires everything with `Microsoft.Extensions.Hosting`, so services are resolved through dependency injection.
    -   `MainWindow` hosts modular pages (Cleanup, Deep Scan, Install Hub, Registry, Activity Log) loaded via `NavigationService`.
    -   Tray support, PulseGuard notifications, crash logging, and background preferences live under `Services/`.

-   **Core Services (`src/TidyWindow.Core/`)**

    -   Encapsulate cleanup, diagnostics, install orchestration, registry management, and package upkeep.
    -   `Automation/PowerShellInvoker.cs` runs scripts inside managed runspaces, falls back to external `pwsh.exe`, and streams structured output.
    -   `Maintenance/RegistryStateService.cs`, `Maintenance/RegistryOptimizerService.cs`, and `Maintenance/RegistryStateWatcher.cs` coordinate detection, application, and monitoring of registry tweaks.
    -   `Diagnostics/DeepScanService.cs` performs fast file system walks with live progress snapshots.
    -   `Install/InstallQueue.cs` and `Install/BundlePresetService.cs` map YAML bundles to actionable install plans.

-   **Automation Assets (`automation/`)**

    -   PowerShell 7 scripts grouped by purpose: essentials repairs, diagnostics, package bootstrap, registry probes, and catalog installs.
    -   `modules/TidyWindow.Automation/TidyWindow.Automation.psm1` supplies shared helpers for logging, elevation, and structured output.
    -   Scripts emit JSON so core services can parse results without brittle text parsing.

-   **Catalog & Data (`data/catalog/`)**
    -   YAML bundles describe curated install sets and maintenance packages.
    -   Additional data folders (for example `data/cache/registry`) are populated at runtime for state snapshots and restore points.

## 3. Execution Pipeline

1. **Startup** – `App.xaml.cs` ensures elevation, attaches `CrashLogService`, spins a splash screen, and builds the DI container.
2. **Navigation** – The navigation rail binds to `NavigationService` and `MainViewModel`; pages resolve their viewmodels on first use, keeping startup lean.
3. **Command Dispatch** – ViewModel commands call into strongly typed services (`CleanupService`, `DeepScanService`, etc.) housed in `TidyWindow.Core`.
4. **Automation Bridge** – Services either run pure .NET workflows or call PowerShell via `PowerShellInvoker`. Parameters are normalised, cancellation tokens respected, and JSON output is parsed back into domain records.
5. **Feedback Loop** – `ActivityLogService` records entries that drive the Activity Log page and PulseGuard notifications; viewmodels update observable collections so WPF refreshes automatically.

```
User action → ViewModel → Core service → (Runspace PowerShell | Managed logic)
                                           ↓
                                   ActivityLog + results
                                           ↓
                                     UI updates / prompts
```

## 4. Core Subsystems

-   **Cleanup** – `CleanupService` previews and deletes items with retry, recycle-bin, and hidden/system guards. Signatures and policies ship in `Cleanup/`.
-   **Deep Scan** – `DeepScanService` categorises folders/files, merges overlapping matches, and streams progress via `DeepScanProgressUpdate`.
-   **Install Hub** – `InstallQueue` sequences catalog-defined packages while `BundlePresetService` reads/writes preset YAML files.
-   **Package Maintenance** – `PackageMaintenanceService` triggers update/remove scripts and validates JSON payloads before surfacing results.
-   **Registry Optimizer** – `RegistryOptimizerService` builds plans, saves restore points, and works with `RegistryPreferenceService` for custom values.
-   **Background Presence** – `BackgroundPresenceService` toggles auto-start based on saved preferences; `PulseGuardService` throttles and routes notifications using Activity Log entries.

## 5. Automation Script Patterns

-   Scripts import `TidyWindow.Automation` to share logging, elevation checks, and error handling.
-   Parameters are always named so `PowerShellInvoker` can pass dictionaries straight through.
-   Diagnostics scripts (for example `automation/essentials/network-fix-suite.ps1`) emit JSON objects as their final output line.
-   Catalog installers use YAML (`data/catalog/bundles.yml` and `data/catalog/packages/**`) to stay editable without recompiling.

## 6. Observability & Resilience

-   `ActivityLogService` stores recent entries in-memory for quick filtering and copy/paste within the app.
-   `PulseGuardService` listens for warnings/errors, applies cool-down windows, and escalates through tray notifications or high-friction prompts.
-   `CrashLogService` writes WPF, TaskScheduler, and AppDomain exceptions to `%LocalAppData%/TidyWindow/logs`.
-   Registry operations create restore points and can revert them later (`RegistryOptimizerViewModel` + `RegistryOptimizerService`).

## 7. Tooling & Build

-   **Runtime** – .NET 8.0, WPF, PowerShell 7 (invoked as `pwsh`).
-   **MVVM Toolkit** – CommunityToolkit.Mvvm for observable properties and relay commands.
-   **Dependency Injection** – `Microsoft.Extensions.Hosting` + `Microsoft.Extensions.DependencyInjection` to configure services once at startup.
-   **Packaging** – Inno Setup script `installer/TidyWindowInstaller.iss` bundles the WPF app, PowerShell scripts, and catalog data.
-   **Testing** – `tests/` projects validate core services (e.g., cleanup safety checks, PulseGuard throttling) and automation contracts.

## 8. Why This Stack Works

-   Built specifically for Windows: WPF UI, PowerShell automation, and installer support fit native expectations.
-   Strong separation of concerns keeps UI, core logic, scripts, and data independent and easy to extend.
-   Script-driven design means many updates ship by editing YAML or PowerShell without touching compiled code.
-   Hosting + DI simplify background services (tray, notifications, preferences) and make unit testing achievable.
-   Modern C# features (records, async/await, pattern matching) keep the codebase expressive while remaining performant.

The result is a maintainable, Windows-native toolkit that automates common system hygiene tasks without sacrificing responsiveness or transparency.

