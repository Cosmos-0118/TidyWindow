# TidyWindow Delivery Roadmap

> Scope note: this roadmap is optimized for a personal maintenance cockpit. Sections that felt enterprise-grade (see `RoadMapFix.md`) are either simplified or moved to a deferred backlog so the core experience stays lean.

## Foundation Setup

[x] Step 1.1: Create baseline docs (`README.md`, `roadmap.md`, `docs/architecture.md`) with the high-level vision.
[x] Step 1.2: Add repo standards (`.editorconfig`, `.gitignore`, `LICENSE`) and document coding conventions.
[x] Step 1.3: Initialize solution file `src/TidyWindow.sln` using the .NET 8 WPF desktop template.
[x] Step 1.4: Scaffold WPF project `src/TidyWindow.App/TidyWindow.App.csproj` with default resources and app manifest.
[x] Step 1.5: Add shared class library `src/TidyWindow.Core/TidyWindow.Core.csproj` and reference it from the app.

[x] Step 1.6: Commit the initial skeleton to version control and tag `init-foundation` for reference.

**Build & Run Checkpoint**

-   `dotnet build src/TidyWindow.sln`
-   `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`

## Build & Infrastructure

[x] Step 2.1: Create `Directory.Build.props` to pin .NET 8, enable nullable, and turn on analyzers.
[x] Step 2.2: Configure solution-level NuGet restore and deterministic builds via `Directory.Build.targets` if needed.
[x] Step 2.3: Add core package references (CommunityToolkit.Mvvm, Microsoft.Windows.Compatibility, Serilog) in project files.
[x] Step 2.4: Author CI workflow `/.github/workflows/ci.yml` covering restore, build, and unit test execution.
[x] Step 2.5: Document local build steps in `docs/getting-started.md` so contributors can verify setup.

**Build & Run Checkpoint**

-   `dotnet build src/TidyWindow.sln`
-   `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`

## Automation Layer

[x] Step 3.1: Scaffold module file `automation/modules/TidyWindow.Automation.psm1` with logging and elevation helpers.
[x] Step 3.2: Create script `automation/scripts/bootstrap-package-managers.ps1` with detection stubs for winget, Chocolatey, Scoop and also all well know powerful package managers.
[x] Step 3.3: Implement invocation bridge `src/TidyWindow.Core/Automation/PowerShellInvoker.cs` using `System.Management.Automation` runspaces for async script execution.
[x] Step 3.4: Add unit tests `tests/TidyWindow.Core.Tests/Automation/PowerShellInvokerTests.cs` covering error handling.
[x] Step 3.5: Provide usage notes in `docs/automation.md` describing script conventions and parameters.

**Build & Run Checkpoint**

-   `dotnet build src/TidyWindow.sln`
-   `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`

## App Shell & Navigation

[x] Step 4.1: Configure WPF entry point in `TidyWindow.App/App.xaml` and `App.xaml.cs` with MVVM bootstrap logic.
[x] Step 4.2: Build `MainWindow.xaml` shell with a Frame or HamburgerMenu host, command bar, and status footer.
[x] Step 4.3: Add placeholder pages (`Views/DashboardPage.xaml`, `Views/TasksPage.xaml`, `Views/SettingsPage.xaml`).
[x] Step 4.4: Implement navigation service `src/TidyWindow.App/Services/NavigationService.cs` using Frame navigation wired to the shell.
[x] Step 4.5: Create base view models in `src/TidyWindow.App/ViewModels` and hook them via dependency injection.

**Build & Run Checkpoint**

-   `dotnet build src/TidyWindow.sln`
-   `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`

## Environment Bootstrapper

[x] Step 5.1: Implement detection logic `src/TidyWindow.Core/PackageManagers/PackageManagerDetector.cs` using the PowerShell invoker.
[x] Step 5.2: Create view model `src/TidyWindow.App/ViewModels/BootstrapViewModel.cs` with async detect/install commands.
[x] Step 5.3: Design UI `Views/BootstrapPage.xaml` showing status cards and action buttons.
[x] Step 5.4: Wire success/error notifications into the activity log for bootstrap operations.
[x] Step 5.5: Write integration script `automation/scripts/test-bootstrap.ps1` to validate bootstrap flows end-to-end.

**Build & Run Checkpoint**

-   `dotnet build src/TidyWindow.sln`
-   `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`

## System Clean-Up Suite

