# Cleanup Issues and Improvements

**Current rating:** 7/10

**Potential after fixes:** 9/10

## Key issues and recommended improvements

-   [ ] **Protected root coverage is narrow** – The delete pipeline in [src/TidyWindow.Core/Cleanup/CleanupService.cs](src/TidyWindow.Core/Cleanup/CleanupService.cs) only guards specific Windows subfolders (System32, WinSxS, SystemApps, WindowsApps, etc.). It does not explicitly cover broader roots like Program Files, ProgramData, or user profile roots, so an unlucky selection could target them if surfaced in preview. _Recommendation:_ extend the protected list to include Program Files, ProgramData, and common profile roots; add tests to prevent regressions.
-   [ ] **Recycle Bin toggle can still permanently delete** – Manual runs enable `AllowPermanentDeleteFallback` even when Recycle Bin is selected, so a failed shell move leads to permanent delete without an extra prompt ([src/TidyWindow.App/ViewModels/CleanupViewModel.cs](src/TidyWindow.App/ViewModels/CleanupViewModel.cs)). _Recommendation:_ make the fallback explicit (UI toggle or warning) or default to "skip if recycle fails" to align with user expectation of reversibility.
-   [ ] **Hidden/system/recent skips are off by default** – `CleanupDeletionOptions` defaults leave `SkipHiddenItems`, `SkipSystemItems`, and `SkipRecentItems` false ([src/TidyWindow.Core/Cleanup/CleanupDeletionOptions.cs](src/TidyWindow.Core/Cleanup/CleanupDeletionOptions.cs)). Age filters and risk cues help, but nothing prevents deleting fresh or hidden/system files once selected. _Recommendation:_ add opt-in checkboxes to the confirmation sheet (and persist them) or bias defaults to skip hidden/system and recent items unless explicitly overridden.
-   [ ] **Automation is size-only and low-context** – The scheduler sorts top-N purely by size with no age/risk filter and cannot emit reports ([src/TidyWindow.App/Services/CleanupAutomationScheduler.cs](src/TidyWindow.App/Services/CleanupAutomationScheduler.cs)). It may delete newer cache files while skipping long-lived clutter, and operators cannot audit what was removed. _Recommendation:_ allow age/risk filters for automation, optional report generation, and a "preview-only" dry run mode for verification.
-   [ ] **Lock inspection coverage can miss blockers** – Sampling caps at 32 items per category and 600 overall; items outside the sample are not inspected for locks ([src/TidyWindow.App/ViewModels/CleanupViewModel.cs](src/TidyWindow.App/ViewModels/CleanupViewModel.cs)). _Recommendation:_ surface the sampling coverage more prominently and allow a full-scan option when selections are small.
-   [ ] **Browser coverage is Edge-only** – History cleanup is limited to Microsoft Edge via WebView2 APIs ([src/TidyWindow.App/Services/BrowserCleanupService.cs](src/TidyWindow.App/Services/BrowserCleanupService.cs)). _Recommendation:_ clarify this in the UI and consider optional adapters for Chrome/Firefox profiles if in scope.


## To reach 10/10

-   [ ] Expand protected-path coverage with tests (Program Files/ProgramData/user profiles) and hard stops in the UI when targets are under those roots.
-   [ ] Make recycle-bin-first the default with an explicit permanent-delete toggle and "skip if recycle fails" option; surface irreversible actions with stronger confirmation UX.
-   [ ] Enable persistent safety defaults: skip hidden/system/recent items unless explicitly overridden, with per-run and persisted preferences.
-   [ ] Add automation reports (JSON/Markdown), dry-run mode, and age/risk-aware selection so unattended runs are auditable and conservative by default.
-   [ ] Offer full lock inspection (beyond sampling) and allow scoped full scans when selections are small.
-   [ ] Broaden browser coverage (Chrome/Firefox profiles) with capability detection and clear fallbacks.

