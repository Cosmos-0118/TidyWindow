# Install Hub Page Documentation

## Overview

The Install Hub is a comprehensive package management interface that enables users to browse curated package bundles, explore the full install catalog, queue installations, and monitor installation progress. It serves as the central hub for package discovery, selection, and installation orchestration in TidyWindow.

## Purpose

The Install Hub provides a unified experience for:

-   **Bundle Management**: Browse and queue curated collections of related packages
-   **Catalog Exploration**: Search and filter through the complete package catalog
-   **Installation Queue**: Monitor, manage, and retry package installations
-   **Preset Management**: Export and import custom package selections as presets

## Architecture

### Component Structure

The Install Hub uses a pivot-based navigation system with three main views, all managed by a single ViewModel:

```
┌─────────────────────────────────────────────────────────────┐
│                    InstallHubPage.xaml                      │
│                    (View - Container)                       │
└───────────────────────┬─────────────────────────────────────┘
                        │
                        ▼
┌─────────────────────────────────────────────────────────────┐
│                 InstallHubViewModel.cs                      │
│              (ViewModel - Business Logic)                   │
└───────┬───────────────────────────────┬─────────────────────┘
        │                               │
        ▼                               ▼
┌───────────────────┐         ┌──────────────────────────┐
│ InstallCatalog    │         │ InstallQueue             │
│ Service           │         │ (Queue Processing)       │
│ (Catalog Loading) │         │                          │
└─────────┬─────────┘         └──────────-┬──────────────┘
          │                               │
          ▼                               ▼
┌─────────────────────────────────────────────────────────┐
│         Three Pivot Views                               │
│  • InstallHubBundlesView                                │
│  • InstallHubCatalogView                                │
│  • InstallHubQueueView                                  │
└─────────────────────────────────────────────────────────┘
```

### Key Classes

#### `InstallHubViewModel`

-   **Location**: `src/TidyWindow.App/ViewModels/InstallHubViewModel.cs`
-   **Responsibilities**:
    -   Manages pivot navigation (Bundles, Catalog, Queue)
    -   Coordinates catalog loading and caching
    -   Handles package/bundle queuing operations
    -   Manages search and filter state
    -   Tracks installation queue operations
    -   Handles preset import/export

#### `InstallCatalogService`

-   **Location**: `src/TidyWindow.Core/Install/InstallCatalogService.cs`
-   **Responsibilities**:
    -   Loads YAML catalog files from `data/catalog/`
    -   Parses bundle and package definitions
    -   Provides lookup methods for packages and bundles
    -   Resolves package IDs to definitions

#### `InstallQueue`

-   **Location**: `src/TidyWindow.Core/Install/InstallQueue.cs`
-   **Responsibilities**:
    -   Manages installation operation queue
    -   Processes installations asynchronously
    -   Tracks operation status and progress
    -   Handles retry logic for failed operations
    -   Raises events for operation state changes

#### `BundlePresetService`

-   **Location**: `src/TidyWindow.Core/Install/BundlePresetService.cs`
-   **Responsibilities**:
    -   Exports selected packages to YAML preset files
    -   Imports preset files and resolves packages
    -   Validates preset structure

## User Interface

### Pivot Navigation

The Install Hub uses three pivot views accessible via toggle buttons:

1. **Bundles**: Browse curated package collections
2. **Catalog**: Explore and search the full package catalog
3. **Queue**: Monitor installation progress and history

### Bundles View (`InstallHubBundlesView`)

**Layout**:

-   **Bundle Library Card**: Displays all available bundles in a tile layout
-   Each bundle card shows:
    -   Bundle name and description
    -   Package count badge
    -   "Queue bundle" button (hidden for "All packages" synthetic bundle)
    -   "Browse catalog" button

**Features**:

-   Synthetic "All packages" bundle for browsing entire catalog
-   Bundle selection filters the catalog view
-   Direct bundle queuing

### Catalog View (`InstallHubCatalogView`)

**Layout**:

-   **Hero Section**: Statistics cards showing:
    -   Queued installs count
    -   Active runs count
    -   Visible packages count
-   **Filters Section**:
    -   Search text box (debounced, 120ms delay)
    -   Bundle filter dropdown
    -   Active filter badges
    -   "Reset filters" button
-   **Catalog Results**: Virtualized list of package cards

**Package Card Features**:

-   Checkbox for multi-selection
-   Package name and ID
-   Manager badge (WINGET, CHOCO, SCOOP)
-   Admin requirement badge (if applicable)
-   Queued status badge
-   Summary text
-   Tags display
-   Last operation status
-   "Queue" button (disabled if already queued)

