# TidyWindow Delivery Roadmap

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

[ ] Step 2.1: Create `Directory.Build.props` to pin .NET 8, enable nullable, and turn on analyzers.
[ ] Step 2.2: Configure solution-level NuGet restore and deterministic builds via `Directory.Build.targets` if needed.
[ ] Step 2.3: Add core package references (CommunityToolkit.Mvvm, Microsoft.Windows.Compatibility, Serilog) in project files.
[ ] Step 2.4: Author CI workflow `/.github/workflows/ci.yml` covering restore, build, and unit test execution.
[ ] Step 2.5: Document local build steps in `docs/getting-started.md` so contributors can verify setup.

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

[ ] Step 4.1: Configure WPF entry point in `TidyWindow.App/App.xaml` and `App.xaml.cs` with MVVM bootstrap logic.
[ ] Step 4.2: Build `MainWindow.xaml` shell with a Frame or HamburgerMenu host, command bar, and status footer.
[ ] Step 4.3: Add placeholder pages (`Views/DashboardPage.xaml`, `Views/TasksPage.xaml`, `Views/SettingsPage.xaml`).
[ ] Step 4.4: Implement navigation service `src/TidyWindow.App/Services/NavigationService.cs` using Frame navigation wired to the shell.
[ ] Step 4.5: Create base view models in `src/TidyWindow.App/ViewModels` and hook them via dependency injection.

**Build & Run Checkpoint**

-   `dotnet build src/TidyWindow.sln`
-   `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`

## Environment Bootstrapper

[ ] Step 5.1: Implement detection logic `src/TidyWindow.Core/PackageManagers/Detector.cs` using the PowerShell invoker.
[ ] Step 5.2: Create view model `src/TidyWindow.App/ViewModels/BootstrapViewModel.cs` with async detect/install commands.
[ ] Step 5.3: Design UI `Views/BootstrapPage.xaml` showing status cards and action buttons.
[ ] Step 5.4: Wire success/error notifications into the activity log for bootstrap operations.
[ ] Step 5.5: Write integration script `automation/scripts/test-bootstrap.ps1` to validate bootstrap flows end-to-end.

**Build & Run Checkpoint**

-   `dotnet build src/TidyWindow.sln`
-   `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`

## System Clean-Up Suite

[ ] Step 6.1: Draft safe clean-up script `automation/scripts/cleanup-preview.ps1` with dry-run output.
[ ] Step 6.2: Model clean-up results in `src/TidyWindow.Core/Cleanup/CleanupReport.cs` and related DTOs.
[ ] Step 6.3: Add service `src/TidyWindow.Core/Cleanup/CleanupService.cs` coordinating script execution and parsing.
[ ] Step 6.4: Build `Views/CleanupPage.xaml` with preview list, filters, and execute button.
[ ] Step 6.5: Add regression tests `tests/TidyWindow.Automation.Tests/Cleanup/CleanupScriptTests.cs` for key scenarios.

**Build & Run Checkpoint**

-   `dotnet build src/TidyWindow.sln`
-   `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`

## Runtime & App Updates

[ ] Step 7.1: Create metadata catalog `src/TidyWindow.Core/Updates/RuntimeCatalogService.cs` listing tracked runtimes.
[ ] Step 7.2: Implement script `automation/scripts/check-runtime-updates.ps1` returning version/availability info.
[ ] Step 7.3: Build view model `ViewModels/RuntimeUpdatesViewModel.cs` aggregating catalog and script results.
[ ] Step 7.4: Design `Views/RuntimeUpdatesPage.xaml` with sortable grid and update/install commands.
[ ] Step 7.5: Document supported runtimes and manual override steps in `docs/runtime-updates.md`.

**Build & Run Checkpoint**

-   `dotnet build src/TidyWindow.sln`
-   `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`

## Deep Scan Analyzer

[ ] Step 8.1: Implement scanning engine `src/TidyWindow.Core/Diagnostics/DeepScanService.cs` using parallel file enumeration.
[ ] Step 8.2: Build script `automation/scripts/deep-scan.ps1` to surface large files and orphaned folders.
[ ] Step 8.3: Create view model `ViewModels/DeepScanViewModel.cs` translating scanner output to UI models.
[ ] Step 8.4: Craft `Views/DeepScanPage.xaml` with filters, sorting, and detail pane interactions.
[ ] Step 8.5: Add performance benchmarks in `docs/perf/deep-scan-benchmarks.md` to track scanning costs.

**Build & Run Checkpoint**

-   `dotnet build src/TidyWindow.sln`
-   `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`

## Smart Install Hub

