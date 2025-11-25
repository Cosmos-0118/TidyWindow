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

[x] Step 3.1: Scaffold module file `automation/modules/TidyWindow.Automation/TidyWindow.Automation.psm1` with logging and elevation helpers.
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

## PathPilot Runtime Switchboard

Reference: `versioncontrol.md` (PathPilot concept guide).

[x] Step 11.1: Ship `automation/scripts/Get-PathPilotInventory.ps1` plus JSON config for machine-scope runtime discovery (Python, JDK, Node, .NET to start).
[x] Step 11.2: Add `PathPilotInventoryService` + view models that render runtime cards, status badges, and machine-scope warnings.
[x] Step 11.3: Implement PATH backup + switching commands with rollback logging under `%ProgramData%/TidyWindow/PathPilot/`.
[x] Step 11.4: Provide export actions (JSON + Markdown) and wire them into activity log + report sharing flow.
[x] Step 11.5: Document operator guidance in `docs/automation.md` and add automated tests validating inventory parsing + switch safeguards.
[x] Step 11.6: Build an Essentials-style responsive UI surface (`Views/PathPilotPage.xaml` + card controls) reusing the hero strip, card stack, and detail drawer patterns for clarity on both desktop and narrow layouts.

## Driver Updates Experience (retired)

> Removed in November 2025 due to inconsistent automation output and low usage. Scripts remain archived in `automation/essentials/driver-update-detect.ps1` for future experimentation, but the WPF surface and related services have been dropped from the product plan.

## Project Oblivion Popup Deep Clean

Reference sources: `projectoblivion.txt`, `Future-ideas/idea2.txt`, PathPilot animation patterns, and Cleanup artifact scanners.

[x] Step 13.1: Enhance `automation/scripts/get-installed-app-footprint.ps1` to emit install roots, suspected services/process names, uninstall metadata, and confidence scores so the popup can start scanning immediately.
[x] Step 13.2: Create `automation/scripts/uninstall-app-deep.ps1` that performs the stage pipeline (native uninstall → process sweep → artifact discovery → user-selected cleanup → summary) while streaming JSON events for the UI.
[x] Step 13.3: Extend `automation/modules/TidyWindow.Automation/TidyWindow.Automation.psm1` with helpers for process correlation, artifact scanning, deletion ordering, and structured logging shared by the deep script.
[x] Step 13.4: Implement `ProjectOblivionViewModel` (list surface) and `ProjectOblivionPopupViewModel` (timeline, checklist, summary) that consume the streaming events and manage user selections.
[x] Step 13.5: Build `Views/ProjectOblivionPage.xaml` plus the modal popup (PathPilot-style animation ring, timeline chips, collapsible log, artifact checklist, summary card) with responsive states.
[x] Step 13.6: Persist run telemetry under `data/cleanup/<AppId>/oblivion-run.json`, surface inline success/error toasts, and expose a `View log` CTA from the summary.

## Project Oblivion Safety Rework

Reference: `rework.md` (risk assessment) plus `automation/modules/TidyWindow.Automation/TidyWindow.Automation.psm1` + `automation/scripts/oblivion-*.ps1`.

[x] Step 13.7: Harden the selection handshake so `Resolve-ArtifactSelection` (force-cleanup + uninstall scripts) fails closed when no valid selection JSON is supplied, simplify the schema to `{ selectedIds, deselectedIds }`, and persist selections alongside inventory snapshots with checksum validation (completed Nov 25 2025 via `ProjectOblivionPopupViewModel` persistence + `oblivion-force-cleanup.ps1` signature enforcement).
[x] Step 13.8: Rebuild `Invoke-OblivionArtifactDiscovery` to require trusted anchors (install roots, explicit hints, registry install locations), demote heuristic matches to opt-in candidates, cap token scans by depth/count, and annotate each artifact with provenance metadata for the popup (completed Nov 25 2025 via anchored scope scanning and candidate metadata in `automation/modules/TidyWindow.Automation/TidyWindow.Automation.psm1`).
[x] Step 13.9: Refactor `Find-TidyRelatedProcesses`/`Invoke-OblivionProcessSweep` to match processes/services via full image paths or explicit hints only, blacklist `%SystemRoot%` binaries, and stop feeding substring matches back into discovery (completed Nov 25 2025 by anchoring process matching and blocked-root checks inside `automation/modules/TidyWindow.Automation/TidyWindow.Automation.psm1`).
[x] Step 13.10: Gate `Invoke-OblivionForce*` removals behind path validation (must reside under approved roots or whitelisted registry keys), remove robocopy/pending-delete fallbacks for unknown paths, and emit pre-execution dry-run summaries so operators can review destructive actions (completed Nov 25 2025 with new artifact validation + `forceRemovalPlan` events in `automation/modules/TidyWindow.Automation/TidyWindow.Automation.psm1`).
[x] Step 13.11: Split inventory/dedupe logic so each source (registry, manager, AppX, portable) keeps its own identity, ship a committed `data/catalog/oblivion-inventory.json` template for CLI scenarios, and move the dedupe heuristics into a testable helper with opt-in merges inside `ProjectOblivionViewModel` (completed Nov 26 2025 via `ProjectOblivionInventoryDeduplicator`, the new `MergeSources` toggle, and the committed template snapshot).
[x] Step 13.12: Expand automated coverage with fixture-based discovery tests (decoy system paths), selection-handshake integration tests (timeouts, corrupt JSON, resume), and regression tests ensuring rejected artifacts/services are logged for telemetry (completed Nov 26 2025 with new `ProjectOblivionScriptTests` coverage for discovery heuristics plus timeout/corrupt/resume selection flows).

