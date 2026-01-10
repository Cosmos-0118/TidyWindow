# Deep Scan Issues and Improvements

**Current rating:** 6/10

**Potential after fixes:** 8/10

## Key issues and recommendations

-   [x] **Hidden toggle also exposes system files** – Added a separate "include system files" flag and defaulted to skipping system entries unless explicitly enabled ([src/TidyWindow.Core/Diagnostics/DeepScanService.cs](src/TidyWindow.Core/Diagnostics/DeepScanService.cs#L195-L238), [src/TidyWindow.Core/Diagnostics/DeepScanService.cs](src/TidyWindow.Core/Diagnostics/DeepScanService.cs#L538-L566), [src/TidyWindow.App/ViewModels/DeepScanViewModel.cs](src/TidyWindow.App/ViewModels/DeepScanViewModel.cs#L24-L214)).
-   [x] **Aggressive force-delete behavior** – Force delete now requires an explicit "Enable force delete" arm switch, the button stays disabled until armed, and the flow auto-disarms after any attempt with clearer warning copy ([src/TidyWindow.App/ViewModels/DeepScanViewModel.cs](src/TidyWindow.App/ViewModels/DeepScanViewModel.cs#L24-L128), [src/TidyWindow.App/Views/DeepScanPage.xaml](src/TidyWindow.App/Views/DeepScanPage.xaml#L532-L551), [src/TidyWindow.App/Views/DeepScanPage.xaml](src/TidyWindow.App/Views/DeepScanPage.xaml#L951-L986), [src/TidyWindow.App/Views/DeepScanPage.xaml.cs](src/TidyWindow.App/Views/DeepScanPage.xaml.cs#L63-L96)).
-   [x] **No scan cancellation in the UI** – Added a cancel command with a live `CancellationTokenSource`, wired the scan to honor it, and surfaced a Cancel button shown only while a scan is in flight ([src/TidyWindow.App/ViewModels/DeepScanViewModel.cs](src/TidyWindow.App/ViewModels/DeepScanViewModel.cs#L25-L288), [src/TidyWindow.App/Views/DeepScanPage.xaml](src/TidyWindow.App/Views/DeepScanPage.xaml#L382-L468)).

