# PackageMaintenanceViewModel Extraction Roadmap

`src/TidyWindow.App/ViewModels/PackageMaintenanceViewModel.cs` mixes data acquisition, filtering, suppression tracking, command orchestration, and operation processing in a single class that spans nearly 2,000 lines. This roadmap mirrors the cleanup model plan so we can extract cohesive collaborators step by step and keep progress visible.

## Working Notes

-   Maintain feature parity after every extraction; keep bindings/API stable until the final cleanup pass.
-   Favor constructor injection for new collaborators so the view model becomes a coordinator.
-   Keep long-lived background work (operation processor) cancellable and testable.

## Extraction Checklist

-   [ ] **Extraction 1 – Inventory Snapshot & Filtering**

    -   Move `_allPackages`, `_packagesByKey`, `ApplySnapshot`, `ApplyFilters`, `EnsureManagerFilters`, `SynchronizeCollection`, and related helper methods into `PackageInventoryController` under `ViewModels/Packages/`.
    -   Controller exposes observable collections for filtered packages plus summary stats.

-   [ ] **Extraction 2 – Warning & Suppression Management**

    -   Extract `Warnings`, `ResolveSuppression`, `ApplySuppressionState`, `RegisterNonActionableSuppression`, `TryClearSuppression`, `BuildSuppressionDetails`, `AddSuppressionWarning`, `RemoveSuppressionWarnings`, and suppression DTO helpers into `PackageSuppressionService`.
    -   Service owns warning strings and raises events when the warning list changes.

-   [ ] **Extraction 3 – Operation Queue & Processor**

    -   Move `_pendingOperations`, `_operationLock`, `_isProcessingOperations`, `EnqueueMaintenanceOperation`, `ProcessOperationsAsync`, `ProcessOperationAsync`, wait handling, and installer-busy helpers to `PackageMaintenanceOperationCoordinator`.
    -   Expose enqueue APIs plus status callbacks so the view model only reflects UI state.

-   [ ] **Extraction 4 – Installer Busy & Elevation Guards**

    -   Separate `EnsureElevation`, `ManagerRequiresElevation`, installer-busy detection utilities, delay calculations, and wait messaging into `MaintenanceOperationGuards` (shared service).
    -   Coordinator consumes this service; view model only observes state changes.

-   [ ] **Extraction 5 – Commands & Selection Utilities**

    -   Move command bodies for `QueueSelectedUpdates`, `SelectAllPackages`, `RetryFailed`, `ClearCompleted`, `ToggleWarnings`, etc., into `PackageMaintenanceCommandHost` which operates on injected controllers/services.
    -   Keeps `[RelayCommand]` methods thin wrappers delegating to testable logic.

-   [ ] **Extraction 6 – Inventory Refresh Workflow**

    -   Extract `RefreshAsync`, snapshot logging, `_lastRefreshedAt`, and `BuildInventoryDetails` to `PackageInventoryRefreshWorkflow` that orchestrates services and updates the inventory controller.

-   [ ] **Extraction 7 – ViewModel Attachment Lifecycle**

    -   Move `_attachedItems`, `_attachedOperations`, attach/detach logic, and collection change handlers into `PackageMaintenanceBindingManager` so the view model only subscribes through a single helper.

-   [ ] **Extraction 8 – Operation Detail Builders**

    -   Relocate `BuildOperationDetails`, `BuildResultDetails`, non-actionable message parsing, and winget-specific helpers into `MaintenanceOperationDiagnostics` for reuse by telemetry or logging.

-   [ ] **Extraction 9 – Item View Model Enhancements**

    -   Consider splitting `PackageMaintenanceItemViewModel` into a base model plus decorators (suppression, queue state) so the package view model file shrinks further.

-   [ ] **Extraction 10 – Disposable Pattern & Cleanup**
    -   After earlier steps, move `Dispose`, `_isDisposed`, and cleanup logic into a lightweight host (possibly `PackageMaintenanceScope`) to ensure all services and subscriptions dispose predictably.

## Tracking Guidelines

-   Update this file with `[x]` when an extraction lands; reference the extraction number in PR titles (e.g., `feature/package-extraction-03-ops`).
-   Keep each extraction focused; avoid mixing structural changes with bug fixes unless necessary.
-   When public APIs change, document the impact in `docs/architecture.md`.

## Definition of Done

-   `PackageMaintenanceViewModel` focuses on wiring properties/commands to collaborators.
-   Operation processing, inventory filtering, and suppression tracking have isolated unit tests.
-   Both extraction roadmaps reflect the final state with all checkboxes checked and notes about deviations, if any.
