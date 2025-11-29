# Cleanup Page Documentation

## Overview

The Cleanup page surfaces a guided workflow for reclaiming disk space without risking accidental data loss. It combines a high-performance .NET scanner with rich filtering, process lock inspection, and a multi-phase UI that walks users from target discovery to confirmation and post-run summaries.

## Purpose

-   **Discover Clutter**: Run a fast preview that ranks reclaimable folders and files across downloads, browser caches, and system temp areas.
-   **Vet Risk**: Sort by size, age, or heuristic risk, filter by extension, and inspect drill-down details before selecting anything for deletion.
-   **Remove Safely**: Execute deletions with recycle bin fallback, retry logic, and optional permission repair while skipping protected locations.
-   **Handle Locks**: Detect apps that are holding files open and close them gracefully (or forcefully) before cleanup.
-   **Automate Cleanup**: Schedule recurring sweeps that trim only the top offenders using conservative defaults, or export a detailed report for audits.

## Safety Features

### Why Cleanup Operations Are Safe

#### 1. **Protected System Guardrails**

-   `CleanupService` maintains a list of protected roots (Windows, Program Files, AppData system folders) and automatically skips any path that resolves under them.
-   Hidden and system attributes are respected when `SkipHiddenItems` or `SkipSystemItems` is turned on (defaults applied in automation and confirmation sheets).

#### 2. **Recycle Bin Preference With Fallbacks**

-   Users can opt to move selections into the Recycle Bin (`PreferRecycleBin`), with an automatic fallback to ordinary deletion if the shell refuses the move.
-   Permanent deletes are only attempted when the fallback path is explicitly enabled.

#### 3. **Recent File Protection**

-   The confirmation sheet highlights recently modified files (≤ 3 days) and the engine respects the "skip recent" option to avoid wiping active work.
-   Age filters (7/30/90/180+ days) give an extra layer of protection before items ever reach the queue.

#### 4. **Lock Inspection & Process Control**

-   The lock inspector samples up to 600 of the largest selected paths, asks `ResourceLockService` for handle owners, and surfaces a list of blocking apps.
-   Users can close, force close, or ignore locks; the deletion run automatically skips busy items unless explicit force modes are configured.

#### 5. **Permission Repair Options**

-   When enabled, `CleanupDeletionOptions.TakeOwnershipOnAccessDenied` attempts to clear ACL obstacles and retries deletions.
-   The engine only escalates to force-delete flows after all softer attempts fail, and it documents those actions in the Activity Log.

#### 6. **Browser History Safety**

-   Microsoft Edge history targets are cleared via `IBrowserCleanupService.ClearEdgeHistoryAsync`, which calls official APIs instead of deleting database files.
-   If the Edge API refuses the request, the entry remains untouched and the warning is logged.

#### 7. **Sequential, Observable Execution**

-   Deletions run sequentially with a progress dispatcher that updates the UI every ~120 ms.
-   Each deletion result (`CleanupDeletionEntry`) is aggregated into transcripts that feed the status message, celebration view, and optional cleanup report.

#### 8. **Phase-Based UX Guardrails**

-   Users must pass through Setup → Preview → Confirmation phases; destructive actions are hidden during preview.
-   The confirmation sheet centralises risk warnings, recycle bin and force options, and locking app tooling before deletion can start.

## Architecture

```
┌──────────────────────────────────────────────┐
│              CleanupPage.xaml                │
│             (View – Container)               │
└──────────────┬───────────────────────────────┘
               │
               ▼
┌──────────────────────────────────────────────┐
│           CleanupViewModel.cs                │
│      (ViewModel – Business Logic)            │
└───────┬───────────────┬──────────────┬─────-─┘
        │               │              │
        ▼               ▼              ▼
┌───────────────┐ ┌───────────────┐ ┌─────────────────────┐
│CleanupService │ │ResourceLock   │ │CleanupAutomation    │
│(.Core)        │ │Service        │ │Scheduler            │
└─────┬─────────┘ └────┬──────────┘ └─────────┬───────────┘
      │                │                      │
      ▼                ▼                      ▼
┌─────────────-┐ ┌──────────────┐ ┌────────────────────┐
│Browser       │ │IRelativeTime │ │CleanupAutomation   │
│CleanupService│ │Ticker        │ │SettingsStore       │
└─────────────-┘ └──────────────┘ └────────────────────┘
```