[x] Step 6.1: Draft safe clean-up script `automation/scripts/cleanup-preview.ps1` with dry-run output.
[x] Step 6.2: Model clean-up results in `src/TidyWindow.Core/Cleanup/CleanupReport.cs` and related DTOs.
[x] Step 6.3: Add service `src/TidyWindow.Core/Cleanup/CleanupService.cs` coordinating script execution and parsing.
[x] Step 6.4: Build `Views/CleanupPage.xaml` with preview list, filters, and execute button.
[x] Step 6.5: Add regression tests `tests/TidyWindow.Automation.Tests/Cleanup/CleanupScriptTests.cs` for key scenarios.
[x] Step 6.6: Enable selecting previewed files for deletion and apply changes from the Cleanup UI.

**Build & Run Checkpoint**

-   `dotnet build src/TidyWindow.sln`
-   `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`

## Package Maintenance Console

> Legacy runtime updates surface has been retired. Next milestone is a cross-manager maintenance page that enumerates installed software and supports bulk updates or removals.

[x] Step 7.1: Retire runtime update script, service, page, tests, and docs (removed Oct 2025 cleanup).
[x] Step 7.2: Implement package inventory service that shells out to winget/choco/scoop using dedicated PowerShell runspaces, elevating winget/choco while keeping scoop in the standard user context.
[x] Step 7.3: Join inventory data with `data/catalog/packages` metadata to surface friendly names, tags, and safe removal guidance.
[x] Step 7.4: Build view model `ViewModels/PackageMaintenanceViewModel.cs` exposing update/delete commands and wiring to the install queue when needed.
[x] Step 7.5: Design `Views/PackageMaintenancePage.xaml` to present installed packages, available updates, and removal actions with responsive status feedback.
[x] Step 7.6: Document package maintenance workflows and troubleshooting in `docs/package-maintenance.md` with updated, well-structured automation script references.

**Build & Run Checkpoint**

-   `dotnet build src/TidyWindow.sln`
-   `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`

## Deep Scan Analyzer

[x] Step 8.1: Implement scanning engine `src/TidyWindow.Core/Diagnostics/DeepScanService.cs` that invokes automation to surface heavy files.
[x] Step 8.2: Build script `automation/scripts/deep-scan.ps1` to surface large files and return summary JSON.
[x] Step 8.3: Create view model `ViewModels/DeepScanViewModel.cs` translating scanner output to UI models.
[x] Step 8.4: Craft `Views/DeepScanPage.xaml` with filters, sorting, and detail pane interactions.
[x] Step 8.5: Add performance benchmarks in `docs/perf/deep-scan-benchmarks.md` to track scanning costs.

**Build & Run Checkpoint**

-   `dotnet build src/TidyWindow.sln`
-   `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`

## Smart Install Hub

Target a curated list of roughly 30 essential developer packages (Python, Java, GCC/MinGW, Git, Node, etc.) before expanding the catalog. they should ask for admin permission if needed.

[x] Step 9.1: Define package catalog `data/catalog/packages.yml` with bundles and metadata.
[x] Step 9.2: Implement queue orchestrator `src/TidyWindow.Core/Install/InstallQueue.cs` with retry semantics.
[x] Step 9.3: Build view model `ViewModels/InstallHubViewModel.cs` supporting bundle selection and queue actions.
[x] Step 9.4: Design `Views/InstallHubPage.xaml` showing curated bundles and progress indicators.
[x] Step 9.5: Add export/import helpers `src/TidyWindow.Core/Install/BundlePresetService.cs` for sharing presets.

**Build & Run Checkpoint**

-   `dotnet build src/TidyWindow.sln`
-   `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`

## Install Hub Rework

Reference: `Future-ideas/installhubpagerework.txt` (grounded in current `Views/InstallHubPage.xaml` + `InstallHubViewModel`).