[ ] Step 9.1: Define package catalog `data/catalog/packages.yml` with bundles and metadata.
[ ] Step 9.2: Implement queue orchestrator `src/TidyWindow.Core/Install/InstallQueue.cs` with retry semantics.
[ ] Step 9.3: Build view model `ViewModels/InstallHubViewModel.cs` supporting bundle selection and queue actions.
[ ] Step 9.4: Design `Views/InstallHubPage.xaml` showing curated bundles and progress indicators.
[ ] Step 9.5: Add export/import helpers `src/TidyWindow.Core/Install/BundlePresetService.cs` for sharing presets.

**Build & Run Checkpoint**

-   `dotnet build src/TidyWindow.sln`
-   `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`

## Integrity & Threat Checks

[ ] Step 10.1: Script Defender bridge `automation/scripts/defender-scan.ps1` invoking quick and custom scans.
[ ] Step 10.2: Implement heuristics service `src/TidyWindow.Core/Security/ConfidenceScoringService.cs` with scoring rules.
[ ] Step 10.3: Build view model `ViewModels/SecurityViewModel.cs` aggregating Defender and heuristic data.
[ ] Step 10.4: Layout `Views/SecurityPage.xaml` with confidence badges and remediation actions.
[ ] Step 10.5: Add security documentation `docs/security-model.md` explaining detection logic and user controls.

**Build & Run Checkpoint**

-   `dotnet build src/TidyWindow.sln`
-   `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`

## Privilege & Deployment

[ ] Step 11.1: Implement privilege broker `src/TidyWindow.Core/Security/PrivilegeBroker.cs` to request elevation.
[ ] Step 11.2: Update automation scripts to assert execution rights and fallback for non-admin sessions.
[ ] Step 11.3: Configure self-contained publish profile under `build/publish-settings.json` for portable builds.
[ ] Step 11.4: Add packaging script `build/package.ps1` that zips the published output and automation scripts.
[ ] Step 11.5: Document deployment paths (portable zip, optional MSIX, CLI scripts) in `docs/deployment.md`.

**Build & Run Checkpoint**

-   `dotnet build src/TidyWindow.sln`
-   `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`

## Task Orchestration & Logging

[ ] Step 12.1: Create background queue `src/TidyWindow.Core/Infrastructure/TaskQueue.cs` with cancellation support.
[ ] Step 12.2: Wire Serilog configuration via `TidyWindow.App/appsettings.json` and bootstrap in `App.xaml.cs`.
[ ] Step 12.3: Build shared logging helpers `src/TidyWindow.Core/Logging/LogContext.cs` for structured events.
[ ] Step 12.4: Implement activity log control `Views/Controls/ActivityLogControl.xaml` and integrate with pages.
[ ] Step 12.5: Add diagnostics tests `tests/TidyWindow.Core.Tests/Infrastructure/TaskQueueTests.cs` for concurrency flows.

**Build & Run Checkpoint**

-   `dotnet build src/TidyWindow.sln`
-   `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`

## Persistence & Settings

[ ] Step 13.1: Integrate LiteDB data context `src/TidyWindow.Core/Data/DatabaseContext.cs` with repositories.
[ ] Step 13.2: Implement settings service `src/TidyWindow.Core/Settings/SettingsService.cs` with caching.
[ ] Step 13.3: Expose domain models for schedules, telemetry, and plugin preferences.
[ ] Step 13.4: Build `Views/SettingsPage.xaml` bindings for toggles, schedules, and storage paths.
[ ] Step 13.5: Add migration script `automation/scripts/reset-settings.ps1` for troubleshooting.

**Build & Run Checkpoint**

-   `dotnet build src/TidyWindow.sln`
-   `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`

## Plugin System Foundation

[ ] Step 14.1: Draft plugin specification in `docs/plugin-spec.md` with schema and lifecycle events.
[ ] Step 14.2: Implement loader `src/TidyWindow.Core/Plugins/PluginLoader.cs` supporting PowerShell and .NET plugins.
[ ] Step 14.3: Provide plugin base interfaces in `src/TidyWindow.Core/Plugins/Contracts`.
[ ] Step 14.4: Add sample plugin `samples/plugins/SampleCleanupPlugin` demonstrating both script and managed actions.
[ ] Step 14.5: Document plugin authoring guide in `docs/plugins/developer-guide.md`.

**Build & Run Checkpoint**

-   `dotnet build src/TidyWindow.sln`
-   `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`

## Scheduling & Notifications

