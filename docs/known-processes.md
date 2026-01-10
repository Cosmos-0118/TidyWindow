# Known Processes

This page helps administrators keep background services and suspicious executables under control. It combines a curated catalog, user overrides, auto-stop automation, and Threat Watch telemetry. The experience is split into three pivots so you can move between recommendations, bulk settings, and runtime detections without losing context.

## Page Anatomy

-   **Known Processes (Catalog)**: Category accordions show vetted entries with risk badges, rationale, recommended action, and optional notes. Cards let you toggle Auto-stop/Keep, stop or restart supported services (with confirmation), and jump to Microsoft Learn search for the service name. A summary banner counts how many entries are set to auto-stop.
-   **Process Controls (Settings)**: A filterable table of the same catalog plus any orphaned preferences. Includes search, “auto-stop only” filtering, and segment quick toggles by category. First-run questionnaire prompts appear here; import/export and reset live here too.
-   **Threat Watch**: Severity buckets (Critical/Elevated/Watch) backed by live scans. Each hit shows process name, path, matched rules, and last action. Actions include whitelist directory/process, ignore, Defender file scan, open file location, and quarantine (kill + persist record after confirmation).

## Lifecycle and Data Flow

-   On first entry, `KnownProcessesViewModel` loads a catalog snapshot, merges stored preferences from `ProcessStateStore`, builds category cards, and updates the auto-stop summary. It then refreshes process settings and triggers a Threat Watch scan.
-   Preferences are persisted as `ProcessPreference` records with an action, source (Default, Questionnaire, User Override), timestamp, and optional notes. Questionnaire snapshots, whitelist/quarantine decisions, suspicious hits, and automation settings are all stored in `ProcessStateStore`.
-   Threat Watch rehydrates persisted hits on startup, then scans running processes plus startup entries via `ThreatWatchScanService` and `ThreatWatchDetectionService`. Results are grouped by suspicion level and cached for later sessions.

## Catalog Experience

-   Category accordions default to expanded; caution categories stay visually marked. Cards display rationale (falling back to category description when missing), recommendation label, current action, and source badge.
-   Toggle switches immediately write a user override and refresh summary counts. Service Stop/Restart buttons are only enabled for catalog entries that map to concrete services; every action goes through `IUserConfirmationService` and logs to the Activity Log. “Learn more” opens a Microsoft Learn search for the process name.

## Process Controls (Settings) Pivot

-   Search and “auto-stop only” filters drive the table view. Rows include category, current action, source, last updated, and notes. Orphaned preferences (not present in the current catalog) remain visible so they can be cleaned up.
-   Segment quick toggles apply Auto-stop or Keep to all rows in a category, with mixed-state indicators.
-   Import/Export moves preferences, questionnaire answers, whitelist/quarantine entries, and automation settings as JSON. Reset clears questionnaire answers and all overrides after confirmation.
-   The questionnaire can be auto-launched on first visit or re-run on demand. Answers are evaluated by `ProcessQuestionnaireEngine` and applied as overrides.

## Auto-stop Automation

-   Settings: enable/disable flag, interval (clamped to 5–120 minutes), and last-run timestamp. Applying settings persists them and, when enabled, immediately enforces once.
-   Scheduler: `ProcessAutoStopEnforcer` runs sequentially on a timer. It skips when disabled or when there are zero Auto-stop preferences. It logs upcoming runs about one minute before enforcement when targets exist.
-   Enforcement: stops each target service through `ProcessControlService`, updates last-run time, and records successes or failures in the Activity Log. Manual “Run now” is available when automation is enabled.

## Threat Watch

-   Scans: collects running processes (including paths and start times where available) plus startup entries, then evaluates them via detection rules and Defender intel. Results are written to state so they survive app restarts.
-   Filters: text search across process name, path, and matched rules, plus severity drop-down. Quick clear resets filters.
-   Actions: whitelist (process + directory), ignore/remove, Defender file scan, open file location, and quarantine. Quarantine prompts for confirmation, attempts to terminate matching processes, then persists a quarantine record with optional Defender verdict details.
-   Summaries: shows last scan timestamp and counts per severity; summary text highlights total detections or confirms a clean scan.

## Safety and Guardrails

-   Confirmation prompts precede service control and quarantine. Caution categories surface risk before toggling.
-   Automation is single-threaded with a lock to avoid overlapping runs; it records last-run time and upcoming-run notifications only when targets exist.
-   Service control is Windows-only; attempts on non-Windows hosts return a descriptive message instead of throwing.
-   Whitelist and quarantine decisions are persisted to avoid repeated prompts and to keep an audit trail.

## Developer Notes

-   Catalog entries originate from the process catalog parser and should include clear identifiers, recommendations, and rationale. Avoid empty rationales—card text falls back to the category description when missing.
-   Keep questionnaire definitions aligned with catalog changes so recommended actions stay relevant.
-   Detection rules should default to Watch until validated; always include evidence strings so matched-rules text stays meaningful.