### Key Components

-   **`CleanupViewModel`** (`src/TidyWindow.App/ViewModels/CleanupViewModel.cs`)

    -   Orchestrates setup/preview/celebration phases, filtering, and selection state.
    -   Builds `CleanupDeletionOptions`, invokes `CleanupService.DeleteAsync`, and logs outcomes.
    -   Manages lock inspection, automation panel state, and celebration analytics.

-   **`CleanupService`** (`src/TidyWindow.Core/Cleanup/CleanupService.cs`)

    -   Scans filesystem metadata and executes deletions with retry, recycle-bin fallback, delete-on-reboot, and ACL repair.
    -   Guarantees deterministic handling of protected paths and detail-rich result objects.

-   **`ResourceLockService`** (`src/TidyWindow.App/Services/ResourceLockService.cs`)

    -   Queries running processes for open handles to sampled paths and exposes graceful/force close flows.

-   **`CleanupAutomationScheduler`** (`src/TidyWindow.App/Services/Cleanup/CleanupAutomationScheduler.cs`)

    -   Runs preview/deletion cycles on a timer using top-N items, with guard rails to avoid long-running batches.

-   **`BrowserCleanupService`** (`src/TidyWindow.App/Services/BrowserCleanupService.cs`)

    -   Integrates with Edge profiles to clear history through supported APIs.

-   **`CleanupTargetGroupViewModel` / `CleanupPreviewItemViewModel`** (`src/TidyWindow.App/ViewModels/CleanupTargetGroupViewModel.cs`)
    -   Represent category-level groupings (Downloads, Temp, Browser, etc.) and individual preview items with selection state, signals, and metadata badges.

## User Interface

### Setup Phase

-   **Hero Section**: Summarises last run, reclaimed MB, and suggested next action.
-   **Scope Toggles**: Include Downloads, include browser history, and preview size slider (10–100 000 items).
-   **Filters**:
    -   Item type (files/folders/both), extension mode (include/exclude/custom), and extension profiles (Documents, Images, Archives, Logs, etc.).
    -   Age filter presets and sort modes (Largest, Newest, Risk).
-   **Run Button**: Launches `RunPreviewAsync` to generate a fresh report.

### Preview Phase

-   **Target Shelf**: Grid of category cards showing remaining items, total size, warnings, and quick-select toggles.
-   **Item List**: Virtualised list with icon, name, location, size, age, signals, and risk chips; supports paging (50 items/page by default) and select-all per page.
-   **Selection Summary**: Live aggregate of selected item count and size.
-   **Risk Panel**: Shows warnings for recent, protected, or locked files.
-   **Action Buttons**: Delete selected, clear selection, or return to setup.

### Confirmation Sheet

-   Opens when Delete is invoked and includes:
    -   Item count, total MB, and category breakdown.
    -   Toggles for Recycle Bin, generate report, skip locked items, repair permissions.
    -   Locking process panel with refresh/close controls.
    -   “Confirm cleanup” button guarded by run popup and disable logic when no items remain.

### Locking Process Popup

-   Lists apps/services holding handles, with metadata, impacted item count, and close/force close commands.
-   Provides context such as restartable services and whether an entry is critical.

### Celebration Phase

-   Displays reclaimed size, deletion stats, skipped/failed counts, categories touched, duration, and shareable summary text.
-   Provides direct link to generated report (if requested) and enumerates skipped/failed items with reasons.

### Automation Panel

-   Configure top-item limit (10–5000), interval (1h–30d), deletion mode (Skip Locked, Move to Dustbin, Force Delete), and scope toggles.
-   Shows last automation run, current status, and pending configuration changes.

## Workflow

1. **Scope Preview**

    - User adjusts toggles/filters and runs a preview.
    - `CleanupService.PreviewAsync` returns grouped targets which are filtered and sorted before rendering.

