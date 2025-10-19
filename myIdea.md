## TidyWindow Concept Brief

**Vision**

- Build a Windows-first desktop utility that combines package management, system hygiene, and proactive maintenance into one approachable dashboard.
- Translate trusted PowerShell automation into a friendly GUI so power users and everyday users can both benefit.

**Primary Use Cases**

- Onboard or refresh a Windows machine with a curated set of package managers and productivity tooling in minutes.
- Keep developer runtimes (Java, Python, Node, etc.) patched without hunting through installers.
- Detect and clean system clutter, orphaned artifacts, and suspicious files before they become a problem.

## Proposed Tech Stack

- **Frontend/UI**: WPF on .NET 8 using XAML and MVVM (CommunityToolkit.Mvvm) for a battle-tested, highly compatible Windows desktop experience.
- **Automation Layer**: PowerShell 7 scripts executed through `System.Management.Automation` runspaces so existing scripts stay first-class citizens.
- **Orchestration Services**: .NET 8 background services handling elevation, long-running jobs, and bridging results back to the UI.
- **Package Management Integrations**: winget (primary), Chocolatey, and Scoop routed through a thin abstraction layer to keep providers swappable.
- **Local Data Storage**: SQLite via Microsoft.Data.Sqlite for caching scan results, package metadata, and user preferences with minimal setup.
- **Telemetry & Logging**: Serilog to rolling files with an optional Application Insights toggle for aggregated diagnostics.

## Core Feature Modules

1. **Environment Bootstrapper**
   - Detects existing package managers (winget, Chocolatey, Scoop).
   - Offers one-click install/repair/update flows for missing managers.
   - Validates prerequisites such as PowerShell execution policy and admin rights.
2. **System Clean-Up Suite**
   - Temp/cache cleaner with safe defaults and preview mode.
   - Startup program review with enable/disable recommendations.
   - Scheduled clean-up profiles users can automate.
3. **Runtime & App Updates**
   - Centralized dashboard for SDKs/runtimes (Java, Python, Node, .NET) and common dev tools.
   - Uses package manager commands when possible; falls back to vendor APIs/installers otherwise.
   - Supports version pinning and rollback metadata.
4. **Deep Scan Analyzer**
   - Surfaces large files, leftover folders from uninstalled apps, and long-running background services.
   - Provides context (path, size, last accessed, associated process) and recommended actions.
   - Includes filters for game assets, IDE caches, and virtual machine images.
5. **Smart Install Hub**
   - Curated catalog (IDE, browsers, productivity tools) with tags and recommended bundles (e.g., “Python Dev Setup”).
   - One-click install queue running through chosen package manager with progress feedback.
   - Option to export/import presets for new machines.
6. **Integrity & Threat Checks**
   - Hash verification for critical system binaries and installed package manifests.
   - Integrates Microsoft Defender cmdlets for quick scans and exposes results.
   - Heuristics-based “confidence score” for suspicious files with remediation suggestions.

## Supporting Capabilities

- **Privilege Broker**: Detects when admin access is required and asks for elevation via MSIX packaging or COM elevation moniker.
- **Task Orchestration**: Background queue with progress notifications, cancellation, and detailed logs per action.
- **Extensibility**: Plugin manifest allowing custom PowerShell modules or C# tasks to be dropped into a `plugins` directory.

## Roadmap Snapshot

- **MVP (0.1)**: Package manager detection/installation, basic clean-up, runtime update listings, simple PowerShell-backed actions.
- **Beta (0.5)**: Deep scan visualizations, preset bundles, telemetry, scheduled maintenance jobs.
- **1.0 Release**: Plugin system, Defender integration, rollback/pinning, enterprise-friendly reporting export.

## Future Ideas

- Microsoft Entra ID / Azure AD sign-in for managed fleets.
- Remote execution agent so IT teams can push actions to multiple machines.
- Companion command-line interface mirroring GUI features for automation scripts.
- GitHub Actions template to bootstrap fresh Windows runners with the same presets.
