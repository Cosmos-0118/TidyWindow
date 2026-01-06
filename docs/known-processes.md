# Known Processes Page Documentation

## Overview

The Known Processes workspace helps administrators tame noisy background services and suspicious executables. It combines a curated catalog of Windows services, user-defined preferences, and a live Threat Watch scanner that keeps watch for untrusted processes. The page is split into three pivots—Known Processes, Process Controls, and Threat Watch—so users can jump between recommendations, policy tuning, and runtime telemetry.

## Purpose

-   **Known Process Guidance**: Surface vetted recommendations for services that can be safely auto-stopped or should remain running.
-   **User Overrides**: Let operators override defaults, capture rationale, and export/import preference sets across machines.
-   **Automation**: Enforce auto-stop policies on a recurring schedule and keep an audit trail of enforcement runs.
-   **Threat Hunting**: Flag unsigned or anomalous processes via Threat Watch scanning and provide tools to quarantine or whitelist them.
-   **Education**: Offer one-click documentation and rationales so users understand why a service is recommended for action.

## Safety Features

### Why Known Process Operations Are Safe

#### 1. **Curated Catalog with Risk Levels**

-   Catalog entries (from `data/catalog/processes/`) include category risk flags, rationales, and recommended actions vetted by the team.
-   Caution categories are visually marked so users slow down before making changes.

#### 2. **Confirmation Prompts**

-   Stopping or restarting a service invokes `IUserConfirmationService` with clear warnings about potential side effects.
-   Quarantine actions in Threat Watch prompt for acknowledgement before terminating processes.

#### 3. **Questionnaire & Overrides**

-   The optional onboarding questionnaire captures environment requirements before any automation is enabled.
-   Preferences store the source (System Default, Questionnaire, User Override) so it’s easy to revert to safer defaults.

#### 4. **Automation Guard Rails**

-   `ProcessAutoStopEnforcer` enforces preferences sequentially, logs every action, and respects a configurable interval (5–120 minutes).
-   Automation runs are skipped if there are no auto-stop targets, preventing unnecessary service churn.

#### 5. **Threat Watch Intel**

-   Threat Watch uses Defender APIs and local heuristics to classify suspicious processes into Critical/Elevated/Watch buckets.
-   Whitelisting and quarantine both persist decisions in `ProcessStateStore` to avoid repetitive prompts and to maintain an audit trail.

#### 6. **Activity Logging**

-   All catalog updates, service actions, automation runs, imports/exports, and Threat Watch verdicts are written to the Activity Log with contextual details.

## Architecture

```
┌──────────────────────────────────────────────┐
│            KnownProcessesPage.xaml           │
│             (View – Container)               │
└──────────────┬───────────────────────────────┘
               │
               ▼
┌──────────────────────────────────────────────┐
│        KnownProcessesViewModel.cs            │
│    (Catalog + Pivot Orchestration)           │
└──────┬──────────────┬───────────────────────-┘
       │              │
       ▼              ▼
┌────────────────--┐ ┌────────────────────────┐
│ProcessPreferences│ │ThreatWatchViewModel.cs  │
│ViewModel.cs      │ │(Threat Watch Scanner)   │
└──────┬───────────┘ └──────┬─────────────────┘
       │                    │
       ▼                    ▼
┌──────────────┐   ┌────────────────────────┐
│ProcessState  │   │ThreatWatchScanService.cs│
│Store.cs      │   │ProcessAutoStopEnforcer │
└──────────────┘   └────────────────────────┘
       │
       ▼
┌──────────────────────────┐
│ProcessCatalogParser.cs   │
│ProcessControlService.cs  │
└──────────────────────────┘
```

### Key Components

-   **`KnownProcessesViewModel`** (`src/TidyWindow.App/ViewModels/KnownProcessesViewModel.cs`)

    -   Loads catalog entries, maps preferences, and tracks the active pivot (Known Processes, Process Controls, Threat Watch).
    -   Coordinates service control operations and logs outcomes.

-   **`ProcessPreferencesViewModel`** (`src/TidyWindow.App/ViewModels/ProcessPreferencesViewModel.cs`)

    -   Provides filtering, segment views, import/export, questionnaire prompts, and automation controls for process preferences.

-   **`ProcessStateStore`** (`src/TidyWindow.Core/Processes/ProcessStateStore.cs`)

    -   Persists questionnaire snapshots, user overrides, whitelist/quarantine entries, and auto-stop automation settings.

-   **`ProcessAutoStopEnforcer`** (`src/TidyWindow.App/Services/ProcessAutoStopEnforcer.cs`)

    -   Applies auto-stop rules on demand or on a schedule, using Windows service APIs to stop targets safely.

-   **`ProcessControlService`** (`src/TidyWindow.App/Services/ProcessControlService.cs`)

    -   Wraps service controller operations with retry and status reporting (Stop, Restart).

-   **`ThreatWatchViewModel` + `ThreatWatchScanService`**
    -   Scan running processes using heuristics and Defender intel, track results, and support remediation workflows.

## User Interface

### Known Processes Pivot

-   **Category Accordion**: Displays known process groups with risk badges and descriptions.
-   **Process Cards**: Show display name, rationale, recommendation (Auto-stop or Keep), action toggles, and notes.
-   **Actions**:
    -   Toggle action (Auto-stop ↔ Keep).
    -   Stop/Restart service buttons (when supported).
    -   Learn more opens Microsoft Docs search for the service.