[ ] Step 15.1: Create scheduler service `src/TidyWindow.Core/Scheduling/SchedulerService.cs` wrapping Task Scheduler APIs.
[ ] Step 15.2: Build scheduling UI `Views/SchedulerPage.xaml` with recurrence pickers.
[ ] Step 15.3: Implement notification service `src/TidyWindow.App/Services/NotificationService.cs` using `Microsoft.Toolkit.Uwp.Notifications` toast helpers.
[ ] Step 15.4: Provide sample schedules in `data/schedules/default.json` for common maintenance routines.
[ ] Step 15.5: Add scheduler validation tests `tests/TidyWindow.Core.Tests/Scheduling/SchedulerServiceTests.cs`.

**Build & Run Checkpoint**

-   `dotnet build src/TidyWindow.sln`
-   `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`

## Telemetry & Diagnostics

[ ] Step 16.1: Configure optional Application Insights in `App.xaml.cs` with user opt-in settings.
[ ] Step 16.2: Build diagnostics panel `Views/DiagnosticsPage.xaml` showing environment and log snapshot export.
[ ] Step 16.3: Implement telemetry pipeline `src/TidyWindow.Core/Telemetry/TelemetryService.cs` with buffering.
[ ] Step 16.4: Document privacy commitments in `docs/privacy.md` and provide data export steps.
[ ] Step 16.5: Add offline diagnostic export script `automation/scripts/export-logs.ps1`.

**Build & Run Checkpoint**

-   `dotnet build src/TidyWindow.sln`
-   `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`

## UX Polish & Accessibility

[ ] Step 17.1: Run accessibility audit with Accessibility Insights and capture findings in `docs/accessibility-report.md`.
[ ] Step 17.2: Implement keyboard navigation and semantic fixes across XAML views.
[ ] Step 17.3: Add theme resources `Resources/ThemeResources.xaml` supporting light, dark, and high-contrast.
[ ] Step 17.4: Introduce localization infrastructure with `Strings/en-US/Resources.resw` and sample translations.
[ ] Step 17.5: Conduct usability review and note improvements in `docs/ux-notes.md`.

**Build & Run Checkpoint**

-   `dotnet build src/TidyWindow.sln`
-   `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`

## Testing & Quality Gates

[ ] Step 18.1: Build unit test project `tests/TidyWindow.Core.Tests` with coverage for core services.
[ ] Step 18.2: Add automation test project `tests/TidyWindow.Automation.Tests` targeting PowerShell scripts.
[ ] Step 18.3: Configure static analysis and code coverage thresholds integrated into CI workflow.
[ ] Step 18.4: Set up nightly smoke tests script `automation/scripts/nightly-smoke.ps1` and schedule in CI.
[ ] Step 18.5: Document testing strategy in `docs/testing-strategy.md`.

**Build & Run Checkpoint**

-   `dotnet build src/TidyWindow.sln`
-   `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`

## Documentation & Developer Experience

[ ] Step 19.1: Expand `docs/architecture.md` with component diagrams and data flow.
[ ] Step 19.2: Write contributor onboarding guide `docs/getting-started.md` with screenshots and tips.
[ ] Step 19.3: Create user guide `docs/user-guide.md` detailing each feature module.
[ ] Step 19.4: Add community docs (`CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, issue templates).
[ ] Step 19.5: Publish troubleshooting knowledge base `docs/troubleshooting.md`.

**Build & Run Checkpoint**

-   `dotnet build src/TidyWindow.sln`
-   `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`

## Open Preview Release

[ ] Step 20.1: Verify portable publish artifacts on a clean Windows VM using `build/package.ps1` outputs.
[ ] Step 20.2: Compose preview release notes `docs/release-notes/v0.5.md` summarizing features and known issues.
[ ] Step 20.3: Complete QA checklist `docs/qa-checklist.md` after running through scripted validation.
[ ] Step 20.4: Publish a pre-release on GitHub (tag `v0.5.0-preview`) and collect feedback via issues and `docs/beta-feedback.md`.
[ ] Step 20.5: Iterate on critical preview feedback before locking feature scope for 1.0.

**Build & Run Checkpoint**

-   `dotnet build src/TidyWindow.sln`
-   `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`

## 1.0 Stabilization

[ ] Step 21.1: Triage telemetry (if opted-in) and community feedback to resolve high-priority defects.
[ ] Step 21.2: Finalize plugin APIs and update `docs/plugin-spec.md` with breaking-change notes.
[ ] Step 21.3: Refresh `README.md` and `docs/user-guide.md` with setup screenshots and open-source contribution notes.
[ ] Step 21.4: Tag `v1.0.0`, generate release notes, and publish portable zip + scripts on GitHub Releases.
[ ] Step 21.5: Host post-launch retrospective notes in `docs/postmortem/v1.0.md` including future personal enhancements.

**Build & Run Checkpoint**

-   `dotnet build src/TidyWindow.sln`
-   `dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj`
