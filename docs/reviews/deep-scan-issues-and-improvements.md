# Deep Scan Issues and Improvements

**Current rating:** 6/10

**Potential after fixes:** 8/10

## Key issues and recommendations

-   [ ] **Permanent deletes with no recycle-bin path** – Standard delete permanently removes items after clearing read-only flags; force delete uses the cleanup engine with `SkipLockedItems=false`, ownership repair, and delete-on-reboot ([src/TidyWindow.App/ViewModels/DeepScanViewModel.cs](src/TidyWindow.App/ViewModels/DeepScanViewModel.cs#L144-L232) and [src/TidyWindow.Core/Cleanup/CleanupService.cs](src/TidyWindow.Core/Cleanup/CleanupService.cs)). There is no Recycle Bin option and no lock-aware skip in the normal path. _Recommendation:_ add a recycle-bin-first option and make force delete an explicit, high-friction choice with stronger warnings.
-   [ ] **Hidden toggle also exposes system files** – Hidden/system filtering is tied together: turning on "include hidden" allows system files because the skip-system check is gated by the same flag ([src/TidyWindow.Core/Diagnostics/DeepScanService.cs](src/TidyWindow.Core/Diagnostics/DeepScanService.cs#L467-L503)). _Recommendation:_ add a separate "include system files" toggle and keep system skipping on by default.
-   [ ] **Aggressive force-delete behavior** – Force delete runs with retries, ownership repair, skip-locked disabled, and delete-on-reboot fallback ([src/TidyWindow.App/ViewModels/DeepScanViewModel.cs](src/TidyWindow.App/ViewModels/DeepScanViewModel.cs#L144-L232)). This can delete in-use items without previewing impact. _Recommendation:_ surface lock detection or at least warn when a force delete is requested; consider reusing the lock inspection flow from Cleanup.
-   [ ] **No scan cancellation in the UI** – The service accepts cancellation tokens, but the view model does not expose a cancel command; long scans cannot be stopped once started ([src/TidyWindow.App/ViewModels/DeepScanViewModel.cs](src/TidyWindow.App/ViewModels/DeepScanViewModel.cs#L61-L141)). _Recommendation:_ add a cancel button wired to a `CancellationTokenSource` passed into `RunScanAsync`.
-   [ ] **Classification is Windows-specific** – Category labeling relies on Windows paths/markers ([src/TidyWindow.Core/Diagnostics/DeepScanService.cs](src/TidyWindow.Core/Diagnostics/DeepScanService.cs#L1204-L1337)). For a Windows-only target this is fine; keep tests to avoid regressions and extend only if we ever broaden platform scope.
-   [ ] **No export/audit of findings** – Results are only paged in-memory (100 items/page) with no CSV/JSON export. _Recommendation:_ add export to CSV/JSON and allow saving a scan snapshot for audits.

## To reach 10/10

-   [ ] Add lock inspection parity with Cleanup (sample or full-scan with close/force-close options, plus skip-locked toggle for standard delete).
-   [ ] Provide reversible delete paths (Recycle Bin by default, explicit permanent-delete opt-in, and a “skip if recycle fails” option).
-   [ ] Harden classification with Windows-specific tests and clearer category rules; extend only if platform scope broadens later.
-   [ ] Add rich reporting/export (CSV/JSON), scan history, and shareable snapshots; optionally trend analysis over time.
-   [ ] Introduce scheduled scans with dry-run/report-only mode and guardrails for long-running jobs.
-   [ ] Offer duplicate detection and grouping to target redundant large files safely.

