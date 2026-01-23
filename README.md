# TidyWindow

Windows maintenance companion for builders who prioritize safety, repeatability, and observability. TidyWindow coordinates PowerShell 7 automations through managed .NET services so every action is guarded, logged, and reversible.

## At a Glance

-   Latest stable: **3.9.3** ([data/catalog/latest-release.json](data/catalog/latest-release.json))
-   Docs: [docs/](docs)
-   Roadmap: [roadmap.md](roadmap.md)
-   Press: [Press-and-Reviews.md](Press-and-Reviews.md)

## Core Capabilities

-   **Essentials repair center** ([docs/essentials.md](docs/essentials.md)): Curated fixes for networking, Defender, printing, and Windows Update with dry-run previews, sequential execution, restore points, and transcripts.
-   **Cleanup and storage hygiene** ([docs/cleanup.md](docs/cleanup.md)): Preview clutter, apply risk-aware deletions with recycle-bin-first flows, and schedule recurring sweeps.
-   **Registry optimizer** ([docs/registry-optimizer.md](docs/registry-optimizer.md)): Stage presets or custom tweaks with JSON restore points, rollback countdowns, baseline tracking, and preset customization alerts.
-   **Install and maintain software** ([docs/install-hub.md](docs/install-hub.md), [docs/maintenance.md](docs/maintenance.md)): Drive winget, Scoop, and Chocolatey from one queue and keep curated bundles current.
-   **PathPilot and process intelligence** ([docs/pathpilot.md](docs/pathpilot.md), [docs/known-processes.md](docs/known-processes.md)): Safely manage PATH edits, monitor running processes, and escalate Threat Watch findings with remediation guidance.
-   **Startup controller** ([docs/startup-controller.md](docs/startup-controller.md)): Inventory Run/RunOnce, Startup folders, logon tasks, and services with accurate enabled/disabled states, reversible backups, delays, and guardrails.
-   **PulseGuard observability** ([docs/activity-log.md](docs/activity-log.md), [docs/settings.md](docs/settings.md)): Turn significant Activity Log events into actionable notifications, prompts, and searchable transcripts.

## Safety by Design

-   Restore-point enforcement for high-risk flows, with pruning to stay within disk budgets.
-   Cleanup guardrails: protected roots, skip-recent filters, recycle-bin preference, lock inspection, and permission-repair fallbacks.
-   Sequential automation queues with cancellation hooks, deterministic logging, and retry policies.
-   PulseGuard notification gating that respects user toggles, window focus, and cooldown windows.
-   Activity Log plus JSON/Markdown transcripts for audits, with direct "View log" actions.
-   PathPilot safeguards that validate, diff, and checkpoint PATH changes before applying them.

## Architecture Overview

-   Frontend: WPF (.NET 8) views with CommunityToolkit.Mvvm view models ([src/TidyWindow.App](src/TidyWindow.App)).
-   Services: C# services for cleanup, registry state, automation scheduling, PowerShell invocation, and tray presence ([src/TidyWindow.Core](src/TidyWindow.Core), [src/TidyWindow.App/Services](src/TidyWindow.App/Services)).
-   Automation: PowerShell 7 scripts in [automation/](automation) for essentials, cleanup, registry, and diagnostics; YAML/JSON catalogs describe tweaks, presets, processes, and bundles.
-   Packaging: Inno Setup installer ([installer/TidyWindowInstaller.iss](installer/TidyWindowInstaller.iss)) plus self-contained release artifacts in GitHub Releases.

## Install and Run

Prerequisites: Windows 10 or later, .NET SDK 8.0+, PowerShell 7 on PATH.

```powershell
git clone https://github.com/Cosmos-0118/TidyWindow.git
cd TidyWindow

dotnet restore src/TidyWindow.sln
dotnet build src/TidyWindow.sln -c Debug

dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj
```

See [docs/getting-started.md](docs/getting-started.md) for optional dependencies, troubleshooting, and installer usage. Latest installer: **3.9.3** (stable). Inside the app, open Settings ▸ Updates to read the hosted manifest and jump to the newest release notes without a manual download.

## Test Suite

```powershell
dotnet test tests/TidyWindow.Core.Tests/TidyWindow.Core.Tests.csproj
dotnet test tests/TidyWindow.Automation.Tests/TidyWindow.Automation.Tests.csproj
dotnet test tests/TidyWindow.App.Tests/TidyWindow.App.Tests.csproj
```

-   Tools in [tools/](tools) validate catalog consistency ([tools/check_duplicate_packages.py](tools/check_duplicate_packages.py), [tools/suggest_catalog_fixes.py](tools/suggest_catalog_fixes.py)) and PowerShell flows ([tools/test-process-catalog-parser.ps1](tools/test-process-catalog-parser.ps1)).
-   PulseGuard and the Activity Log surface runtime confirmation for long-running automations.

## Documentation Map

-   [docs/cleanup.md](docs/cleanup.md) – Cleanup workflow, risk model, and scheduler.
-   [docs/essentials.md](docs/essentials.md) – Repair catalog, queues, and safety features.
-   [docs/deep-scan.md](docs/deep-scan.md) – Diagnostics, heuristics, and Threat Watch scanners.
-   [docs/install-hub.md](docs/install-hub.md) / [docs/maintenance.md](docs/maintenance.md) – Package installation and upkeep cockpit.
-   [docs/pathpilot.md](docs/pathpilot.md) – PATH governance with diff previews and rollback plans.
-   [docs/registry-optimizer.md](docs/registry-optimizer.md) – Restore-point-backed registry tuning.
-   [docs/startup-controller.md](docs/startup-controller.md) – Startup sources, toggles, delays, backups, guardrails, and rollback guidance.
-   [docs/known-processes.md](docs/known-processes.md) – Process catalog, classifications, and Threat Watch signals.
-   [docs/activity-log.md](docs/activity-log.md) – Observability pipeline, transcripts, and PulseGuard integration.
-   [docs/settings.md](docs/settings.md) – Preference system, PulseGuard toggles, and background presence.
-   [docs/tech-stack.md](docs/tech-stack.md) – Full technology breakdown.

## Support, Roadmap, and Press

-   Open issues with Activity Log snippets, cleanup reports, or automation transcripts for faster triage.
-   Track work in [roadmap.md](roadmap.md).
-   Press and listings live in [Press-and-Reviews.md](Press-and-Reviews.md).

## License and Credits

TidyWindow ships under the MIT License (see [LICENSE](LICENSE)).

Built with GitHub Copilot assistance alongside contributions from the Windows, PowerShell, winget, Scoop, and Chocolatey ecosystems.
