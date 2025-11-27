# Simple Uninstaller – Phase 0 Guardrails

_Phase status: Completed 2025-11-27_

## MVP Definition

-   Enumerate at least 95% of Add/Remove Programs entries exposed through HKLM/HKCU (32-bit + 64-bit) plus `winget list` overlays.
-   Launch the vendor-provided uninstaller (MSI, quiet uninstall string, standard uninstall string, or optional winget fallback) and surface success/failure with exit codes.
-   Never delete arbitrary files or registry keys automatically; all cleanup remains post-MVP and opt-in.
-   Keep the UI focused on orchestration (status, progress, dry-run indicator) instead of “deep cleaning”.

## Guarantee Gap Communication

-   No deep scanning, driver/service removal, or heuristic file deletions will ship with MVP.
-   Messaging must make it clear that leftovers can remain and that invasive removals are out-of-scope without explicit user consent.
-   Documentation and UX copy will reference this file so customer-facing strings stay aligned with the supported surface area.

## Telemetry, Diagnostics, and Sandbox Requirements

-   Every uninstall request is funneled through the new guardrail primitives in `src/TidyWindow.Core/Uninstall`:
    -   `UninstallOperationPlan` describes the allowed command set (MSI, quiet uninstall, uninstall string, winget fallback) and blocks ad-hoc `cmd.exe`/PowerShell payloads that could delete files.
    -   `UninstallSandbox` executes the plan, captures timestamped command snapshots, exit codes, stdout/stderr, and publishes an `UninstallTelemetryRecord` via `IUninstallTelemetrySink`.
-   Dry-run mode is built into the plan. When `plan.DryRun` is `true`, the sandbox logs "would execute" entries for every command without launching a process. The UI can surface this by toggling the same flag.
-   Telemetry payloads intentionally include:
    -   Operation id, timestamp window, dry-run bit, DisplayName/Publisher/Version context.
    -   Each command snapshot’s command line, duration, exit code, output/error text, and whether it was simulated.
    -   Metadata bag for future diagnostics (e.g., source registry hive, catalog match).
-   `NoOpUninstallTelemetrySink` lets us keep instrumentation on even before wiring the backend; swapping in a real sink later does not require altering the guardrails.

## Operational Guardrails

-   Only official uninstallers are allowed in Phase 0 by construction; if a plan attempts to call `cmd.exe` or PowerShell inline, `UninstallOperationPlan` throws.
-   Winget fallback stays opt-in (`IncludesWingetFallback`), enabling UI toggles to remain truthful.
-   All future orchestration (Phase 1/2) should instantiate plans via the helper factory methods so MSI GUID handling and winget arguments stay consistent.

## Deliverables

-   Guardrail code base: `src/TidyWindow.Core/Uninstall/UninstallOperationPlan.cs`, `src/TidyWindow.Core/Uninstall/UninstallSandbox.cs`.
-   Telemetry + dry-run checklist captured in this document for UX/documentation alignment.
-   Ready for Phase 1 enumeration work; the MVP acceptance bar is locked and referenced by roadmap + design docs.