**Features**:

-   Real-time search filtering (name, ID, summary, tags)
-   Bundle-based filtering
-   Multi-select with "Queue selected" action
-   Export selection as preset
-   Import preset functionality

### Queue View (`InstallHubQueueView`)

**Layout**:

-   **Hero Section**: Statistics badges:
    -   Total entries count
    -   Running operations count
    -   Queued operations count
    -   Completed operations count
    -   Failed operations count
-   **Queue Timeline**: Virtualized list of operations
-   **Action Buttons**: "Retry failed" and "Clear completed"

**Operation Card Features**:

-   Package name
-   Status label (color-coded: Queued, Installing, Installed, Failed, Cancelled)
-   Operation message
-   Attempt count (if > 1)
-   Completion timestamp
-   "View details" button
-   "Cancel" button (only for active operations)

**Operation Details Overlay**:

-   Modal dialog showing:
    -   Package name and status
    -   Full output transcript
    -   Error transcript
    -   Attempt history

## Workflow

### Catalog Loading

1. **Initialization**

    - `InstallHubPage` loads and calls `EnsureViewModelInitializedAsync()`
    - `InstallHubViewModel.EnsureLoadedAsync()` is invoked
    - Overlay loader is engaged

2. **Catalog Loading**

    - `LoadCatalogAsync()` runs on background thread
    - `InstallCatalogService` loads YAML files from `data/catalog/`
    - Files are parsed using YamlDotNet
    - Packages and bundles are extracted and validated

3. **UI Update**
    - Catalog data is cached in ViewModel
    - `ApplyCatalog()` updates UI collections on UI thread
    - Bundles collection is populated
    - Packages collection is initialized (filtered by selected bundle)
    - Overlay loader is dismissed (minimum 2 seconds display)

### Bundle Queuing

1. **User Action**

    - User clicks "Queue bundle" on a bundle card
    - `QueueBundleCommand` is invoked

2. **Package Resolution**

    - Bundle's package IDs are resolved to `InstallPackageDefinition` objects
    - Uses cached bundle-to-packages mapping

3. **Queue Operation**

    - `InstallQueue.EnqueueRange()` is called
    - Duplicate active operations are skipped
    - New operations are created and added to queue
    - Operations are written to processing channel

4. **UI Update**
    - Package queue states are updated
    - Queued packages show "Queued" badge
    - Status message is displayed

### Package Queuing

1. **Single Package**

    - User clicks "Queue" button on package card
    - `QueuePackageCommand` is invoked
    - `InstallQueue.Enqueue()` creates operation

2. **Multiple Packages**
    - User selects packages via checkboxes
    - Clicks "Queue selected" button
    - `QueueSelectionCommand` processes all selected packages
    - Selections are cleared after queuing

### Installation Processing

1. **Queue Processing**

    - Background task (`ProcessQueueAsync`) reads from channel
    - Operations are processed sequentially
    - Each operation executes PowerShell install script

2. **Status Updates**

    - `OperationChanged` event is raised for each state change
    - ViewModel's `OnInstallQueueChanged` handler updates UI
    - Operation view models are created/updated
    - Package queue states are refreshed

3. **Operation States**:
    - **Pending**: Queued, awaiting processing
    - **Running**: Currently installing
    - **Succeeded**: Installation completed successfully
    - **Failed**: Installation failed (can be retried)
    - **Cancelled**: User cancelled operation

### Search and Filtering

1. **Search Text**

    - User types in search box
    - Debounced (110ms delay) to avoid excessive filtering
    - `OnSearchTextChanged` triggers `ApplyBundleFilter()`
    - Packages are filtered by name, ID, summary, or tags

2. **Bundle Filter**

    - User selects bundle from dropdown
    - `OnSelectedBundleChanged` triggers filtering
    - Only packages in selected bundle are shown
    - "All packages" bundle shows everything

3. **Filter Application**
    - `ApplyBundleFilter()` combines bundle and search filters
    - Results are sorted alphabetically by name
    - Collection is synchronized (minimal changes)

### Preset Management

#### Export Preset

1. **User Action**

    - User selects packages via checkboxes
    - Clicks "Export selection" (if available)
    - `ExportSelectionAsync` is invoked

2. **File Dialog**

    - Save file dialog opens
    - Default filename: `tidywindow-preset.yml`
    - User selects save location

3. **Preset Creation**
    - `BundlePreset` is created with selected package IDs
    - Preset is serialized to YAML
    - File is saved asynchronously

#### Import Preset

1. **User Action**

    - User clicks "Import preset" (if available)
    - `ImportPresetAsync` is invoked