[x] Step 9.6: Introduce a `CurrentInstallHubPivot` enum and related state in `ViewModels/InstallHubViewModel.cs` so navigation between Bundles, Catalog, and Queue is VM-driven and testable (added enum, observable `CurrentPivot`, headline helper, and `NavigatePivotCommand`).
[x] Step 9.7: Split the monolithic page into `Views/InstallHubBundlesView.xaml`, `Views/InstallHubCatalogView.xaml`, and `Views/InstallHubQueueView.xaml`, each reusing the shared view model (new user controls hosted by the rebuilt `InstallHubPage.xaml`).
[x] Step 9.8: Rebuild the Bundles view with hero text, bundle cards, and quick actions (queue bundle, view details, open queue chip) while respecting the existing `_headline` binding and bundle metadata (new hero card, selected bundle detail panel, and per-card actions wired to `QueueBundle`/`ViewBundleDetails`).
[x] Step 9.9: Recreate the Catalog view as a responsive, virtualized browser with Essentials-style breakpoints, filters, and multi-select queueing that leverages `QueueSelection` and `VirtualizingStackPanel` recycling (sidebar filters with bundle/tag selection, responsive two-column layout, and a recycling ListView with refreshed package cards).
[x] Step 9.10: Move queue/history UX into its own page that reuses `InstallOperationItemViewModel`, exposes retry/cancel/clear commands, and supports the responsive drawer-to-button behavior described in the notes (new split layout with hero metrics, queue timeline cards, and drawer overlay that collapses into a button on compact breakpoints).
[x] Step 9.11: Remove legacy entrance `Storyboard` resources (`InstallBundleCardItemStyle`, `InstallPackageCardItemStyle`, `InstallQueueItemStyle`) and replace with skeleton/loading states while keeping the existing throttled overlay animation only (page resources rewritten with static styles plus shared skeleton templates now wired into Bundles, Catalog, and Queue views).
[x] Step 9.12: Add responsive layout helpers (borrowed from `Views/EssentialsPage.xaml`) plus empty states and queue telemetry badges so the hub scales from single-column to split-view layouts without dropped frames.

## PulseGuard Watchdog & Notifications

PulseGuard is the smart watchdog that keeps a pulse on automation logs, surfaces actionable insights, and keeps the app helpful without becoming noisy.

[x] Step 10.1: Finalize PulseGuard log heuristics, naming, and UX copy; define error/event taxonomy for automation scripts.
[x] Step 10.2: Rework `Views/SettingsPage.xaml` and related view models so the admin-mode default is explained, the layout is modernized check other pages for reference, and PulseGuard controls are grouped coherently.
[x] Step 10.3: Implement background (system tray) mode with a toggle in settings, including tray icon states, auto-start behavior, and graceful shutdown hooks.
[x] Step 10.4: Build notification pipeline that queues toast notifications, enforces cooldown rules, and differentiates success summaries from actionable alerts.
[x] Step 10.5: Surface high-friction scenarios (e.g., legacy PowerShell, post-install restarts) as targeted prompts with "View logs" and "Restart app" actions.

## Driver Updates Experience

Reference: `Future-ideas/driverupdatespage.txt` plus `automation/essentials/driver-update-detect.ps1`.

[x] Step 11.1: Extend `driver-update-detect.ps1` output contract (or adapter DTO) so the UI can display badges for update availability, downgrade risk, vendor/class, optional flag, and skip reasons without extra transforms.
[x] Step 11.2: Build a modular Driver Updates UI (e.g., `Views/DriverUpdates/DriverUpdatesShell.xaml` hosting `DriverUpdatesListView.xaml`, `DriverUpdatesFiltersView.xaml`, and `DriverUpdatesInsightsView.xaml`) wired to the refreshed `DriverUpdatesViewModel` so each surface stays focused and independently testable.
[x] Step 11.3: Implement Windows Update install actions via `Microsoft.Update.Session.CreateUpdateInstaller`, respecting include/optional toggles and queue integration.
[x] Step 11.4: Add reinstall/rollback helpers that call `pnputil` (and fallbacks for older builds) using `installedInfPath` from the script payload, surfacing logs in the UI.
[x] Step 11.5: Provide GPU-specific guidance (links or future vendor CLI hooks) plus health insights for problem codes/unsigned drivers so the page remains useful even when WU shows zero updates.

## Version Control Hub

Reference: `Future-ideas/idea.txt` (Version Control Page concept).

[ ] Step 12.1: Enhance `automation/scripts/get-package-inventory.ps1` and related DTOs to emit duplicate installs, install roots, PATH ownership data, and pin metadata for reuse by the new view model.
[ ] Step 12.2: Create `VersionControlSnapshot` models and a dedicated `VersionControlViewModel` that groups anomalies (update available, duplicates, pins, manual upgrades) and exposes filterable collections.
[ ] Step 12.3: Compose the Version Control UI from smaller views—`Views/VersionControl/VersionControlShell.xaml` plus dedicated `AnomaliesView.xaml`, `AllPackagesView.xaml`, and `ActionsDrawerView.xaml`—reusing Install Hub responsive helpers so tabs remain light-weight.
[ ] Step 12.4: Wire per-package actions (update, switch version, clear pin, set PATH owner, open location) to existing maintenance commands, and expose export/report sharing.
[ ] Step 12.5: Link from the Maintenance page so users can pivot into the Version Control hub for deep audits while keeping Maintenance focused on quick updates.

## Project Oblivion Deep Uninstall

Reference: `Future-ideas/idea2.txt` (Project Oblivion blueprint).

