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

## PulseGuard Watchdog & Notifications

PulseGuard is the smart watchdog that keeps a pulse on automation logs, surfaces actionable insights, and keeps the app helpful without becoming noisy.

[x] Step 10.1: Finalize PulseGuard log heuristics, naming, and UX copy; define error/event taxonomy for automation scripts.
[x] Step 10.2: Rework `Views/SettingsPage.xaml` and related view models so the admin-mode default is explained, the layout is modernized check other pages for reference, and PulseGuard controls are grouped coherently.
[x] Step 10.3: Implement background (system tray) mode with a toggle in settings, including tray icon states, auto-start behavior, and graceful shutdown hooks.
[x] Step 10.4: Build notification pipeline that queues toast notifications, enforces cooldown rules, and differentiates success summaries from actionable alerts.
[x] Step 10.5: Surface high-friction scenarios (e.g., legacy PowerShell, post-install restarts) as targeted prompts with "View logs" and "Restart app" actions.
[ ] Step 10.6: Add unit and UI automation coverage validating log parsing, notification throttling, and background-mode lifecycle transitions.