2. **File Dialog**

    - Open file dialog opens
    - User selects preset file

3. **Preset Loading**

    - Preset is deserialized from YAML
    - Package IDs are resolved via `BundlePresetService.ResolvePackages()`
    - Missing packages are identified

4. **UI Update**
    - Filters are reset
    - Resolved packages are selected in catalog
    - Status message shows import result (with missing packages if any)

### Queue Management

#### Retry Failed Operations

1. **User Action**

    - User clicks "Retry failed" button
    - `RetryFailedCommand` is invoked

2. **Retry Processing**

    - `InstallQueue.RetryFailed()` finds all failed operations
    - Operations are reset (status → Pending, attempt count incremented)
    - Operations are re-queued to processing channel

3. **UI Update**
    - Operation statuses are updated
    - Status message shows retry count

#### Clear Completed Operations

1. **User Action**

    - User clicks "Clear completed" button
    - `ClearCompletedCommand` is invoked

2. **Cleanup**
    - `InstallQueue.ClearCompleted()` removes all non-active operations
    - Operations are removed from UI collection
    - Snapshots and view models are cleaned up

#### Cancel Operation

1. **User Action**

    - User clicks "Cancel" button on active operation
    - `CancelOperationCommand` is invoked

2. **Cancellation**
    - `InstallQueue.Cancel()` requests cancellation
    - Operation status changes to Cancelled
    - Processing stops (if not yet started)

## Data Models

### InstallPackageDefinition

```csharp
public sealed record InstallPackageDefinition(
    string Id,                    // Unique package identifier
    string Name,                  // Display name
    string Manager,               // Package manager (winget, choco, scoop)
    string Command,               // Install command
    bool RequiresAdmin,           // Admin privileges required
    string Summary,               // Package description
    string? Homepage,             // Optional homepage URL
    ImmutableArray<string> Tags,  // Searchable tags
    ImmutableArray<string> Buckets) // Manager-specific buckets
```

### InstallBundleDefinition

```csharp
public sealed record InstallBundleDefinition(
    string Id,                           // Unique bundle identifier
    string Name,                         // Display name
    string Description,                  // Bundle description
    ImmutableArray<string> PackageIds)  // Package IDs in bundle
```

### InstallQueueOperationSnapshot

Represents the state of an installation operation at a point in time:

-   **Id**: Unique operation identifier (Guid)
-   **Package**: Package definition being installed
-   **Status**: Current status (Pending, Running, Succeeded, Failed, Cancelled)
-   **AttemptCount**: Number of installation attempts
-   **StartedAt**: When operation started
-   **CompletedAt**: When operation completed (if applicable)
-   **LastMessage**: Latest status message
-   **Output**: Installation output lines
-   **Errors**: Installation error lines
-   **IsActive**: Whether operation is currently active
-   **CanRetry**: Whether operation can be retried

## Catalog File Format

### Package Definition (YAML)

```yaml
packages:
    - id: package-identifier
      name: Package Display Name
      manager: winget
      command: install --id Package.Publisher.PackageName -e
      requiresAdmin: false
      summary: Brief description of the package
      homepage: https://example.com
      tags:
          - development
          - tools
      buckets:
          - main
```

### Bundle Definition (YAML)

```yaml
bundles:
    - id: bundle-identifier
      name: Bundle Display Name
      description: Description of what this bundle contains
      packages:
          - package-id-1
          - package-id-2
          - package-id-3
```

### Preset File Format (YAML)

```yaml
name: Custom Preset Name
description: Description of the preset
packages:
    - package-id-1
    - package-id-2
    - package-id-3
```

## State Management

### ViewModel Properties

-   `CurrentPivot`: Active pivot view (Bundles, Catalog, Queue)
-   `IsLoading`: Catalog loading state
-   `Bundles`: Observable collection of bundle view models
-   `Packages`: Observable collection of package view models (filtered)
-   `Operations`: Observable collection of operation view models
-   `SelectedBundle`: Currently selected bundle filter
-   `SearchText`: Current search filter text
-   `SelectedOperation`: Currently selected operation for details
-   `IsQueueOperationDetailsVisible`: Details overlay visibility
-   `QueuedOperationCount`: Count of queued operations
-   `RunningOperationCount`: Count of running operations
-   `CompletedOperationCount`: Count of completed operations
-   `FailedOperationCount`: Count of failed operations
-   `HasActiveOperations`: Whether any operations are active

### Package View Model State

-   `IsSelected`: Checkbox selection state
-   `IsQueued`: Whether package has active queue operation
-   `LastStatus`: Last operation status message

