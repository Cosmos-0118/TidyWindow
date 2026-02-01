## Foundation Setup

[x] Step 1.1: Create baseline docs (`README.md`, `roadmap.md`, `docs/architecture.md`) with the high-level vision.
[x] Step 1.2: Add repo standards (`.editorconfig`, `.gitignore`, `LICENSE`) and document coding conventions.
[x] Step 1.3: Initialize solution file `src/TidyWindow.sln` using the .NET 8 WPF desktop template.
[x] Step 1.4: Scaffold WPF project `src/TidyWindow.App/TidyWindow.App.csproj` with default resources and app manifest.
[x] Step 1.5: Add shared class library `src/TidyWindow.Core/TidyWindow.Core.csproj` and reference it from the app.

[x] Step 1.6: Commit the initial skeleton to version control and tag `init-foundation` for reference.

**Build & Run Checkpoint**

- `dotnet build src/TidyWindow.sln`
- `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`

## Build & Infrastructure

[x] Step 2.1: Create `Directory.Build.props` to pin .NET 8, enable nullable, and turn on analyzers.
[x] Step 2.2: Configure solution-level NuGet restore and deterministic builds via `Directory.Build.targets` if needed.
[x] Step 2.3: Add core package references (CommunityToolkit.Mvvm, Microsoft.Windows.Compatibility, Serilog) in project files.
[x] Step 2.4: Author CI workflow `/.github/workflows/ci.yml` covering restore, build, and unit test execution.
[x] Step 2.5: Document local build steps in `docs/getting-started.md` so contributors can verify setup.

**Build & Run Checkpoint**

- `dotnet build src/TidyWindow.sln`
- `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`

## Automation Layer

[x] Step 3.1: Scaffold module file `automation/modules/TidyWindow.Automation/TidyWindow.Automation.psm1` with logging and elevation helpers.
[x] Step 3.2: Create script `automation/scripts/bootstrap-package-managers.ps1` with detection stubs for winget, Chocolatey, Scoop and also all well know powerful package managers.
[x] Step 3.3: Implement invocation bridge `src/TidyWindow.Core/Automation/PowerShellInvoker.cs` using `System.Management.Automation` runspaces for async script execution.
[x] Step 3.4: Add unit tests `tests/TidyWindow.Core.Tests/Automation/PowerShellInvokerTests.cs` covering error handling.
[x] Step 3.5: Provide usage notes in `docs/automation.md` describing script conventions and parameters.

**Build & Run Checkpoint**

- `dotnet build src/TidyWindow.sln`
- `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`

## App Shell & Navigation

[x] Step 4.1: Configure WPF entry point in `TidyWindow.App/App.xaml` and `App.xaml.cs` with MVVM bootstrap logic.
[x] Step 4.2: Build `MainWindow.xaml` shell with a Frame or HamburgerMenu host, command bar, and status footer.
[x] Step 4.3: Add placeholder pages (`Views/DashboardPage.xaml`, `Views/TasksPage.xaml`, `Views/SettingsPage.xaml`).
[x] Step 4.4: Implement navigation service `src/TidyWindow.App/Services/NavigationService.cs` using Frame navigation wired to the shell.
[x] Step 4.5: Create base view models in `src/TidyWindow.App/ViewModels` and hook them via dependency injection.

**Build & Run Checkpoint**

- `dotnet build src/TidyWindow.sln`
- `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`

## Environment Bootstrapper

[x] Step 5.1: Implement detection logic `src/TidyWindow.Core/PackageManagers/PackageManagerDetector.cs` using the PowerShell invoker.
[x] Step 5.2: Create view model `src/TidyWindow.App/ViewModels/BootstrapViewModel.cs` with async detect/install commands.
[x] Step 5.3: Design UI `Views/BootstrapPage.xaml` showing status cards and action buttons.
[x] Step 5.4: Wire success/error notifications into the activity log for bootstrap operations.
[x] Step 5.5: Write integration script `automation/scripts/test-bootstrap.ps1` to validate bootstrap flows end-to-end.

**Build & Run Checkpoint**

- `dotnet build src/TidyWindow.sln`
- `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`

## System Clean-Up Suite

[x] Step 6.1: Draft safe clean-up script `automation/scripts/cleanup-preview.ps1` with dry-run output.
[x] Step 6.2: Model clean-up results in `src/TidyWindow.Core/Cleanup/CleanupReport.cs` and related DTOs.
[x] Step 6.3: Add service `src/TidyWindow.Core/Cleanup/CleanupService.cs` coordinating script execution and parsing.
[x] Step 6.4: Build `Views/CleanupPage.xaml` with preview list, filters, and execute button.
[x] Step 6.5: Add regression tests `tests/TidyWindow.Automation.Tests/Cleanup/CleanupScriptTests.cs` for key scenarios.
[x] Step 6.6: Enable selecting previewed files for deletion and apply changes from the Cleanup UI.

