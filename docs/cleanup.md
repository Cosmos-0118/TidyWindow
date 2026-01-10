# Cleanup Page

## What the Cleanup page does

The Cleanup page walks users through discovering reclaimable disk space, vetting risky targets, and deleting selected items with observable progress. It pairs a fast .NET scanner with guarded deletion flows, Edge history cleanup, lock inspection, and optional automation for unattended runs.

## Core flow (Setup → Preview → Confirm → Delete → Celebrate)

-   **Setup**: Toggle Downloads and Edge history scope, choose item kind (files, folders, both), set the preview size (10–100,000; default 50), pick extension filters/profiles (Documents, Spreadsheets, Images, Media, Archives, Logs), select age presets (all/7/30/90/180 days), and choose sort (Impact, Newest, Risk).
-   **Preview**: Grouped targets show counts, size, and warnings. A paged item list (default 50 per page) supports select-all per page and category-level selection. Live summaries show selected count/size and risk signals.
-   **Confirmation**: When Delete is invoked, the sheet shows totals, category breakdown, and risk highlights. Toggles: Recycle Bin, generate report, skip locked items (default on), repair permissions (take ownership), lock panel with refresh/close/force-close controls, and a run popup before proceeding.
-   **Delete**: `CleanupService.DeleteAsync` runs sequentially with progress updates (~120 ms cadence), recycle-bin preference, retries, delete-on-reboot fallback, and optional permission repair. Edge history items are cleared via WebView2 APIs instead of file deletion. Activity Log entries and optional JSON+Markdown reports capture outcomes.
-   **Celebrate**: Shows reclaimed size, deleted/skipped/failed counts, categories touched, duration, time-saved estimate, share text, and report link when generated. Failures remain selected for follow-up.

## Safety guardrails

-   **Protected paths**: `CleanupService` refuses to delete under critical Windows folders (System32, SysWOW64, WinSxS, SystemApps, SystemResources, servicing, assembly, Installer, Fonts, WindowsApps).
-   **Lock handling**: Up to 32 items per category (600 total) are sampled for lock inspection. `ResourceLockService` surfaces locking apps; users can close or force close them, and deletions default to skipping locked entries unless overridden.
-   **Permission repair (opt-in)**: When enabled, the engine clears attributes, takes ownership on access denied, retries, then falls back to force-delete or delete-on-reboot.
-   **Recycle-bin preference**: Users can request recycle-bin moves; permanent delete fallback is allowed in manual runs and disabled in automation’s dustbin mode.
-   **Recent/risk awareness**: The confirmation sheet calls out items modified in the last 3 days, protected locations, and lock signals before deletion.
-   **Progress and logging**: Every deletion result is captured; missing items are marked skipped; progress reporting throttles UI churn while remaining responsive.

## Defaults and limits

-   Preview count: default 50; min 10, max 100,000.
-   Age filters: all, 7, 30, 90, or 180+ days (filtering; not hard safety gates).
-   Sorting: Impact (size), Newest (last modified), Risk (signal-heavy first).
-   Confirmation defaults: Recycle Bin off, reports off, skip locked on, permission repair off.
-   Automation defaults: Disabled; daily interval when enabled; top 200 items (min 50, max 50,000); Skip Locked mode; Downloads and browser history off by default.

## Automation

-   Scheduler persists settings, clamps intervals to 1 hour–30 days, and top-item counts to 50–50,000.
-   Each run previews, sorts by size, trims to the configured top-N, clears Edge history via API when included, deletes with mode-specific options (Skip Locked, Move to Recycle Bin without permanent fallback, or Force Delete with ownership + delete-on-reboot), logs results, and updates last-run status.

## Implementation map

-   UI and flow: [src/TidyWindow.App/ViewModels/CleanupViewModel.cs](src/TidyWindow.App/ViewModels/CleanupViewModel.cs)
-   Deletion engine: [src/TidyWindow.Core/Cleanup/CleanupService.cs](src/TidyWindow.Core/Cleanup/CleanupService.cs)
-   Automation: [src/TidyWindow.App/Services/CleanupAutomationScheduler.cs](src/TidyWindow.App/Services/CleanupAutomationScheduler.cs) and [src/TidyWindow.Core/Cleanup/CleanupAutomationSettings.cs](src/TidyWindow.Core/Cleanup/CleanupAutomationSettings.cs)
-   Lock inspection/closing: [src/TidyWindow.App/Services/ResourceLockService.cs](src/TidyWindow.App/Services/ResourceLockService.cs)
-   Edge history cleanup: [src/TidyWindow.App/Services/BrowserCleanupService.cs](src/TidyWindow.App/Services/BrowserCleanupService.cs)

## Developer notes

-   Add new targets via `CleanupDefinitionProvider` with clear signals and category metadata so preview grouping and risk surfacing stay meaningful.
-   Update protected roots when introducing new system-sensitive areas; the delete pipeline skips them before any retries.
-   Keep automation conservative by default; expose destructive switches (force delete, permission repair) as opt-in and log outcomes for diagnostics.

## Known gaps to watch

-   Protected root list currently covers critical Windows subfolders but not the broader Program Files or user-profile roots; keep selections sane for those paths.
-   Hidden/system file skipping is off by default; pair with filters or consider enabling when adding higher-risk targets.
-   Users must pass through Setup → Preview → Confirmation phases; destructive actions are hidden during preview.

