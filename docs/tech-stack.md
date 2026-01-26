# TidyWindow Tech Stack

This document lists the primary technologies, libraries, and tooling that power TidyWindow.

## Application Layers

| Layer         | Technology                         | Purpose                                                                                                |
| ------------- | ---------------------------------- | ------------------------------------------------------------------------------------------------------ |
| UI            | **WPF (.NET 8)**                   | Desktop shell, navigation, dialogs, animations.                                                        |
| ViewModel     | **CommunityToolkit.Mvvm**          | Observable properties, RelayCommand, dependency injection-friendly MVVM plumbing.                      |
| Core Services | **.NET 8 class libraries**         | Cleanup engine, install orchestrators, registry management, automation scheduling.                     |
| Automation    | **PowerShell 7 scripts + modules** | OS-level work (repairs, installs, diagnostics).                                                        |
| Data          | **YAML / JSON**                    | Catalog definitions (install bundles, process catalog), persisted state snapshots, automation reports. |
| Installer     | **Inno Setup**                     | Generates distributable .exe installers for releases.                                                  |

## Key Libraries & Packages

-   **CommunityToolkit.Mvvm** – MVVM helpers (observable properties, commands, messaging).
-   **Microsoft.Extensions.DependencyInjection** – DI container for WPF composition.
-   **System.Text.Json** – JSON serialization (reports, activity log details, automation payloads).
-   **YamlDotNet** – Catalog parsing (bundles, process metadata, registry definitions).
-   **FluentValidation** (select components) – Input validation where strong constraints are required.
-   **Windows APIs** – Via P/Invoke for recycle bin operations, privilege adjustments, scheduled tasks.

## PowerShell Environment

-   **TidyWindow.Automation** module under `automation/modules/` centralises logging, error handling, JSON output helpers, and elevation utilities.
-   Scripts use `pwsh` by default but gracefully fall back to Windows PowerShell if 7.x is unavailable.
-   Automation is convention-driven: every script returns structured objects that `PowerShellInvoker` parses.

## Build & Packaging

-   **dotnet build/test** – Standard .NET CLI workflows managed via `Directory.Build.props/targets`.
-   **CI** – GitHub Actions (not shown here) run builds and tests; release pipelines produce signed installers.
-   **Inno Setup** – `installer/TidyWindowInstaller.iss` packages binaries, resources, and automation assets for distribution.
-   **Versioning** – Assembly version and installer version track the app release (current: `2.9.0`).

## Runtime Services

-   **PowerShellInvoker** – Executes scripts with runspace pooling, JSON capture, and fallback to external `pwsh` processes.
-   **ActivityLogService** – In-memory structured logging with UI integration and PulseGuard signals.
-   **PulseGuardService** – Notification engine driving toast alerts and high-friction prompts.
-   **CleanupService** – Low-level file management (preview/deletion) written entirely in C# for speed and safety.
-   **ProcessAutoStopEnforcer** – Windows service controller wrapper for Known Processes automation.
-   **MaintenanceAutoUpdateScheduler** – Queues winget/Chocolatey/Scoop updates via PowerShell.

## Data Storage & State

-   **Preferences** – JSON persisted under `%AppData%/TidyWindow/` (user scope) via `UserPreferencesService`.
-   **Automation State** – Stored in `%ProgramData%/TidyWindow/` for machine-wide visibility (cleanup automation, process enforcement, registry restore points).
-   **Catalogs** – YAML/JSON definitions under `data/catalog/` (bundles, processes, cleanup definitions).

## Testing Infrastructure

-   **xUnit** – Unit tests across `tests/` projects.
-   **Moq / NSubstitute** – Mocking frameworks (where applicable) for ViewModel/service tests.
-   **PowerShell Harnesses** – Helper scripts under `tools/` to validate catalog integrity and script execution in isolation.

## Tooling & Developer Experience

-   **VS Code / Visual Studio** – Both supported; XAML hot reload works best in Visual Studio 2022.
-   **EditorConfig** – Enforces code style and consistent formatting across C# and PowerShell.
-   **Git Hooks** (optional) – Recommended to run `dotnet format` before committing.

## Operating System Integration

-   **Windows Task Scheduler** – Manages startup tasks for background mode.
-   **Windows Notifications** – PulseGuard toasts via Win32 APIs and AppUserModelID declarations.
-   **System Restore / Recycle Bin** – Registry optimizer and cleanup flows leverage restore points and the recycle bin for safety.

## Observability

-   **Activity Log + PulseGuard** – Real-time logging and actionable toasts.
-   **CrashLogService** – Captures unhandled exceptions (dispatcher, task scheduler, AppDomain) to `%LocalAppData%/TidyWindow/logs`.
-   **High-Friction Prompts** – Guidance UI for risky operations (registry tweaks, forced deletes).

## Release Artifacts

-   **Installer** – Versioned Inno Setup executable.
-   **Portable Build** – Unzipped binaries (`out/` folder) for manual deployment.
-   **Documentation** – Markdown under `docs/` shipped with the repo and packaged releases.

