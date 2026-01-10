# Registry Optimizer

This page lets you stage, apply, and revert curated registry tweaks with built-in guardrails. Tweaks can be applied individually or via presets, with custom values validated before any script runs.

## Page Anatomy

-   **Tweaks list**: Cards show name, summary, category, risk, toggle state, recommended value, detected value (when available), and custom value inputs with validation messaging.
-   **Presets**: Published collections that set multiple tweaks at once. Selecting a preset updates selections and persists the choice; customization indicators appear when edits diverge from the preset.
-   **Actions**: Apply (enabled only when pending and valid), Revert to baseline selections, and Restore from the latest restore point. Status text surfaces last operation details and restore point info.

## Safety and Guardrails

-   **System Restore freshness check**: Apply is blocked unless a system restore point newer than 24 hours exists; the guard prompts users to create one via the System Restore flow.
-   **Sequential, logged execution**: Plans are executed one step at a time via PowerShell; outputs/errors are logged to the Activity Log.
-   **Restore points**: After a successful apply with revertable operations, a JSON restore point is written under `%ProgramData%/TidyWindow/RegistryBackups` (max 10 kept, newest-first). Restore points are loaded on page init and can be applied from the UI.
-   **Validation-first**: Custom values must pass type/range checks; Apply is disabled while invalid. No-work plans short-circuit without touching the registry.
-   **Baseline tracking**: Applied states are persisted; Revert resets selections to the last applied baseline without invoking scripts.

## Key Workflows

-   **Apply tweaks**: Pending selections → build plan (`RegistryOptimizerService.BuildPlan`) → guard check → sequential execution (`ApplyAsync`) → persist applied state → attempt restore point save → update summaries.
-   **Restore last point**: Runs stored revert operations (`ApplyRestorePointAsync`), then refreshes state and clears pending changes.
-   **Revert selections**: Resets UI selections and custom values to the last applied baseline; no registry writes occur.
-   **Preset flow**: Selecting a preset updates selections and persists the preset id; subsequent edits mark the preset as customized.

## Data and Persistence

-   **Configuration**: Tweaks/presets from `data/cleanup/registry-defaults.json`; scripts resolved from registry automation paths.
-   **Preferences**: Applied states, selected preset id, and custom values persisted via `RegistryPreferenceService`.
-   **Restore points**: Stored as JSON with tweak selections and revert operations; oldest entries pruned beyond 10.

## Developer Notes

-   Keep tweak definitions populated with identifiers, risk, recommended values, and constraints; missing metadata degrades card UX and validation.
-   Ensure enable/disable operations are symmetric so restore points can revert cleanly.
-   Guard remains 24h by default—adjust `SystemRestoreFreshnessThreshold` in `RegistryOptimizerViewModel` if policy changes.

## Future Enhancements

-   Export/import restore points and applied-state snapshots.
-   Richer state telemetry (detected values, diffs) in the UI.
-   Optional dry-run/report-only mode and per-tweak preview.
-   Dependency hints between tweaks and presets.

