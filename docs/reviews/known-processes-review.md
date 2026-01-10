# Known Processes Issues and Improvements (2026-01-10)

**Current rating:** 7/10

**Potential after fixes:** 9/10

## Strengths

-   Clear separation of pivots (catalog, settings, Threat Watch) with persisted state for preferences, questionnaire answers, whitelist, quarantine, and automation runs.
-   Automation is single-threaded, skips when there are no auto-stop targets, and logs outcomes with last-run tracking.
-   Threat Watch actions (whitelist, ignore, scan, quarantine) keep an audit trail and respect user confirmation for destructive steps.

## Key issues and recommended improvements

-   [x] **Service control targets are ambiguous** – Calls use the catalog display name rather than a service name when stopping/restarting or enforcing automation. Any mismatch becomes a no-op or failure. Store and use a dedicated service identifier and validate it during catalog ingestion.
-   [x] **Manual run blocked when automation off** – "Run now" is gated behind the automation-enabled flag. Allow a one-time enforcement without enabling the scheduler for break-glass use.
-   [ ] **Fixed 25s service timeout** – The stop path uses a fixed 25-second timeout and ignores disabled services, causing false failures on slow or disabled services. Add start-type checks and configurable timeouts.
-   [ ] **Limited Threat Watch context** – Snapshots omit command-line and parent-process context, reducing triage fidelity. Capture these fields where permissions allow to improve rule precision and user insight.

