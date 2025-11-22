# CleanupViewModel Extraction Roadmap

`src/TidyWindow.App/ViewModels/CleanupViewModel.cs` currently hosts paging, filtering, selection, deletion, lock inspection, and celebration orchestration logic in a single class that exceeds 3,000 lines. The plan below breaks that type into focused collaborators and provides a trackable checklist so each extraction can be tackled (and marked complete) independently.

## Working Notes

-   Stabilize unit tests in `TidyWindow.App.Tests` that touch `CleanupViewModel` before each extraction.
-   Favor `partial` classes only as a temporary bridge; the target end-state is one public view model delegating to private services.
-   Keep public API changes additive until the final step to avoid breaking XAML bindings.

## Extraction Checklist

-   [x] **Extraction 1 – Preview Paging & Filtering**

    -   Move `FilteredItems`, `RefreshFilteredItems`, paging fields/properties, and `MatchesFilters` into `PreviewPagingController` (new class under `ViewModels/Preview/`).
    -   Expose a small interface `IPreviewFilter` for reusable filtering predicates (age, extensions, item kind).
    -   Update `CleanupViewModel` to delegate `RefreshFilteredItems` and page commands to the controller.
    -   _Done 2025-11-22:_ Added `PreviewPagingController`, `CleanupPreviewFilter`, and `IPreviewFilter`, rewired `CleanupViewModel` to delegate paging/filters, and marked checklist complete.

-   [ ] **Extraction 2 – Extension Filter Configuration**

    -   Relocate `_activeExtensions`, `RebuildExtensionCache`, `ParseExtensions`, and related properties to `CleanupExtensionFilterModel` under `ViewModels/Filters/`.
    -   Model should raise events when the extension set changes so the paging controller can re-filter without tightly coupling to `CleanupViewModel`.

-   [ ] **Extraction 3 – Selection Management**

    -   Extract `SelectAllCurrent`, `SelectAllPages`, `SelectPageRange`, `ClearCurrentSelection`, `ApplySelectionAcrossTargets`, `ApplySelectionToCurrentTarget`, and helpers into `CleanupSelectionCoordinator`.
    -   Coordinator exposes commands (or methods) that the view model wires into `[RelayCommand]` instances, keeping `CleanupViewModel` focused on orchestration.

-   [ ] **Extraction 4 – Lock Inspection Pipeline**

    -   Create `LockInspectionCoordinator` under `ViewModels/Locks/` containing `BeginLockInspection`, `CancelLockInspection`, `InspectLockingProcessesAsync`, `BuildLockInspectionSample`, and lock-related state.
    -   Provide an interface (`ILockInspectionCoordinator`) used by `CleanupViewModel` so the pipeline can be mocked in tests.

-   [ ] **Extraction 5 – Deletion Workflow & Confirmation Sheet**

    -   Move `PrepareDeletionConfirmation`, `ClearPendingDeletionState`, `ExecuteDeletionAsync`, `HandleEdgeHistoryAsync`, and risk/report helpers into `CleanupDeletionWorkflow` under `ViewModels/Deletion/`.
    -   Expose callbacks/events for: confirmation requested, deletion progress, celebration data.

-   [ ] **Extraction 6 – Celebration Presenter**

    -   Extract `ShowCleanupCelebrationAsync`, celebration properties, and share/report helpers into `CleanupCelebrationPresenter` (maybe shareable with other flows).
    -   Presenter produces a value object describing the celebration screen that the main view model consumes.

-   [ ] **Extraction 7 – Phase & Toast Transitions**

    -   Move `_phaseTransition*` fields, `TransitionToPhaseAsync`, `ShowRefreshToast`, `HideRefreshToast`, and toast dismissal logic into `CleanupPhaseTransitionController`.
    -   Keep the controller UI-agnostic by surfacing events (`PhaseTransitioning`, `ToastChanged`).

-   [ ] **Extraction 8 – Locking Process Commands**

    -   Relocate `CloseLockingProcessesAsync`, `CloseSelectedLockingProcessesAsync`, `ForceClose...`, and related command logic into `LockingProcessCommandHost`.
    -   This host depends on `ILockInspectionCoordinator` to re-scan after closures.

-   [ ] **Extraction 9 – Browser History Special-Case Handling**

    -   Separate Edge/Chrome history handling (currently inside `HandleEdgeHistoryAsync`) into service(s) under `Services/History/` to simplify the deletion workflow class.

-   [ ] **Extraction 10 – Sensitive Path & Elevation Utilities**
    -   Move `BuildSensitiveRoots`, `IsElevationLikelyRequired`, and helpers into `CleanupSecurityGuards` (static class under `Core/Cleanup/` or shared utilities) to decouple from the view model.

## Tracking Guidelines

-   Commit each extraction with the checkbox update in this document (`[x]`) so history shows progress.
-   Reference the extraction number in branch names and PR titles, e.g., `feature/cleanup-extraction-04-lock-inspection`.
-   Keep `CleanupViewModel` constructor wiring lightweight: it should accept the new collaborators through dependency injection.

## Definition of Done

-   `CleanupViewModel` shrinks to primarily property forwarding and high-level orchestration.
-   Each collaborator has focused tests (unit or component) covering its behavior.
-   `extraction.md` reflects the current completion status with checked boxes for finished steps.
