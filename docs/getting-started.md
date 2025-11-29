# Getting Started with TidyWindow

This guide walks you through setting up a development environment, building the WPF app, and exploring core features. It assumes you are working on Windows 10 or later.

## 1. Prerequisites

Install the following tools before cloning the repository:

-   **.NET SDK 8.0** or newer (`winget install Microsoft.DotNet.SDK.8`) – required for building the solution.
-   **PowerShell 7 (pwsh)** (`winget install Microsoft.PowerShell`) – used by automation flows and scripts.
-   **Git** (`winget install Git.Git`) – for cloning the repository.
-   **Visual Studio 2022** (optional) – the WPF designer experience is tailored for VS, but `dotnet` CLI works fine.
-   **Package managers** _(optional but recommended)_: winget, Chocolatey, and Scoop. The bootstrap scripts can install these for you later.

## 2. Clone the Repository

```powershell
# Open a PowerShell 7 session
cd C:\Developer

# Clone the repo
git clone https://github.com/Cosmos-0118/TidyWindow.git
cd TidyWindow
```

## 3. Restore Dependencies

```powershell
pwsh -NoLogo -Command "dotnet restore src/TidyWindow.sln"
```

The solution uses `Directory.Build.props/targets` to consolidate settings across projects, so a single restore brings everything down.

## 4. Build the Solution

```powershell
dotnet build src/TidyWindow.sln -c Release
```

> Tip: Use `-c Debug` when iterating locally; Release builds mirror shipping configurations and are ideal before testing installers.

## 5. Run the App

```powershell
dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj --configuration Debug
```

The WPF shell launches with the navigation hub on the left. From here you can explore each feature:

-   **Bootstrap** to install package managers.
-   **Cleanup** for disk hygiene.
-   **Install Hub** for curated software bundles.
-   **Maintenance** for package updates/removals.
-   **PathPilot**, **Essentials**, **Registry Optimizer**, and **Known Processes** for more advanced tuning.

## 6. Run Automated Tests

```powershell
# Core services
dotnet test tests/TidyWindow.Core.Tests/TidyWindow.Core.Tests.csproj

# WPF + ViewModel coverage
dotnet test tests/TidyWindow.App.Tests/TidyWindow.App.Tests.csproj

# Automation script harness
dotnet test tests/TidyWindow.Automation.Tests/TidyWindow.Automation.Tests.csproj
```

> If you are iterating on PowerShell scripts, use the helper harnesses under `tools/` (e.g., `test-process-catalog-parser.ps1`).

## 7. Explore Automation Scripts

PowerShell automation lives under `automation/`. Recommended starting points:

-   `automation/essentials/*.ps1` – high-impact repair flows.
-   `automation/scripts/` – install/update helpers, deep scan orchestrators, etc.
-   `automation/modules/TidyWindow.Automation/` – module imported by every script for logging, elevation, and JSON output.

Use `pwsh` to run scripts directly when validating changes; most scripts support `-WhatIf` or `-DryRun` style parameters.

## 8. Build an Installer (Optional)

The shipping build uses Inno Setup:

```powershell
# Install Inno Setup first: winget install JRSoftware.InnoSetup

iscc installer/TidyWindowInstaller.iss
```

Artifacts are written to `out/` and include versioned installers built from the current sources.

## 9. Stay in Sync

-   Track releases in the GitHub `Releases` tab. Version **2.9.0** is the current stable build.
-   See `roadmap.md` for planned work and larger initiatives.
-   Documentation for each feature lives under `docs/`. Start with:
    -   `docs/cleanup.md`
    -   `docs/maintenance.md`
    -   `docs/known-processes.md`
    -   `docs/activity-log.md`
    -   `docs/settings.md`

## 10. Troubleshooting

-   **Missing PowerShell 7**: Update the `pwsh` path in environment variables or install via winget.
-   **XAML Designer Errors**: Clean/rebuild the solution. WPF designer typically works in Visual Studio 2022 17.8 or newer.
-   **Administrator Rights Needed**: Some flows request elevation (installs, registry tweaks). Restart as admin when prompted.
-   **Logs**: The Activity Log page captures detailed transcripts. PulseGuard toasts include a shortcut to the log entry.

Happy tidying! If you run into issues, open an issue with the log excerpt or start a discussion in the repository.

