# TidyWindow

v1.0.0-alpha

TidyWindow is a Windows desktop companion for setting up, cleaning, and maintaining developer machines. The WPF shell surfaces smart automation flows, while a .NET service layer executes PowerShell 7 scripts in the background so tasks stay responsive and auditable.

## Highlights

-   Unified dashboard for setup, clean-up, diagnostics, and curated installs.
-   PowerShell automation invoked through managed runspaces to keep the UI fluid.
-   Package manager bootstrapper for winget, Chocolatey, and Scoop.
-   Deep scan analyzer that pinpoints large files and folders with rich filtering.
-   Smart install hub driven by catalog metadata for consistent developer environments.

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

-   `src/TidyWindow.App/` – WPF MVVM client that renders the dashboard, workflows, and status telemetry.
-   `src/TidyWindow.Core/` – Background services that schedule automation, manage catalogs, and persist results.
-   `automation/` – PowerShell module plus task scripts (bootstrap, cleanup, deep scan, package installs).
-   `data/catalog/` – YAML metadata describing bundles, packages, and guidance shown in the UI.
-   `tests/` – .NET and Pester-style unit tests for the automation bridge and service layer.
-   `docs/` – Additional guides covering architecture, automation conventions, performance notes, and onboarding.

## Feature Overview

-   **Environment Bootstrapper** – Detects and installs preferred package managers, elevating when required.
-   **Cleanup Suite** – Previews reclaimable disk space and applies deletions with audit logs.
-   **Package Maintenance** – Aggregates installed software across winget, Chocolatey, and Scoop; supports bulk updates and removals.
-   **Deep Scan Diagnostics** – Surfaces heavy files and folders using PowerShell deep scans with JSON summaries.
-   **Smart Install Hub** – Installs curated bundles built from catalog presets with retry-aware orchestration.

## Building and Testing

-   `dotnet build src/TidyWindow.sln`
-   `dotnet test tests/TidyWindow.Core.Tests/TidyWindow.Core.Tests.csproj`
-   `dotnet test tests/TidyWindow.Automation.Tests/TidyWindow.Automation.Tests.csproj`
-   Optional: run `automation/scripts/test-bootstrap.ps1` to validate package manager flows end-to-end.

## Automation Conventions

-   Scripts import `automation/modules/TidyWindow.Automation.psm1` for consistent logging and elevation handling.
-   Parameters must be named, and scripts should emit structured objects for consumption by `PowerShellInvoker`.
-   Terminating errors bubble back to the .NET layer for user-friendly reporting in the dashboard.

## Further Reading

-   `docs/getting-started.md` – Detailed setup walkthrough.
-   `docs/architecture.md` – High-level component interactions.
-   `docs/automation.md` – Script authoring and invocation guidelines.
-   `docs/perf/deep-scan-benchmarks.md` – Performance characteristics for diagnostics tooling.
-   `roadmap.md` – Delivery milestones and future enhancements.

## License

TidyWindow is distributed under the MIT License. See `LICENSE` for details.

