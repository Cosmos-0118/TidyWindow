# UI for Processes & Anti-System Helper Roadmap

## 1. Scope and Inputs

-   **Specs**: `uiforprocesses.txt` defines the three-tab layout (Known Processes, Settings, Anti-System) and the first-run questionnaire.
-   **Service catalog**: `listofknown.txt` supplies grouped processes/services plus safe/unsafe guidance for tab 1 cards and auto-stop defaults.
-   **Suspicious activity**: `anitsystem.txt` outlines the four-layer detection model, whitelisting, and user actions shown in tab 3.

## 2. Key Requirements Snapshot

-   **First-run questionnaire**
    -   Ask capability/usage questions (touchscreen, printer, VR, gaming, telemetry tolerance).
    -   Map answers to category toggles (e.g., “No printer” ⇒ allow stopping `Spooler`).
    -   Persist responses for reuse and allow reset from settings.
-   **Tab 1 – Known Processes**
    -   Card layout mirroring DeepScan: show name, category, status (Keep/Auto-stop), short rationale, actions (`Restart`, `Stop`, `Learn more`).
    -   Initial state driven by questionnaire + `listofknown.txt` safety guidance.
    -   Inline badges for caution items (e.g., BITS, WaaSMedicSvc).
-   **Tab 2 – Settings & Auto-stop**
    -   Toggle per process to switch between Keep/Auto-stop, overriding questionnaire defaults.
    -   Button “Show auto-stop processes” listing currently configured stops with remove/edit actions.
    -   Controls to rerun questionnaire and export/import preferences.
-   **Tab 3 – Anti-System Watch**
    -   For each suspicious process hit: card with file path, suspicion level (Green/Yellow/Orange/Red), matched rules, and actions (`Mark harmless`, `Scan`, `Open location`, `Stop`).
    -   Respect the four-layer flow: critical process path checks, Defender/hash/local blocklist checks, top 5 behavior rules, user confirmation before kill.
    -   Persist harmless marks and integrate with auto-stop list when the user chooses to stop items.
-   **Persistence & Telemetry**
    -   Store questionnaire answers, auto-stop overrides, harmless whitelist, and anti-system decisions (JSON or lightweight DB).
    -   Audit log for start/stop actions to aid troubleshooting.

## 3. Implementation Phases

### Phase 0 – Foundations (✅ Done)

-   Added the shared data contracts (`ProcessCatalogEntry`, `ProcessPreference`, `SuspiciousProcessHit`) under `src/TidyWindow.Core/Processes` with enums covering risk, preference sources, and suspicion levels.
-   Built a resilient `ProcessCatalogParser` that ingests `listofknown.txt`, preserves caution guidance, and surfaces structured `ProcessCatalogSnapshot` data for the UI.
-   Introduced a JSON-backed `ProcessStateStore` (with schema versioning + migration hooks) ready to persist questionnaire answers, auto-stop overrides, and anti-system decisions (preferences + detection history already flow through it).

### Phase 1 – Questionnaire Engine (✅ Done)

-   Shipped the `ProcessQuestionnaireEngine` + models (`ProcessQuestion`, `ProcessQuestionnaireDefinition`, `ProcessQuestionnaireResult`) to encapsulate the first-run flow and scoring rules that map answers to catalog categories/process IDs.
-   Added questionnaire persistence (`ProcessQuestionnaireSnapshot`) inside `ProcessStateStore`, including schema v2 with immutable answers + derived auto-stop identifiers and helper APIs to reload/clear state.
-   Engine now normalizes answers, evaluates declarative rules, and syncs questionnaire-sourced `ProcessPreference`s (respecting user overrides) while emitting telemetry-friendly notes for the UI layer.

### Phase 2 – Known Processes Tab (✅ Done)

-   Added the Known Processes navigation entry + page wired through DI with caching so questionnaire-backed recommendations hydrate automatically on first load.
-   Implemented DeepScan-inspired cards with category expanders, caution badges, inline override toggles, status summary chips, and empty-state messaging when no recommendations are present.
-   Created `ProcessControlService` + confirmation prompts so stop/restart commands target the underlying Windows services safely, log outcomes, and display actionable feedback per card.
-   Surfaced questionnaire/user override provenance on each card, updating summary text and persistence whenever preferences change.

### Phase 3 – Settings Tab (✅ Done)

-   Expanded the Settings tab with a searchable, category-aware table of every catalog entry plus instant Keep/Auto-stop toggles that persist preferences via `ProcessStateStore`.
-   Added an auto-stop spotlight (expander + summary chips) showing all currently configured stops with inline “switch to keep” actions.
-   Introduced questionnaire rerun flow (dialog backed by `ProcessQuestionnaireEngine`) alongside reset, export, and import controls wired to the JSON snapshot format for easy portability.

### Phase 4 – Anti-System Detection Service (✅ Done)

-   Added `AntiSystemDetectionService` with the four-layer flow (critical path verification, Defender/blocklist intel, top 5 behavioral rules, startup review) and deterministic hit IDs stored via `ProcessStateStore`.
-   Implemented whitelist persistence (directories, hashes, process names) plus schema v3 upgrades so trusted entries survive restarts and skip detections immediately.
-   Wired threat-intel providers (Windows Defender runner + local malware hash blocklist backed by `data/security/malware-blocklist.json`) with tunable hash budget + telemetry summaries.
-   Introduced dedicated models (process/startup snapshots, detection requests/results) and unit tests covering critical path checks, whitelist behavior, temp heuristics, and blocklist hits.

### Phase 5 – Anti-System Tab UI (✅ Done)

-   Added the Anti-System navigation entry + WPF page with severity buckets (Red/Orange/Yellow), rule badges, and inline status messaging sourced from persisted hits or live scans.
-   Wired actionable commands per hit: whitelist (directory + process), Defender rescan, open file location, dismiss, and quarantine (kills matching processes) with status toasts.
-   Connected the page to the new `AntiSystemScanService`, enabling one-click scans that collect running processes/startup entries, feed the detection service, and update telemetry summaries.

### Phase 6 – Integration, Telemetry, QA (2-3 days)

-   Ensure auto-stop list syncs between tabs and anti-system actions.
-   Add logging + user notifications(pulseguard) for process actions and detection changes.
-   Write automated tests: questionnaire mapping, catalog parser, detection pipeline, persistence round-trips.
-   Conduct UX review comparing with DeepScan/InstallHub layouts and adjust spacing/styling.

## 4. Deliverables

-   Structured catalog + persistence services.
-   Questionnaire UI and state machine.
-   Three-tab UI with synchronized state.
-   Anti-system detection engine with whitelist/blocklist support.
-   Test suite + documentation updates (README section, user guide snippet).