**Build & Run Checkpoint**

- `dotnet build src/TidyWindow.sln`
- `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`

## Package Maintenance Console

> Legacy runtime updates surface has been retired. Next milestone is a cross-manager maintenance page that enumerates installed software and supports bulk updates or removals.

[x] Step 7.1: Retire runtime update script, service, page, tests, and docs (removed Oct 2025 cleanup).
[x] Step 7.2: Implement package inventory service that shells out to winget/choco/scoop using dedicated PowerShell runspaces, elevating winget/choco while keeping scoop in the standard user context.
[x] Step 7.3: Join inventory data with `data/catalog/packages` metadata to surface friendly names, tags, and safe removal guidance.
[x] Step 7.4: Build view model `ViewModels/PackageMaintenanceViewModel.cs` exposing update/delete commands and wiring to the install queue when needed.
[x] Step 7.5: Design `Views/PackageMaintenancePage.xaml` to present installed packages, available updates, and removal actions with responsive status feedback.
[x] Step 7.6: Document package maintenance workflows and troubleshooting in `docs/package-maintenance.md` with updated, well-structured automation script references.

**Build & Run Checkpoint**

- `dotnet build src/TidyWindow.sln`
- `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`

## Deep Scan Analyzer

[x] Step 8.1: Implement scanning engine `src/TidyWindow.Core/Diagnostics/DeepScanService.cs` that invokes automation to surface heavy files.
[x] Step 8.2: Build script `automation/scripts/deep-scan.ps1` to surface large files and return summary JSON.
[x] Step 8.3: Create view model `ViewModels/DeepScanViewModel.cs` translating scanner output to UI models.
[x] Step 8.4: Craft `Views/DeepScanPage.xaml` with filters, sorting, and detail pane interactions.
[x] Step 8.5: Add performance benchmarks in `docs/perf/deep-scan-benchmarks.md` to track scanning costs.

**Build & Run Checkpoint**

- `dotnet build src/TidyWindow.sln`
- `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`

## Smart Install Hub

Target a curated list of roughly 30 essential developer packages (Python, Java, GCC/MinGW, Git, Node, etc.) before expanding the catalog. they should ask for admin permission if needed.

[x] Step 9.1: Define package catalog `data/catalog/packages.yml` with bundles and metadata.
[x] Step 9.2: Implement queue orchestrator `src/TidyWindow.Core/Install/InstallQueue.cs` with retry semantics.
[x] Step 9.3: Build view model `ViewModels/InstallHubViewModel.cs` supporting bundle selection and queue actions.
[x] Step 9.4: Design `Views/InstallHubPage.xaml` showing curated bundles and progress indicators.
[x] Step 9.5: Add export/import helpers `src/TidyWindow.Core/Install/BundlePresetService.cs` for sharing presets.

**Build & Run Checkpoint**

- `dotnet build src/TidyWindow.sln`
- `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`

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

## Startup Controller

> Full-control startup surface that mirrors the Processes page layout patterns while surfacing every startup source (Run keys, Startup folders, scheduled tasks, services) with admin-level actions.

[x] Step 12.1: Inventory startup sources into a unified model (HKCU/HKLM Run/RunOnce, Startup folders, Task Scheduler logon tasks, autostart services) with publisher/signing info, impact score, and source tagging.
[x] Step 12.2: Add view models + service layer to toggle enable/disable per entry with reversible backups (export original state) and activity-log breadcrumbs; keep HKLM writes gated by admin context (app already runs elevated by default).
[x] Step 12.3: Design `Views/StartupControllerPage.xaml` using the Processes-page layout (hero strip + filter/search + card/list hybrid) with clear source badges, risk hints, and trust indicators.
[x] Step 12.4: Implement “delay launch” for user-scope entries we own (defer runs via scheduled tasks with post-boot offsets); avoid delaying system services and warn when apps self-heal their entries.
[x] Step 12.5: Add telemetry/insights: startup impact estimates, last modified, unsigned flags, and a before/after counter of disabled items; include quick filters (safe to disable, unsigned, heavy impact).
[x] Step 12.6: Document guardrails and rollback paths in `docs/startup-controller.md`, covering backups, elevation prompts, and how to restore defaults if vendors re-add entries.

## Driver Updates Experience (retired)

> Removed in November 2025 due to inconsistent automation output and low usage.