2. **Inspect & Select**

    - User pages through items, uses extension/age filters, and selects items.
    - Selection summary and risk panel update in real time.

3. **Confirm & Inspect Locks**

    - Confirmation sheet aggregates selection stats, kicks off lock inspection, and surfaces blocking apps.
    - Users choose options (Recycle Bin, skip locked, permission repair) and resolve locks as needed.

4. **Delete**

    - `ConfirmCleanupAsync` gathers current selections, merges Edge-history results, and calls `CleanupService.DeleteAsync` with progress reporting.
    - Results feed status messages, Activity Log entries, and optional cleanup report generation.

5. **Celebrate & Review**

    - Celebration tracks items deleted/skipped/failed, categories touched, reclaimed size, time saved estimates, and share summary.
    - Failures remain selected for further action; users can export the report or rerun preview.

6. **Automate (Optional)**
    - Automation scheduler periodically runs preview/deletion using top-N items and configured deletion mode.
    - Scheduler logs runs to Activity Log and respects lock skip/force preferences.

## Safety Mechanisms in Detail

-   **Protected Roots**: `CleanupService.IsProtectedPath` prevents deletions under `%SystemRoot%`, `%ProgramFiles%`, `%ProgramData%`, and other sensitive directories.
-   **Retry & Delete-on-Reboot**: For stubborn files, the engine retries with exponential backoff, clears attributes, renames to tombstones, or schedules delete-on-reboot when allowed.
-   **Skips for Missing Items**: If an item disappears between preview and deletion, the result is marked as skipped with reason preserved in transcripts.
-   **Signal-Based Risk**: Items flagged with lock/permission warnings bubble additional risk markers in the UI.
-   **Activity Logging**: Every deletion run writes structured entries (success or error) with counts, duration, and report details.
-   **Report Generation**: Optional JSON+Markdown reports capture disposition per item for audit trails.

## Automation Details

-   **Settings Store**: `CleanupAutomationSettingsStore` persists interval, top-item count, and deletion mode.
-   **Top-N Strategy**: Automation trims only the highest-impact items, respecting filters and guarding against long-running mass deletions.
-   **Browser History & Downloads**: Automation can include downloads/history independently of manual selections.
-   **Work Tracking**: Integrates with `IAutomationWorkTracker` so background runs appear in the global Activity Log and tray notifications.

## Best Practices

### For Users

1. **Preview Frequently**: Run previews before toggling automation so you understand high-impact categories.
2. **Start With Recycle Bin**: Leave the Recycle Bin option enabled until you trust the filters for your environment.
3. **Review Locks**: Close apps/services that hold handles instead of forcing deletes whenever possible.
4. **Leverage Reports**: Generate cleanup reports for compliance or to review skipped/failed items later.
5. **Set Age Filters**: Tighten age filters when cleaning shared or collaborative folders.

### For Developers

1. **Extend via Targets**: Add new cleanup targets in `CleanupDefinitionProvider` with clear risk metadata.
2. **Respect Guardrails**: Always update `ProtectedRoots` when adding new system-sensitive areas.
3. **Emit Signals**: Provide descriptive signals (`CleanupPreviewItem.Signal`) to surface context in the UI.
4. **Keep Automation Conservative**: Default new automation behaviours to safe values and expose toggles in confirmation sheets.
5. **Log Rich Context**: Include item counts, sizes, and failure reasons in Activity Log entries for diagnostics.

## Technical Notes

-   **Concurrency**: Scanning runs on background threads and delivers results through `PreviewPagingController` to keep the UI responsive.
-   **Virtualisation**: Item grids use `CollectionView` paging to avoid rendering thousands of rows at once.
-   **Relative Time**: `IRelativeTimeTicker` keeps duration displays (e.g., last automation run) fresh without expensive recomputations.
-   **Lock Sampling**: To avoid handle storms, lock inspection samples by category size, capping at 600 paths per run.

## Future Enhancements

-   Incremental previews that only refresh changed folders.
-   Richer diffing between sequential runs.
-   Power scheduling rules for automation (run only on AC / idle).
-   Integration hooks for third-party secure erase tools.