[ ] Step 13.1: Author `automation/scripts/get-installed-app-footprint.ps1` to emit the consolidated linkage graph (install roots, services, autoruns, logs, confidence badges).
[ ] Step 13.2: Implement `automation/scripts/remove-app-footprint.ps1` handling the staged pipeline (process freeze, native uninstall, residual purge, verification) with dry-run + execution modes.
[ ] Step 13.3: Create `DeepUninstallViewModel` and a set of focused views (`Views/DeepUninstall/DeepUninstallShell.xaml` hosting `DiscoverView.xaml`, `PreparePlanView.xaml`, `SnapProgressView.xaml`, `AftermathView.xaml`) to mirror Cleanup-style multiphase navigation with confirmations and artifact previews.
[ ] Step 13.4: Persist backups (registry keys, startup items) and expose restore/undo, typed confirmations, and report export for auditing; integrate telemetry/activity logging for each stage.
[ ] Step 13.5: Hook Project Oblivion into Maintenance/Startup modules so uninstalling an app also clears associated startup entries and surfaces follow-up scans.

## Settings Control Center Redesign

Reference: `Future-ideas/idea4.txt` (Settings Redesign blueprint).

[ ] Step 14.1: Introduce `Views/SettingsShellPage.xaml` + `SettingsShellViewModel` hosting a nav rail and content frame that loads discrete `Settings*.xaml` pages.
[ ] Step 14.2: Split the current settings monolith into scoped views (`SettingsGeneralPage`, `SettingsAutomationPage`, `SettingsNotificationsPage`, `SettingsIntegrationsPage`, `SettingsDataPage`, `SettingsLabsPage`) each with dedicated view models and responsive layouts.
[ ] Step 14.3: Build `AutomationScheduleService` to persist per-task schedules (JSON under `%ProgramData%/TidyWindow/`) and surface shared scheduler UI (upcoming runs, last status, run-now actions).
[ ] Step 14.4: Wire feature modules (Maintenance, Cleanup, Install Hub, Driver Updates, Version Control, Project Oblivion) to register automation metadata and respond to schedule changes.
[ ] Step 14.5: Add search/help affordances, reset/export buttons, and telemetry so the new control center becomes the canonical home for automation, notifications, integrations, and safety policies.

## Registry Optimizer Rework

Reference: `Future-ideas/idea5.txt` (Registry Optimizer blueprint) and `newregistoryadditions.txt`.

[ ] Step 15.1: Convert the registry tweak backlog into a structured catalog JSON (category, risk, scripts, keys) plus persistence stores for user state and history logs.
[ ] Step 15.2: Implement `RegistryStateService` + state persistence (`%AppData%/TidyWindow/registry-optimizer-state.json`) that tracks desired/detected states and hydrates the UI on launch.
[ ] Step 15.3: Build the new multi-pane UI as discrete controls (`Views/RegistryOptimizer/RegistryOptimizerShell.xaml` plus `CategoryNavView.xaml`, `CatalogPageView.xaml`, `TweakDetailsDrawer.xaml`, `ActionLogView.xaml`) with paging, search, and badges for applied/different states.
[ ] Step 15.4: Wire apply/revert flows through existing automation runners, ensuring backups (.reg snapshots) are captured, stored, and reusable for rollbacks; log the last 20 operations.
[ ] Step 15.5: Add tests for catalog validation, apply/revert flows (stub registry provider), and update `docs/automation.md` with the new optimizer behavior/support matrix.

## Startup Controller

Reference: `Future-ideas/startupcontroller.txt` (Startup Controller concept).

[ ] Step 16.1: Create `automation/scripts/get-startup-footprint.ps1` to enumerate Run keys, Startup folders, scheduled tasks, services, and AppX startup tasks with impact data.
[ ] Step 16.2: Develop `StartupControllerService` (Windows service or scheduled task) that runs at boot, reads a stored schedule, launches items in waves, and logs telemetry.
[ ] Step 16.3: Build `StartupControllerViewModel` plus modular views (`Views/StartupController/StartupControllerShell.xaml`, `StartupOverviewCardView.xaml`, `StartupListView.xaml`, `DiagnosticsTimelineView.xaml`, `ProfileManagerView.xaml`) so reorderable lists, sliders, and diagnostics remain isolated and maintainable.
[ ] Step 16.4: Implement apply/reset logic that safely disables native startup entries, stores backups, writes schedule JSON, and offers a panic "Restore defaults" action.
[ ] Step 16.5: Integrate with Project Oblivion and Activity Log so startup changes stay in sync with installs/uninstalls and users can export/import profiles.