### Operation View Model State

-   `Status`: Current operation status
-   `StatusLabel`: Human-readable status label
-   `Message`: Operation message
-   `Attempts`: Attempt count display string
-   `CompletedAt`: Completion timestamp
-   `IsActive`: Whether operation is currently active
-   `CanRetry`: Whether operation can be retried
-   `OutputLines`: Installation output lines
-   `ErrorLines`: Installation error lines
-   `HasOutput`: Whether output exists
-   `HasErrors`: Whether errors exist
-   `HasTranscript`: Whether any transcript data exists

## Integration Points

### Activity Log Service

All operations log to `ActivityLogService` with:

-   Category: "Install hub"
-   Message: Operation description
-   Details: Context information (package names, counts, errors)

### Main ViewModel

Status messages are relayed to `MainViewModel.SetStatusMessage()` for display in the main application status bar.

### Install Queue Events

The ViewModel subscribes to `InstallQueue.OperationChanged` to receive real-time updates about installation progress.

## Performance Optimizations

### Virtualization

-   **Package List**: Uses `VirtualizingStackPanel` for efficient rendering of large lists
-   **Queue List**: Uses virtualization with recycling mode and cache length of 4 items
-   **ScrollViewer**: Uses deferred scrolling for smoother performance

### Caching

-   **Catalog Data**: Loaded once and cached in ViewModel
-   **Bundle Packages**: Bundle-to-packages mapping is cached
-   **Package Definitions**: Package lookup dictionary for fast access
-   **Operation Snapshots**: Snapshots are cached to track state changes

### Debouncing

-   **Search Filter**: 110ms debounce to prevent excessive filtering during typing
-   **UI Updates**: Filter suppression during bulk operations

### Background Processing

-   **Catalog Loading**: Runs on background thread to avoid UI blocking
-   **Queue Processing**: Installations run in background task
-   **File I/O**: Preset import/export uses async file operations

## Error Handling

### Catalog Loading Errors

-   **File Not Found**: Returns empty catalog snapshot
-   **Parse Errors**: Throws `InvalidOperationException` with file path
-   **Missing Directory**: Returns empty catalog snapshot

### Queue Operation Errors

-   **Installation Failures**: Captured in operation snapshot
-   **Retry Logic**: Failed operations can be retried automatically
-   **Cancellation**: Operations can be cancelled by user

### Preset Import/Export Errors

-   **File Not Found**: Throws `FileNotFoundException`
-   **Parse Errors**: Throws `InvalidOperationException` with details
-   **Missing Packages**: Reported in import result

## Best Practices

### For Users

1. **Bundle Selection**: Start with bundles to discover related packages
2. **Search Filtering**: Use search to narrow down large catalogs
3. **Multi-Select**: Select multiple packages before queuing for efficiency
4. **Queue Monitoring**: Monitor queue for failed operations that need retry
5. **Preset Export**: Export frequently used selections as presets

### For Developers

1. **Catalog Updates**: Add packages/bundles to YAML files in `data/catalog/`
2. **Bundle Organization**: Group related packages into logical bundles
3. **Package Definitions**: Ensure all required fields are provided
4. **Error Messages**: Provide clear error messages in package definitions
5. **Performance**: Use virtualization for large lists
6. **State Management**: Update UI collections efficiently using synchronization

## Technical Notes

### Catalog Loading

-   Catalog files are loaded from `data/catalog/` directory
-   Supports both `.yml` and `.yaml` extensions
-   Files are loaded recursively from subdirectories
-   Uses YamlDotNet with camelCase naming convention
-   Invalid packages/bundles are skipped (logged but not thrown)

### Queue Processing

-   Uses `System.Threading.Channels` for producer-consumer pattern
-   Single reader, multiple writers
-   Operations processed sequentially (one at a time)
-   PowerShell scripts executed via `PowerShellInvoker`
-   Operation state changes trigger UI updates via events

### Filter Synchronization

-   `SynchronizeCollection()` minimizes UI updates by:
    -   Removing items not in source
    -   Moving existing items to correct positions
    -   Adding only new items
-   Prevents unnecessary UI refreshes

### Pivot Animation

-   Smooth transitions between pivot views using WPF animations
-   Opacity and translate transforms for fade/slide effects
-   180ms enter animation, 160ms exit animation
-   Sine easing functions for natural motion

## Future Enhancements

Potential improvements:

-   Batch operations (install multiple packages in parallel)
-   Advanced filtering (by manager, tags, admin requirement)
-   Installation history persistence
-   Preset templates and sharing
-   Package update detection
-   Dependency resolution
-   Installation scheduling