## Settings Control Center Redesign

Reference: `Future-ideas/idea4.txt` (Settings Redesign blueprint).

[ ] Step 14.1: Introduce `Views/SettingsShellPage.xaml` + `SettingsShellViewModel` hosting a nav rail and content frame that loads discrete `Settings*.xaml` pages.
[ ] Step 14.2: Split the current settings monolith into scoped views (`SettingsGeneralPage`, `SettingsAutomationPage`, `SettingsNotificationsPage`, `SettingsIntegrationsPage`, `SettingsDataPage`, `SettingsLabsPage`) each with dedicated view models and responsive layouts.
[ ] Step 14.3: Build `AutomationScheduleService` to persist per-task schedules (JSON under `%ProgramData%/TidyWindow/`) and surface shared scheduler UI (upcoming runs, last status, run-now actions).
[ ] Step 14.4: Wire feature modules (Maintenance, Cleanup, Install Hub, Version Control, Project Oblivion) to register automation metadata and respond to schedule changes.
[ ] Step 14.5: Add search/help affordances, reset/export buttons, and telemetry so the new control center becomes the canonical home for automation, notifications, integrations, and safety policies.

## Registry Optimizer Rework

Reference: `Future-ideas/idea5.txt` (Registry Optimizer blueprint) and `newregistoryadditions.txt`.

[ ] Step 15.1: Convert the registry tweak backlog into a structured catalog JSON (category, risk, scripts, keys) plus persistence stores for user state and history logs.
[ ] Step 15.2: Implement `RegistryStateService` + state persistence (`%AppData%/TidyWindow/registry-optimizer-state.json`) that tracks desired/detected states and hydrates the UI on launch.
[ ] Step 15.3: Build the new multi-pane UI as discrete controls (`Views/RegistryOptimizer/RegistryOptimizerShell.xaml` plus `CategoryNavView.xaml`, `CatalogPageView.xaml`, `TweakDetailsDrawer.xaml`, `ActionLogView.xaml`) with paging, search, and badges for applied/different states.
[ ] Step 15.4: Wire apply/revert flows through existing automation runners, ensuring backups (.reg snapshots) are captured, stored, and reusable for rollbacks; log the last 20 operations.
[ ] Step 15.5: Add tests for catalog validation, apply/revert flows (stub registry provider)

## Startup Controller

Reference: `Future-ideas/startupcontroller.txt` (Startup Controller concept).

[ ] Step 16.1: Create `automation/scripts/get-startup-footprint.ps1` to enumerate Run keys, Startup folders, scheduled tasks, services, and AppX startup tasks with impact data.
[ ] Step 16.2: Develop `StartupControllerService` (Windows service or scheduled task) that runs at boot, reads a stored schedule, launches items in waves, and logs telemetry.
[ ] Step 16.3: Build `StartupControllerViewModel` plus modular views (`Views/StartupController/StartupControllerShell.xaml`, `StartupOverviewCardView.xaml`, `StartupListView.xaml`, `DiagnosticsTimelineView.xaml`, `ProfileManagerView.xaml`) so reorderable lists, sliders, and diagnostics remain isolated and maintainable.
[ ] Step 16.4: Implement apply/reset logic that safely disables native startup entries, stores backups, writes schedule JSON, and offers a panic "Restore defaults" action.
[ ] Step 16.5: Integrate with Project Oblivion and Activity Log so startup changes stay in sync with installs/uninstalls and users can export/import profiles.

