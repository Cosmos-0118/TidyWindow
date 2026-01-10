# Cleanup Issues and Improvements

**Current rating:** 7/10

**Potential after fixes:** 9/10

## Key issues and recommended improvements

-   [ ] **Protected root coverage is narrow** – The delete pipeline in [src/TidyWindow.Core/Cleanup/CleanupService.cs](src/TidyWindow.Core/Cleanup/CleanupService.cs) only guards specific Windows subfolders (System32, WinSxS, SystemApps, WindowsApps, etc.). It does not explicitly cover broader roots like Program Files, ProgramData, or user profile roots, so an unlucky selection could target them if surfaced in preview. _Recommendation:_ extend the protected list to include Program Files, ProgramData, and common profile roots; add tests to prevent regressions.
-   [ ] **Recycle Bin toggle can still permanently delete** – Manual runs enable `AllowPermanentDeleteFallback` even when Recycle Bin is selected, so a failed shell move leads to permanent delete without an extra prompt ([src/TidyWindow.App/ViewModels/CleanupViewModel.cs](src/TidyWindow.App/ViewModels/CleanupViewModel.cs)). _Recommendation:_ make the fallback explicit (UI toggle or warning) or default to "skip if recycle fails" to align with user expectation of reversibility.
-   [ ] **Hidden/system/recent skips are off by default** – `CleanupDeletionOptions` defaults leave `SkipHiddenItems`, `SkipSystemItems`, and `SkipRecentItems` false ([src/TidyWindow.Core/Cleanup/CleanupDeletionOptions.cs](src/TidyWindow.Core/Cleanup/CleanupDeletionOptions.cs)). Age filters and risk cues help, but nothing prevents deleting fresh or hidden/system files once selected. _Recommendation:_ add opt-in checkboxes to the confirmation sheet (and persist them) or bias defaults to skip hidden/system and recent items unless explicitly overridden.
-   [ ] **Browser coverage is Edge-only** – History cleanup is limited to Microsoft Edge via WebView2 APIs ([src/TidyWindow.App/Services/BrowserCleanupService.cs](src/TidyWindow.App/Services/BrowserCleanupService.cs)). _Recommendation:_ consider optional adapters for Chrome/Firefox profiles if in scope.

