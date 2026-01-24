# Maintenance backend simplification plan

## Goals

- Make maintenance execution reliable and cancel-safe (no stuck operations, clear outcomes).
- Break the current monolith view model into small, testable units.
- Reduce redundant logic and centralize shared rules (elevation, suppression, busy-installer handling).
- Keep UI behavior unchanged while back-end code becomes easier to reason about.

## Current pain points (quick scan)

- `src/TidyWindow.App/ViewModels/PackageMaintenanceViewModel.cs` (~3.5k lines) mixes queueing, filtering, paging, version lookup, suppression rules, downgrade fallback, elevation prompts, and UI wiring. Hard to test and reason about.
- `src/TidyWindow.App/Services/MaintenanceAutoUpdateScheduler.cs` and automation view model each replicate bits of suppression/selection logic instead of relying on a shared policy.
- Logging/status and cancellation handling are scattered; failures/installer-busy paths are interleaved with UI updates.

## Target shape (proposed structure)

- `src/TidyWindow.App/Maintenance/`
    - `MaintenanceOperationQueue.cs` — owns enqueue/dequeue, cancellation, installer-busy backoff, downgrade fallback, and work-tracker integration; UI subscribes to events.
    - `MaintenanceInventoryController.cs` — loads inventory, applies filters/paging, exposes observable collections for the view; no direct UI threading logic.
    - `MaintenanceVersionProvider.cs` — wraps `PackageVersionDiscoveryService` with caching/refresh and drives version picker options.
    - `MaintenancePolicy.cs` — centralizes elevation requirement, suppression rules, downgrade allowance, and package-id resolution (used by queue + automation scheduler).
    - `MaintenanceLogger.cs` (thin) — consistent activity log/status messages.
- `PackageMaintenanceViewModel` becomes a coordinator composed from the above services, slim (<400 lines) with UI-only concerns.
- `MaintenanceAutomationViewModel` consumes `MaintenancePolicy` for selection/eligibility; scheduler remains, but uses the queue service instead of calling maintenance service directly.

## Step-by-step checklist

- [ ] Step 1: Lock current behavior with high-value tests (queueing, retry/backoff, downgrade fallback, suppression) around `PackageMaintenanceViewModel` and `MaintenanceAutoUpdateScheduler`.
- [ ] Step 2: Extract a pure `MaintenancePolicy` (manager/identifier resolution, suppression decisions, elevation requirement, downgrade allowance) and update both maintenance and automation flows to consume it.
- [ ] Step 3: Carve out `MaintenanceOperationQueue` from the view model (enqueue, processing loop, installer-busy detection/backoff, downgrade fallback, cancellation) returning immutable operation snapshots/events.
- [ ] Step 4: Move filtering/paging/search into `MaintenanceInventoryController` that exposes a read-only view; `PackageMaintenanceViewModel` only binds to it.
- [ ] Step 5: Isolate version discovery into `MaintenanceVersionProvider` with caching + refresh flags; remove version-picker logic from the main view model.
- [ ] Step 6: Audit the maintenance PowerShell scripts (e.g., `automation/scripts/update-catalog-package.ps1`, `automation/scripts/remove-catalog-package.ps1`) for consistent parameters, JSON payload shape, logging, exit codes, and failure signaling; centralize shared helpers to cut duplication.
- [ ] Step 7: Introduce `MaintenanceLogger` (or reuse ActivityLogService wrappers) so status/log messages are built in one place and consumed by UI + scheduler.
- [ ] Step 8: Refactor `MaintenanceAutomationViewModel` to read/write via the new policy + queue, and drop duplicated selection/eligibility code.
- [ ] Step 9: Simplify `PackageMaintenanceViewModel` to orchestration only (wire commands to queue/controller/provider, handle navigation/page events, no business rules inside).
- [ ] Step 10: Add targeted unit tests for the new services (queue, policy, version provider, scripts’ JSON contract/exit codes) and integration tests for automation run plans.
- [ ] Step 11: Delete or inline dead code revealed during extraction; ensure public surface docs/README mention the new maintenance layout.

## Notes

- Keep existing public behaviors and UI strings stable while refactoring.
- Prefer immutable DTOs for operation states so UI updates are easy to diff/test.
- Wire UI thread marshaling at the edge (view model) instead of inside services.
