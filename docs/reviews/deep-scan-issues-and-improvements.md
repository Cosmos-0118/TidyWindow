# Deep Scan Issues and Improvements

**Current rating:** 6/10

**Potential after fixes:** 8/10

## Key issues and recommendations

-   [ ] **Hidden toggle also exposes system files** – Hidden/system filtering is tied together: turning on "include hidden" allows system files because the skip-system check is gated by the same flag ([src/TidyWindow.Core/Diagnostics/DeepScanService.cs](src/TidyWindow.Core/Diagnostics/DeepScanService.cs#L467-L503)). _Recommendation:_ add a separate "include system files" toggle and keep system skipping on by default.
-   [ ] **Aggressive force-delete behavior** – Force delete runs with retries, ownership repair, skip-locked disabled, and delete-on-reboot fallback ([src/TidyWindow.App/ViewModels/DeepScanViewModel.cs](src/TidyWindow.App/ViewModels/DeepScanViewModel.cs#L144-L232)). This can delete in-use items without previewing impact. _Recommendation:_ make force delete an explicit, high-friction choice with stronger warnings. make the popup ui nicer and clearer.
-   [ ] **No scan cancellation in the UI** – The service accepts cancellation tokens, but the view model does not expose a cancel command; long scans cannot be stopped once started ([src/TidyWindow.App/ViewModels/DeepScanViewModel.cs](src/TidyWindow.App/ViewModels/DeepScanViewModel.cs#L61-L141)). _Recommendation:_ add a cancel button wired to a `CancellationTokenSource` passed into `RunScanAsync`.
