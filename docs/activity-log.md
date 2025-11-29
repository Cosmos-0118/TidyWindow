# Activity Log Documentation

## Overview

The Activity Log keeps a searchable, filterable record of everything TidyWindow does—installer runs, cleanup jobs, automation cycles, PulseGuard prompts, and more. It is backed by an in-memory ring buffer with thread-safe writes so UI components, services, and background automation can all emit diagnostic events without blocking the UI.

## Purpose

-   **Operational Transparency**: Provide one place to review what the app changed, when it happened, and whether it succeeded.
-   **Troubleshooting**: Surface detailed error messages, stack traces, and payload excerpts when automations fail.
-   **Audit Trail**: Offer copy-friendly transcripts for support tickets or compliance reviews.
-   **Notification Backing**: Allow PulseGuard and other prompts to link back to the exact log entry that triggered them.

## Reliability Features

### Why the Activity Log Can Be Trusted

#### 1. **Bounded Memory Footprint**

-   `ActivityLogService` enforces a configurable capacity (default 500 entries) and trims from the tail as new items arrive.
-   Entries are held in a `LinkedList` protected by a lock to prevent race conditions.

#### 2. **Detail Sanitisation**

-   Arbitrary objects, dictionaries, and collections emitted as details are flattened and JSON-serialized where possible.
-   Lines are clamped to 4 096 characters and capped at 500 lines so the UI never becomes unresponsive.

#### 3. **Thread-Safe Broadcast**

-   Writers call `LogInformation/LogSuccess/LogWarning/LogError`; the service raises an `EntryAdded` event that the view listens to.
-   The view model dispatches onto the UI thread when updates originate from background automation.

#### 4. **Clipboard-Safe Output**

-   Copy operations produce a timestamped, leveled transcript compatible with plaintext editors and ticketing systems.
-   When the clipboard is inaccessible (e.g., remote sessions), failures are swallowed to avoid interrupting review flow.

#### 5. **PulseGuard Integration**

-   PulseGuard notifications include a "View log" action that jumps directly to the entry, reinforcing traceability.

## Architecture

```
┌──────────────────────────────────────────────┐
│               LogsPage.xaml                  │
│             (View – Container)               │
└──────────────┬───────────────────────────────┘
               │
               ▼
┌──────────────────────────────────────────────┐
│             LogsViewModel.cs                 │
│     (Filtering, Selection, Commands)         │
└──────────────┬───────────────────────────────┘
               │
               ▼
┌──────────────────────────────────────────────┐
│           ActivityLogService.cs              │
│  (Thread-safe log buffer + event source)     │
└──────────────────────────────────────────────┘
```

### Key Components

-   **`LogsViewModel`** (`src/TidyWindow.App/ViewModels/LogsViewModel.cs`)

    -   Manages an `ObservableCollection` of `ActivityLogItemViewModel`, sets up sorting, filtering, auto-select-latest, and clipboard copy commands.
    -   Uses `UiDebounceDispatcher` to throttle search refreshes for smooth UX.

-   **`ActivityLogService`** (`src/TidyWindow.App/Services/ActivityLogService.cs`)

    -   Stores entries, normalises details, dispatches updates, and exposes helper methods for common log levels.

-   **`ActivityLogItemViewModel`**
    -   Formats timestamps, maps enum levels to badges, provides search helpers, and builds copy-friendly text blocks.

## User Interface

-   **Filter Bar**: Level drop-down (All/Info/Success/Warning/Error) and a search box. Searches match source, message, and detail lines.
-   **Entries List**: Timestamp (local time), level badge, source, and message. Selecting an entry reveals full details in the side panel.
-   **Auto-Select Toggle**: When enabled, the newest entry is an auto-selection target; toggling off lets users lock focus on historical entries.
-   **Commands**:
    -   `Copy` (context menu or keyboard) copies the selected entry in transcript format.
    -   `Clear` isn’t exposed in the UI by design; logs remain available until capacity trims them.

## Workflow

1. **Emit Entries**

    - Any service can call `ActivityLogService.LogXXX`. The entry is timestamped and appended at the head of the buffer.

2. **Broadcast & UI Update**

    - `EntryAdded` fires; the view model inserts the entry at index 0 (newest-first). The collection is trimmed to match capacity.
    - If auto-select is on, the new entry becomes the selected row.

3. **User Interaction**

    - Filters instantly re-query the `CollectionView` thanks to the WPF filter predicate.
    - Copy command builds text via `BuildClipboardText` and pushes it to the Windows clipboard.

4. **Retention**
    - When capacity is exceeded, the oldest entries are evicted automatically. There’s no manual prune button to avoid accidental data loss;
      exporting is handled by copying or external telemetry integration.

## Best Practices

### For Users

1. **Pin the Log**: Keep the page open in a second monitor when running long automations to watch progress in real time.
2. **Use Search**: Combine level filtering with keyword search (e.g., package ID, registry key) to jump to relevant entries.
3. **Copy with Context**: Include detail lines when sharing logs with support—they often contain JSON payloads or stack traces.
4. **Leave Auto-Select On During Runs**: The newest entry will always highlight, making it easy to spot errors as they arrive.

### For Developers

1. **Emit Structured Details**: Pass collections or anonymous objects to logging APIs; they’ll be JSON-serialized for readability.
2. **Keep Messages Actionable**: Surface the user-facing summary in `message` and reserve `details` for diagnostics.
3. **Set Appropriate Levels**: Success vs Information vs Warning vs Error drives PulseGuard behaviour.
4. **Mind Capacity**: If you need longer retention, increase `ActivityLogService` capacity via DI configuration.

## Technical Notes

-   **Serialization**: Details use `System.Text.Json` with relaxed escaping to preserve paths and PowerShell snippets.
-   **Threading**: `LogXXX` methods are safe to call from any thread; the view model marshals UI updates onto the dispatcher when necessary.
-   **Debounce**: Search refresh is debounced at 110 ms to prevent re-filtering on every keystroke.
-   **Clipboard**: Clipboard write failures (e.g., locked by remote session) are ignored so the UI remains responsive.

## Future Enhancements

-   Export to Markdown/JSON from the UI.
-   Persistent log store with daily rotation.
-   Bookmarking and tagging entries for investigations.
-   Correlation IDs across automation runs.

