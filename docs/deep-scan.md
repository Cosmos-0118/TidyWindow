# Deep Scan Page

## What the Deep Scan page does

Deep Scan is a top-N analyzer for large files and folders under a chosen root. It streams progress as it walks the file system, classifies items (Games, Videos, Documents, etc.), and lets users open locations or delete items directly (with a force-delete option for stubborn files).

## Core flow

-   **Configure**: Choose a root (defaults to `C:\` when present, otherwise the user profile). Presets include User profile, Downloads, Desktop, Documents, Pictures, Videos, Local AppData, and Edge profiles. Set minimum size (MB, default 0), max items (default 1,000), name filters (split on `; , |`), match mode (Contains/StartsWith/EndsWith/Exact), case sensitivity, include hidden toggle, and include directories toggle. Page size is fixed at 100 results.
-   **Run scan**: `DeepScanService.RunScanAsync` resolves the root, enumerates files/directories with `FileSystemEnumerable`, skips reparse points and system items, and skips hidden items unless the include-hidden toggle is on. Size filtering happens in bytes (minimum MB converted to bytes). A priority queue keeps only the top-N largest candidates.
-   **Progress**: Streaming updates throttle around 600 ms (candidates ~220 ms) and include current top findings, processed count/bytes, current path, and category totals. Errors like access denied, path-too-long, or missing files are treated as non-fatal and skipped.
-   **Results**: Findings are sorted by size, paged 100 per page, and summarized (count, total size, top category totals). Selecting an item allows opening its folder or deleting it.
-   **Delete / Force delete**: Standard delete clears read-only flags then permanently deletes (no Recycle Bin, no lock handling). Force delete uses the cleanup pipeline with ownership repair, retries, skip-locked disabled, and delete-on-reboot fallback. Missing items are removed from the list with a status message.

## Defaults and limits

-   Max items: default 1,000 (must be ≥1).
-   Min size: default 0 MB (no lower bound other than zero).
-   Include hidden/system: off by default; enabling hidden also allows system items through the filter.
-   Include directories: off by default.
-   Paging: 100 items/page.
-   Parallelism: up to 8 threads (capped by CPU count) for child directory processing.

## Safety and behavior notes

-   Scanning is read-only; non-critical I/O errors are skipped, and progress continues.
-   Hidden and system files are skipped unless the include-hidden toggle is on; reparse points are always skipped.
-   Category classification is Windows-oriented and based on path markers plus extensions (e.g., Steam paths → Games, AppData → App Data, media extensions → Videos/Pictures/Music).
-   Delete operations are permanent. The standard path does not use the Recycle Bin. Force delete can take ownership and schedule delete-on-reboot for locked items.

## Implementation map

-   View and commands: [src/TidyWindow.App/ViewModels/DeepScanViewModel.cs](src/TidyWindow.App/ViewModels/DeepScanViewModel.cs)
-   Scan engine, classification, progress: [src/TidyWindow.Core/Diagnostics/DeepScanService.cs](src/TidyWindow.Core/Diagnostics/DeepScanService.cs)
-   Force-delete path (via cleanup engine): [src/TidyWindow.Core/Cleanup/CleanupService.cs](src/TidyWindow.Core/Cleanup/CleanupService.cs)

## Known gaps to watch

-   Deletions are always permanent (no Recycle Bin) and standard delete has no lock handling; force delete can remove in-use items via ownership + delete-on-reboot.
-   The include-hidden toggle also admits system files; there is no separate "skip system" control.
-   Classification is Windows-centric; non-Windows paths may be misclassified.
-   No built-in export/reporting of findings; only on-screen paging is available.

