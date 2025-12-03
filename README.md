# TidyWindow

**Windows maintenance companion for builders who care about safety, repeatability, and observability.**

TidyWindow consolidates environment bootstrapping, cleanup, registry tuning, diagnostics, and automated repairs into a single WPF application. Every operation flows through managed .NET services that coordinate PowerShell 7 scripts, enforce guard rails, and emit structured activity logs so you always know what ran and how to roll it back.

## Key Capabilities

-   **Essentials repair center** (`docs/essentials.md`): Queue curated automations for networking, Defender, printing, and Windows Update issues. Each task supports dry-run previews, sequential execution, restore-point creation, and detailed transcripts.
-   **Multi-phase cleanup** (`docs/cleanup.md`): Discover clutter with a fast preview, vet selections with risk signals and lock inspection, then delete via recycle-bin-first workflows or schedule recurring sweeps with conservative defaults.
-   **Registry optimizer** (`docs/registry-optimizer.md`): Stage preset or custom tweaks with automatic JSON restore points, 30-second rollback countdowns, baseline tracking, and preset customization alerts.
-   **Install & maintain software** (`docs/install-hub.md`, `docs/maintenance.md`): Drive winget, Scoop, and Chocolatey flows from a single queue, including automation to keep curated bundles current.
-   **PathPilot & process intelligence** (`docs/pathpilot.md`, `docs/known-processes.md`): Manage PATH edits safely, monitor running processes, and flag anti-system behavior with remediation guidance.
-   **PulseGuard observability** (`docs/activity-log.md`, `docs/settings.md`): Turn significant Activity Log events into actionable notifications, high-friction prompts, and searchable transcripts while respecting notification preferences.

## Safety Systems

-   **Restore-point enforcement**: Registry Optimizer, Essentials repairs, and other high-risk flows create restore points on demand and prune older snapshots to stay within disk budgets.
-   **Cleanup guardrails**: Protected roots, skip-recent filters, recycle-bin preference, lock inspection, and permission-repair fallbacks prevent accidental data loss.
-   **Sequential automation queues**: Essentials and Maintenance flows run one script at a time with cancellation hooks, deterministic logging, and retry policies.
-   **PulseGuard notification gating**: Notifications, action alerts, and success digests respect user toggles (`CanAdjustPulseGuardNotifications`), window focus, and cooldown windows before surfacing prompts.
-   **Activity Log + transcripts**: Every automation writes structured entries plus JSON/Markdown reports for audits, with direct "View log" actions in PulseGuard toasts.
-   **PathPilot safeguards**: PATH mutations run through validation, diff previews, and rollback checkpoints before touching environment state.

## Architecture Snapshot

-   **Frontend**: WPF (.NET 8) views with CommunityToolkit.Mvvm view models (`src/TidyWindow.App`).
-   **Service layer**: C# services for cleanup, registry state, automation scheduling, PowerShell invocation, and tray presence (`src/TidyWindow.Core`, `src/TidyWindow.App/Services`).
-   **Automation layer**: PowerShell 7 scripts in `automation/` for essentials, cleanup, registry, and diagnostics; YAML/JSON catalogs describe tweaks, presets, processes, and bundles.
-   **Packaging**: Inno Setup installer (`installer/TidyWindowInstaller.iss`) plus self-contained release artifacts in GitHub Releases.

## Getting Started

### Prerequisites

-   Windows 10 or later
-   .NET SDK 8.0+
-   PowerShell 7 installed and on PATH

### Clone, build, and run

```powershell
git clone https://github.com/Cosmos-0118/TidyWindow.git
cd TidyWindow

dotnet restore src/TidyWindow.sln
dotnet build src/TidyWindow.sln -c Debug

dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj
```

Refer to [`docs/getting-started.md`](docs/getting-started.md) for optional dependencies, troubleshooting, and installer usage. Latest signed installers are published with each GitHub Release (current release: **3.3.0**). Inside the app, open **Settings ▸ Updates** to query the hosted manifest (`data/catalog/latest-release.json`) and jump straight to the newest installer or release notes without downloading a fresh build manually.

## Testing & Verification

```powershell
dotnet test tests/TidyWindow.Core.Tests/TidyWindow.Core.Tests.csproj
dotnet test tests/TidyWindow.Automation.Tests/TidyWindow.Automation.Tests.csproj
dotnet test tests/TidyWindow.App.Tests/TidyWindow.App.Tests.csproj
```

-   Tools in `tools/` validate catalog consistency (`check_duplicate_packages.py`, `suggest_catalog_fixes.py`) and PowerShell flows (`test-process-catalog-parser.ps1`).
-   PulseGuard and the Activity Log offer runtime confirmation that long-running automations completed or surfaced actionable errors.

## Documentation Map

-   [`docs/cleanup.md`](docs/cleanup.md) – Disk cleanup workflow, risk model, and automation scheduler.
-   [`docs/essentials.md`](docs/essentials.md) – Repair catalog, queue orchestration, and safety features.
-   [`docs/deep-scan.md`](docs/deep-scan.md) – Diagnostics, heuristics, and anti-system scanners.
-   [`docs/install-hub.md`](docs/install-hub.md) / [`docs/maintenance.md`](docs/maintenance.md) – Package installation & upkeep cockpit.
-   [`docs/pathpilot.md`](docs/pathpilot.md) – PATH governance with diff previews and rollback plans.
-   [`docs/registry-optimizer.md`](docs/registry-optimizer.md) – Restore-point-backed registry tuning.
-   [`docs/known-processes.md`](docs/known-processes.md) – Process catalog, classifications, and Anti-System signals.
-   [`docs/activity-log.md`](docs/activity-log.md) – Observability pipeline, transcripts, and PulseGuard integration.
-   [`docs/settings.md`](docs/settings.md) – Preference system, PulseGuard toggles, and background presence.
-   [`docs/tech-stack.md`](docs/tech-stack.md) – Full technology breakdown.

## Support, Contributions, and Roadmap

-   Follow the guidance in `docs/` when extending pages or automation flows to preserve safety guarantees.
-   File issues with Activity Log snippets, cleanup reports, or automation transcripts for faster triage.
-   Track future work in [`roadmap.md`](roadmap.md) and GitHub Discussions.

## License & Credits

TidyWindow ships under the MIT License (see [`LICENSE`](LICENSE)).

The project is built with github copilot assistance alongside contributions from the Windows, PowerShell, winget, Scoop, and Chocolatey ecosystems.