## Performance Lab (Advanced Tweaks)

> New launch-style page for competitive performance tuning with strict guardrails. Exclude tweaks already covered by Registry Optimizer presets (power throttling, CPU core parking, HPET toggle) to avoid duplication.

[x] Step 13.1: Design `Views/PerformanceLabPage.xaml` (startup-shell layout) with hero metrics, quick actions, and per-tweak cards grouped by Power, Memory/I-O, Scheduling, and Security, plus a “test-run” dry mode that only reports expected changes.
[x] Step 13.2: Add automation for Ultimate Performance plan enablement/restore (`powercfg -duplicatescheme` + set active) and selective service slimming templates (Xbox/DiagTrack/consumer services) with exportable pre/post service states.
[x] Step 13.3: Ship Hardware Reserved Memory fixer: detect misreported RAM vs physical DIMMs, expose `bcdedit /deletevalue truncatememory`, msconfig max-memory reset guidance, and disable memory compression with reversible apply.
[x] Step 13.4: Implement Kernel & Boot controls beyond existing HPET toggle: dynamic tick on/off, platform clock toggle, `tscsyncpolicy` presets, and `linearaddress57` for >128GB rigs, all behind restore-point gating.
[x] Step 13.5: Add VBS/HVCI off-ramp with detection (Core Isolation status), registry + `bcdedit /set hypervisorlaunchtype off` apply, reboot planner, and clear warnings when unsupported hardware is detected.
[x] Step 13.6: Build ETW tracing purge/reseal flow: list active ETW sessions/providers, allow stop/cleanup with safety tiers, and provide a one-click “re-enable defaults” button to avoid losing diagnostics.
[x] Step 13.7: Deliver Pagefile & Memory deep-tuning: move pagefile to NVMe with size presets, expose EmptyWorkingSet sweep (opt-out for pinned apps), and warn when system-managed is safer.
[x] Step 13.8: Add Scheduler & Affinity toolbox: process affinity templates, ideal-node selection, I/O and CPU priority presets, and a compact stress/benchmark harness for validation; support per-app presets saved to disk.
[x] Step 13.9: Wire DirectStorage & I/O readiness checks (NVMe, GPU, driver) with optional I/O priority boost and thread priority boost toggles plus rollbacks.
[x] Step 13.10: Build Monitoring & Auto-Tune loop: lightweight WMI/ETW sampler that triggers defined presets when gaming apps are detected, logs deltas to Activity Log, and exposes quick revert.

## Reset Rescue: Smart Backup & Restore

> Deep Scan-style UI for guided, manifest-driven backup and restore of user files and app data ahead of resets/reinstalls.

[x] Step 14.1: Define archive/manifest spec (`.rrarchive` + JSON: paths, hashes, ACL hints, app metadata) and storage layout under `data/backup/`; add design note in `docs/backup.md` covering VSS, hashing, and conflict policies.
[x] Step 14.2: Implement core services in `src/TidyWindow.Core/Backup/BackupService.cs` and `RestoreService.cs` with chunked SHA-256 hashing, compression, manifest emit/load, path reconciliation, and conflict strategies (overwrite/rename/skip) plus VSS-aware file capture.
[x] Step 14.3: Add detection bridges: user profile discovery (Known Folders), app inventory (Win32/MSI, Store via Appx APIs, portable via signatures) in `src/TidyWindow.Core/Backup/InventoryService.cs`; cover best-effort app data resolution with clear flags.
[x] Step 14.4: Create view models `ViewModels/ResetRescueViewModel.cs` (states: destination selection, selection trees, validation, progress) and wire commands for Start Backup/Restore with resumability checkpoints.
[x] Step 14.5: Build `Views/ResetRescuePage.xaml` reusing Deep Scan visual language (hero strip, segmented progress, acrylic cards) with sections for destination picker, “What to protect” (users/apps, includes/excludes), validation summary, and dual CTAs (Backup/Restore).
[x] Step 14.6: Add automation helpers in `automation/scripts/reset-rescue.ps1` for VSS snapshot, locked-file copy, and registry export/import; document parameters and logging contract for the invoker.
[x] Step 14.7: Ship tests: core unit tests for manifest round-trip and conflict handling (`tests/TidyWindow.Core.Tests/Backup/`), integration tests for service + PowerShell invoker harness, and UI automation smoke for ResetRescue page navigation/progress states.
[x] Step 14.8: Update docs: `docs/getting-started.md` (new page entry), `docs/backup.md` (flows, limits, privacy), and add an HTML/Markdown sample report to `docs/reports/backup-report-sample.html`.