-   **Summary Banner**: Highlights how many processes are set to auto-stop.

### Process Controls Pivot

-   **Process Table**: Filterable list of known process entries with columns for current action, source, last updated, and notes.
-   **Segments**: Logical groupings (e.g., "Developer tools", "OEM services") to help focus on related processes.
-   **Filters**: Search box, "Auto-stop only" toggle, segment quick filters.
-   **Questionnaire**: Launches first-run questionnaire to seed recommended preferences.
-   **Automation Panel**:
    -   Enable/disable auto-stop automation, set interval, view last run, and apply settings.
    -   Manual "Run now" button re-enforces preferences immediately.
-   **Import/Export**: Save or load JSON bundles of preferences for team rollout.
-   **Reset**: One-click reset clears questionnaire data and user overrides (with confirmation).

### Threat Watch Pivot

-   **Severity Buckets**: Columns for Critical, Elevated, and Watch hits with counts and color coding.
-   **Hit List**: Each entry shows process name, file path, detection rationale, and latest action.
-   **Actions**:
    -   Refresh scan, whitelist directory or process, ignore, quarantine (terminates running instances), scan file via Defender, open file location.
-   **Filters**: Search text and severity drop-down.
-   **Summary**: Last scan timestamp and quick stats on hit counts.

## Workflow

1. **Initialization**

    - On first load, `KnownProcessesViewModel.RefreshAsync` parses catalog JSON/YAML, merges stored preferences, and populates category cards.
    - Settings pivot loads questionnaire snapshot and automation settings.
    - Threat Watch pivot reads persisted hits before the first manual scan.

2. **Known Processes Review**

    - Users expand categories, review rationales, and toggle actions. Each toggle updates `ProcessStateStore` and summary counts.
    - Optional service control actions (Stop/Restart) go through confirmation prompts and log outcomes.

3. **Preference Tuning**

    - Settings pivot provides bulk filtering and editing, import/export, and questionnaire re-run.
    - Applying automation settings writes to `ProcessAutoStopEnforcer` and optionally enforces immediately.

4. **Automation**

    - `ApplyAutoStopAutomationAsync` persists settings and optionally runs enforcement; background scheduler enforces thereafter.
    - Manual "Run now" triggers `ProcessAutoStopEnforcer.RunOnceAsync` on demand.

5. **Threat Watch Response**
    - Users run scans, triage hits, whitelist legitimate software, or quarantine suspicious binaries.
    - All outcomes update `ProcessStateStore` so repeated scans honour earlier decisions.

## Automation Details

-   **Intervals**: 5, 10, 15, 30, 60, or 120 minutes; clamped to `ProcessAutomationSettings` min/max.
-   **Targets**: Only entries whose effective action is Auto-stop are enforced.
-   **Execution**: Enforcer operates on a background thread, respects cancellation, and logs the number of services acted upon.
-   **Status Messaging**: Automation status shows disabled/enabled state, interval string, last run relative time, and pending changes flag.

## Data Models

-   **`KnownProcessCardViewModel`**: Wraps catalog entries with state, toggle labels, and action messages.
-   **`ProcessPreferenceRowViewModel`**: Drives the Settings list, including last change time and source badge.
-   **`ThreatWatchHitViewModel`**: Represents suspicious processes with severity, last action message, and command handlers.
-   **`ProcessPreference`**: Stored in `ProcessStateStore`; captures process identifier, action, source, timestamp, and optional notes.
-   **`ProcessAutomationSettings`**: Persisted automation configuration (enabled flag, interval, targets, last run).

## Best Practices

### For Users

1. **Start with the Questionnaire**: Seed your environment with sensible defaults before enforcing automation.
2. **Review Caution Categories**: Treat yellow-marked catalog entries as advisory; verify business impact before toggling.
3. **Use Import/Export for Teams**: Share JSON bundles to keep preferences consistent across fleets.
4. **Whitelist Intentionally**: Threat Watch whitelists persist; document notes so future reviewers know why an entry is trusted.
5. **Monitor Activity Log**: Service actions and automation runs are logged—use the Activity Log page for auditing.

### For Developers

1. **Document Catalog Updates**: Provide descriptive rationales and risk levels when adding new catalog entries.
2. **Maintain Questionnaire Flow**: Keep questions in sync with catalog changes and environment assumptions.
3. **Guard Long Operations**: Any new service automation should run sequentially and surface progress to `MainViewModel`.
4. **Extend Threat Watch Carefully**: New heuristics should default to Watch level until validated; always log evidence strings.
5. **Respect User Overrides**: Never overwrite explicit user choices during catalog refreshes or automation runs.

## Technical Notes

-   **Data Sources**: Catalog parser supports YAML with include files; state store serializes JSON under `%ProgramData%/TidyWindow/process-state`.
-   **Threading**: UI refreshes are marshalled onto the dispatcher; long-running operations use `Task.Run` with status messages.
-   **Dialogs**: Import/export and Threat Watch holdings use WPF modal windows with owner linking for proper focus management.
-   **Telemetry**: Automation and Threat Watch results feed PulseGuard toasts when noteworthy events occur (e.g., suspicious hits).

## Future Enhancements

-   Multi-machine synchronization of preferences via cloud or file share.
-   Integration with Windows Defender Application Control for policy enforcement.
-   Expanded catalog metadata (e.g., CPU impact, memory footprint) to guide prioritization.
-   Scriptable CLI for headless enforcement on servers.
