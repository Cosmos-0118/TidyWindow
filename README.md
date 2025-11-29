git clone https://github.com/Cosmos-0118/TidyWindow.git
dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj

# TidyWindow

**Version 2.9.0 · Windows maintenance companion for developers**

TidyWindow orchestrates system setup, cleanup, and repair flows from a single WPF shell. High-impact automations run through managed .NET services that call PowerShell 7 safely in the background, keeping the UI responsive while guaranteeing detailed telemetry.

## Release Snapshot

-   **Latest release**: 2.9.0 (see GitHub Releases for installers and notes)
-   **Installer**: Inno Setup package generated from `installer/TidyWindowInstaller.iss`
-   **Supported OS**: Windows 10+ with .NET SDK 8.0 and PowerShell 7

## What You Can Do

-   **Bootstrap** developer environments with winget, Chocolatey, and Scoop helpers.
-   **Clean Up** disks using the multi-phase workflow documented in [`docs/cleanup.md`](docs/cleanup.md).
-   **Install & Maintain** curated software bundles (`docs/install-hub.md`) and keep packages current (`docs/maintenance.md`).
-   **Tune the Registry** safely with automatic restore points, rollback countdowns, and input validation (`docs/registry-optimizer.md`).
-   **Monitor Processes** via the Known Processes catalog and Anti-System scanner (`docs/known-processes.md`).
-   **Audit Activity** with searchable transcripts and PulseGuard notifications (`docs/activity-log.md`).

## Safety by Design

-   **Registry Optimizer** always creates JSON restore points, prompts for rollback, and prunes older snapshots so changes can be reverted instantly.
-   **Cleanup Suite** respects protected roots, prefers the recycle bin, inspects locking processes, and surfaces risk warnings before any deletion.
-   **Automation Queues** execute sequentially with full transcripts and Activity Log entries, so you can see exactly what ran.
-   **PulseGuard** turns noteworthy events into actionable toasts without spamming—controls live on the Settings page (`docs/settings.md`).

## Quick Start

```powershell
git clone https://github.com/Cosmos-0118/TidyWindow.git
cd TidyWindow

dotnet restore src/TidyWindow.sln
dotnet build src/TidyWindow.sln -c Debug

dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj
```

See [`docs/getting-started.md`](docs/getting-started.md) for prerequisites, optional tooling, and troubleshooting tips.

## Documentation

-   [`docs/cleanup.md`](docs/cleanup.md) – Disk cleanup workflow and automation.
-   [`docs/essentials.md`](docs/essentials.md) – One-click repair library.
-   [`docs/deep-scan.md`](docs/deep-scan.md) – Diagnostics and classification heuristics.
-   [`docs/install-hub.md`](docs/install-hub.md) – Bundle installer UX.
-   [`docs/maintenance.md`](docs/maintenance.md) – Package maintenance cockpit.
-   [`docs/pathpilot.md`](docs/pathpilot.md) – Runtime PATH management.
-   [`docs/registry-optimizer.md`](docs/registry-optimizer.md) – Safe registry tweaks.
-   [`docs/known-processes.md`](docs/known-processes.md) – Process catalog & Anti-System scanner.
-   [`docs/activity-log.md`](docs/activity-log.md) – Observability and logging.
-   [`docs/settings.md`](docs/settings.md) – Preferences, PulseGuard, background mode.
-   [`docs/tech-stack.md`](docs/tech-stack.md) – Full technology overview.

## Tech Stack Highlights

-   **WPF (.NET 8)** front-end with CommunityToolkit.Mvvm.
-   **C# services** for cleanup, installs, registry state, automation.
-   **PowerShell 7** scripts under `automation/` for OS integrations.
-   **YAML/JSON catalogs** describing bundles, process metadata, and reports.
-   **Inno Setup** installer for release packaging.

## Testing & Quality

```powershell
dotnet test tests/TidyWindow.Core.Tests/TidyWindow.Core.Tests.csproj
dotnet test tests/TidyWindow.Automation.Tests/TidyWindow.Automation.Tests.csproj
dotnet test tests/TidyWindow.App.Tests/TidyWindow.App.Tests.csproj
```

-   Tools in `tools/` help validate catalog integrity and script behaviour (e.g., `test-process-catalog-parser.ps1`).
-   PulseGuard and Activity Log provide runtime verification for automation runs and failures.

## Contributing & Support

-   Follow the guides in `docs/` when extending pages or automation flows.
-   Open issues with Activity Log snippets or cleanup reports for faster triage.
-   Track roadmap updates in `roadmap.md` and discussions in the repository.

## License

TidyWindow is distributed under the MIT License. See [`LICENSE`](LICENSE) for details.

## Credits

-   This project was developed with the assistance of GitHub Copilot.

