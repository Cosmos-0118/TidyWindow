# Maintenance Page Documentation

## Overview

The Maintenance page is a comprehensive package management interface that allows users to update, remove, and automate maintenance of installed packages across multiple package managers (winget, Chocolatey, Scoop). It provides a centralized control panel for keeping software up to date and managing package lifecycles.

## Purpose

The Maintenance page serves as the primary interface for:

-   **Package Inventory**: View all installed packages across winget, Chocolatey, and Scoop
-   **Update Management**: Queue and execute package updates with version targeting
-   **Package Removal**: Remove packages with optional force cleanup
-   **Automated Maintenance**: Configure scheduled automatic updates for selected packages
-   **Operation Monitoring**: Track maintenance operations in a queue with detailed transcripts
-   **Failure Handling**: Intelligent suppression of non-actionable failures with automatic retry

## Safety Features

### Why Maintenance Operations Are Safe

The Maintenance page implements multiple safety mechanisms to protect system stability:

#### 1. **Intelligent Failure Suppression**

When operations fail due to non-actionable reasons, the system automatically suppresses future attempts:

-   **Automatic Detection**: Identifies specific failure patterns (installer hash mismatch, cannot upgrade, unknown version)
-   **Suppression Registration**: Automatically registers suppressions to prevent repeated failures
-   **Version Tracking**: Tracks the latest known version to detect when new updates become available
-   **Automatic Resumption**: Removes suppression when package is updated manually or new version is detected
-   **Clear Messaging**: Provides user-friendly messages explaining why automatic updates are suppressed

This prevents wasted attempts and user frustration from repeated failures.

#### 2. **Installer Busy Detection and Retry**

The system intelligently handles cases where another installer is already running:

-   **Automatic Detection**: Detects installer busy conditions via exit codes and error messages
-   **Exponential Backoff**: Uses increasing delays (10s, 20s, 30s, up to 60s) between retry attempts
-   **Maximum Attempts**: Limits retries to 6 attempts to prevent infinite loops
-   **Status Updates**: Provides clear status messages during wait periods
-   **Resume Capability**: Automatically resumes operation after wait period

This ensures operations complete successfully even when system installers are busy.

#### 3. **Elevation Handling**

Administrator privileges are handled safely and transparently:

-   **Automatic Detection**: Determines when operations require administrator privileges
-   **User Confirmation**: Prompts user before restarting with elevated privileges
-   **Graceful Restart**: Restarts application with administrator privileges when needed
-   **State Preservation**: Maintains operation state across restarts
-   **Clear Communication**: Explains why elevation is required

#### 4. **Operation Queue Management**

Operations are processed sequentially to prevent conflicts:

-   **Sequential Processing**: One operation at a time prevents package manager conflicts
-   **Duplicate Prevention**: Prevents queuing multiple operations for the same package
-   **Status Tracking**: Real-time status updates for all queued operations
-   **Transcript Logging**: Full output and error logs for each operation
-   **Retry Capability**: Failed operations can be retried individually or in bulk

#### 5. **Version Validation**

Package versions are validated before operations:

-   **Version Normalization**: Normalizes version strings for accurate comparison
-   **Update Detection**: Accurately detects when updates are available
-   **Target Version Support**: Allows specifying exact version for updates
-   **Status Tracking**: Tracks before/after status for each operation

#### 6. **Comprehensive Logging**

All operations are logged with full context:

-   **Operation Logs**: Detailed logs saved for each operation
-   **Activity Log**: All operations appear in Activity Log with details
-   **Transcript Capture**: Full output and error streams captured
-   **Log File Paths**: Log file locations provided for troubleshooting

#### 7. **Warning System**

Warnings are displayed for important conditions:

-   **Suppression Warnings**: Alerts when automatic updates are suppressed
-   **Toggle Visibility**: Warnings can be shown or hidden
-   **Contextual Messages**: Clear explanations of why warnings exist
-   **Automatic Cleanup**: Warnings removed when conditions resolve

## Architecture

### Component Structure

```
┌─────────────────────────────────────────────────────────────┐
│              PackageMaintenancePage.xaml                    │
│                    (View - Container)                       │
└───────────────────────┬─────────────────────────────────────┘
                        │
                        ▼
┌─────────────────────────────────────────────────────────────┐
│          PackageMaintenanceViewModel.cs                     │
│              (ViewModel - Business Logic)                   │
└───────┬───────────────────────────────┬─────────────────────┘
        │                               │
        ▼                               ▼
┌───────────────────┐         ┌──────────────────────────┐
│ PackageInventory  │         │ PackageMaintenance       │
│ Service           │         │ Service                  │
│ (Discovery)       │         │ (Update/Remove)          │
└─────────┬─────────┘         └──────────┬───────────────┘
          │                               │
          ▼                               ▼
┌─────────────────────────────────────────────────────────┐
│         PowerShell Scripts                              │
│  • automation/scripts/update-catalog-package.ps1        │
│  • automation/scripts/remove-catalog-package.ps1        │
│  • Catalog-aware operations                             │
│  • Version targeting                                    │
└─────────────────────────────────────────────────────────┘
```

### Key Classes

#### `PackageMaintenanceViewModel`

-   **Location**: `src/TidyWindow.App/ViewModels/PackageMaintenanceViewModel.cs`
-   **Responsibilities**:
    -   Manages package inventory display
    -   Coordinates package refresh operations
    -   Handles operation queuing and processing
    -   Manages suppression state
    -   Handles installer busy detection and retry
    -   Coordinates elevation requests
    -   Manages three view sections (Packages, Queue, Automation)

#### `PackageMaintenanceService`

-   **Location**: `src/TidyWindow.Core/Maintenance/PackageMaintenanceService.cs`
-   **Responsibilities**:
    -   Executes PowerShell update scripts
    -   Executes PowerShell removal scripts
    -   Parses JSON results into strongly-typed models
    -   Handles force cleanup operations
    -   Maps script results to domain models

#### `MaintenanceAutomationViewModel`

-   **Location**: `src/TidyWindow.App/ViewModels/MaintenanceAutomationViewModel.cs`
-   **Responsibilities**:
    -   Manages automated update scheduling
    -   Handles package selection for automation
    -   Configures update intervals
    -   Tracks last run time
    -   Integrates with `MaintenanceAutoUpdateScheduler`

#### `PackageMaintenanceItemViewModel`

-   **Location**: `src/TidyWindow.App/ViewModels/PackageMaintenanceViewModel.cs`
-   **Responsibilities**:
    -   Represents a single package in the UI
    -   Displays package status and update availability
    -   Manages selection state
    -   Tracks suppression state
    -   Provides computed properties for UI binding

#### `PackageMaintenanceOperationViewModel`

-   **Location**: `src/TidyWindow.App/ViewModels/PackageMaintenanceViewModel.cs`
-   **Responsibilities**:
    -   Represents a single maintenance operation
    -   Tracks operation status (Pending, Waiting, Running, Succeeded, Failed)
    -   Stores operation transcripts (output and errors)
    -   Provides log file paths
    -   Displays operation details

## User Interface

### Layout Structure

The Maintenance page uses three pivot sections:

1.  **Packages**: Browse and queue package updates/removals
2.  **Queue**: Monitor operation progress and view transcripts
3.  **Automation**: Configure scheduled automatic updates

### Packages Section

**Header**:

-   **Title**: "Installed packages"
-   **Summary**: Dynamic summary of package count and available updates
-   **Last Refreshed**: Timestamp of last inventory refresh
-   **Action Button**: "Select all" for bulk selection

**Filters Panel**:

-   **Keyword Search**: Text box for filtering by name, identifier, manager, or tags
-   **Manager Filter**: Dropdown to filter by package manager (All managers, winget, choco, scoop)
-   **Updates Only**: Checkbox to show only packages with available updates
-   **Bulk Actions**: "Queue selected" button to queue updates for all selected packages

**Keyboard Shortcuts**:

-   **Q**: Queue update for selected package
-   **R**: Remove selected package
-   **F**: Force cleanup for selected package

**Package List**:

-   Virtualized list view with alternating row colors
-   Each package card displays:
    -   **Checkbox**: For bulk selection
    -   **Initial Badge**: First letter of package name
    -   **Package Name**: Display name or identifier
    -   **Version Display**: Current version or "Current → Available" if update available
    -   **Manager Badge**: Package manager (winget, Chocolatey, Scoop)
    -   **Source**: Package source/repository
    -   **Tags**: Package tags (if available)
    -   **Status Indicators**: Update available, suppressed, queued, busy
    -   **Action Buttons**: Update, Remove, Force Remove, Details

**Package Details Dialog**:

-   **Package Information**: Full package details including summary, homepage, tags
-   **Version Information**: Installed version, available version, target version
-   **Catalog Information**: Install catalog ID and requirements
-   **Suppression Status**: Shows if automatic updates are suppressed and why

**Warnings Panel**:

-   **Toggle Button**: Show/hide warnings
-   **Warning List**: Displays suppression warnings and other important messages
-   **Warning Format**: Warning icon, message, and context

### Queue Section

**Header**:

-   **Title**: "Maintenance queue"
-   **Description**: "Monitor pending operations, retry failures, and review history"

**Action Buttons**:

-   **Retry Failed**: Retries all failed operations
-   **Clear Completed**: Removes completed operations from queue

**Operation List**:

-   Timeline-style list of operations
-   Each operation card displays:
    -   **Operation Type**: Update, Removal, or Force Removal
    -   **Package Name**: Package being operated on
    -   **Status Badge**: Pending, Waiting, Running, Completed, Failed
    -   **Status Message**: Current operation status
    -   **Timestamps**: Enqueued, started, completed times
    -   **Transcript**: Output and error logs (expandable)
    -   **Log File**: Link to operation log file (if available)

**Operation Details Dialog**:

-   **Full Transcript**: Complete output and error logs
-   **Operation Metadata**: Manager, package ID, exit code, versions
-   **Status Information**: Before/after status, success/failure
-   **Log File Path**: Direct link to log file

### Automation Section

**Header**:

-   **Title**: "Maintenance automation cockpit"
-   **Description**: "Choose which packages stay up to date automatically or let TidyWindow handle every detected update."

**Statistics**:

-   **Inventory Summary**: Package count and update availability
-   **Selection Summary**: Count of selected packages or "all packages" status

**Automation Settings**:

-   **Enable Automation**: Toggle to enable/disable automated updates
-   **Update All Packages**: Toggle to update all packages with available updates
-   **Update Interval**: Dropdown to select interval (3 hours, 6 hours, 12 hours, daily, 3 days, weekly)
-   **Last Run**: Display of last automation run time

**Package Selection**:

-   **Refresh Button**: Refreshes package list from inventory
-   **Package List**: Checkbox list of all installed packages
-   **Package Details**: Shows package name, version, manager, admin requirement
-   **Selection State**: Tracks which packages are selected for automation

**Action Buttons**:

-   **Apply Settings**: Saves automation configuration
-   **Run Now**: Executes automation immediately

## Workflow

### Package Inventory Refresh

1.  **User Action**

    -   User clicks "Refresh" button or page loads
    -   `RefreshCommand` is invoked

2.  **Inventory Collection**

    -   `PackageInventoryService.GetInventoryAsync()` executes PowerShell script
    -   Script queries winget, Chocolatey, and Scoop for installed packages
    -   Matches packages against install catalog

3.  **Result Processing**

    -   JSON payload is parsed from script output
    -   Packages are mapped to `PackageMaintenanceItemViewModel` models
    -   Suppression state is resolved from user preferences
    -   Warnings are generated for suppressed packages

4.  **UI Update**

    -   `ApplySnapshot()` updates package collection
    -   Package cards are created/updated
    -   Filters are refreshed
    -   Summary and statistics are updated
    -   Warnings are displayed

### Package Update

1.  **User Action**

    -   User clicks "Update" on a package card or presses Q
    -   `QueueUpdateCommand` is invoked

2.  **Validation**

    -   Checks if package can be updated (has update available, not suppressed)
    -   Resolves package identifier
    -   Checks if operation already queued
    -   Determines if elevation is required

3.  **Elevation Check**

    -   If elevation required, prompts user for confirmation
    -   Restarts application with administrator privileges if confirmed
    -   Operation is queued after restart

4.  **Operation Queuing**

    -   `EnqueueMaintenanceOperation()` creates operation view model
    -   Operation is added to processing queue
    -   Package is marked as queued and busy
    -   Operation appears in queue section

5.  **Operation Processing**

    -   Background task processes queue sequentially
    -   `PackageMaintenanceService.UpdateAsync()` executes PowerShell script
    -   Script parameters: Manager, PackageId, TargetVersion (optional), RequiresAdmin

6.  **Installer Busy Handling**

    -   If installer busy detected, operation enters wait state
    -   Exponential backoff delay applied (10s, 20s, 30s, up to 60s)
    -   Operation retries up to 6 times
    -   Status messages updated during wait

7.  **Result Processing**

    -   Script result is parsed from JSON
    -   Operation status updated (Succeeded or Failed)
    -   Package state updated (version, update availability)
    -   Transcript captured (output and errors)

8.  **Suppression Handling**

    -   If failure is non-actionable, suppression is registered
    -   Package is marked as suppressed
    -   Warning is added to warnings list
    -   Future automatic updates are skipped

9.  **UI Update**

    -   Operation status updated in queue
    -   Package card refreshed with new state
    -   Activity log entry created
    -   Status message displayed

### Package Removal

1.  **User Action**

    -   User clicks "Remove" or "Force Remove" on a package card
    -   `RemoveCommand` or `ForceRemoveCommand` is invoked

2.  **Validation**

    -   Checks if package can be removed (has identifier)
    -   Checks if operation already queued
    -   Determines if elevation is required

3.  **Operation Queuing**

    -   Operation is queued with appropriate kind (Remove or ForceRemove)
    -   Force cleanup flag is set for force removal

4.  **Operation Processing**

    -   `PackageMaintenanceService.RemoveAsync()` or `ForceRemoveAsync()` executes PowerShell script
    -   Script removes package via appropriate package manager
    -   Force cleanup removes additional files/registry entries

5.  **Result Processing**

    -   Operation status updated
    -   Package removed from inventory (if successful)
    -   Transcript captured

### Automated Updates

1.  **Configuration**

    -   User enables automation in Automation section
    -   User selects packages or enables "Update all packages"
    -   User sets update interval
    -   User clicks "Apply Settings"

2.  **Settings Persistence**

    -   Settings are saved via `MaintenanceAutoUpdateScheduler`
    -   Scheduler is configured with interval and target packages

3.  **Scheduled Execution**

    -   Scheduler runs at configured intervals
    -   Inventory is refreshed
    -   Packages with updates are queued
    -   Operations are processed sequentially

4.  **Manual Execution**

    -   User clicks "Run Now" in Automation section
    -   Scheduler executes immediately
    -   Updates are queued and processed

## Safety Mechanisms in Detail

### Suppression System

The suppression system prevents repeated failures from non-actionable errors:

**Suppression Triggers**:

-   **Installer Hash Mismatch**: winget reports installer hash mismatch
-   **Cannot Upgrade**: Package cannot be upgraded via winget (requires manual update)
-   **Unknown Version**: Package version cannot be determined by winget

**Suppression Registration**:

-   Suppression entry created with:
    -   Manager and package identifier
    -   Reason code (`ManualUpgradeRequired`)
    -   User-friendly message
    -   Exit code
    -   Latest known version
    -   Requested version

**Suppression Resolution**:

-   **Manual Update**: Suppression removed when package is updated manually
-   **New Version**: Suppression removed when new version is detected
-   **Up to Date**: Suppression removed when package is already up to date

**Suppression Display**:

-   Suppressed packages show suppression message
-   Update button is disabled for suppressed packages
-   Warnings list shows suppression details

### Installer Busy Detection

The system detects when another installer is running:

**Detection Methods**:

-   **Exit Codes**: Specific exit codes (1618, 0x80070652)
-   **Error Messages**: Text patterns in output/errors:
    -   "another installation is already in progress"
    -   "another installer is already running"
    -   "msiexec is already running"
    -   "error_install_already_running"

**Retry Logic**:

-   **Maximum Attempts**: 6 attempts
-   **Initial Delay**: 10 seconds
-   **Maximum Delay**: 60 seconds
-   **Exponential Backoff**: Delay increases with each attempt (10s, 20s, 30s, 40s, 50s, 60s)

**Wait State**:

-   Operation enters "Waiting" status
-   Status message shows wait reason and retry count
-   Operation automatically resumes after delay

### Elevation Handling

Administrator privileges are handled transparently:

**Elevation Detection**:

-   Package catalog flags (`RequiresAdmin`)
-   Manager requirements (winget, Chocolatey require elevation)

**Elevation Flow**:

1.  Operation requires elevation
2.  User is prompted: "Package requires administrator privileges. Restart as administrator?"
3.  If confirmed, application restarts with elevation
4.  Operation state is preserved
5.  Operation continues after restart

**Elevation States**:

-   **Already Elevated**: Operation proceeds immediately
-   **Elevation Failed**: Error message displayed, operation cancelled
-   **User Cancelled**: Operation cancelled, logged

## Data Models

### PackageMaintenanceItemViewModel

Represents a single package in the maintenance UI:

-   **Manager**: Package manager (winget, choco, scoop)
-   **PackageIdentifier**: Package identifier
-   **DisplayName**: Package display name
-   **InstalledVersion**: Currently installed version
-   **AvailableVersion**: Latest available version
-   **HasUpdate**: Whether update is available
-   **TargetVersion**: User-specified target version (optional)
-   **Source**: Package source/repository
-   **Tags**: Package tags
-   **InstallPackageId**: Install catalog package ID
-   **RequiresAdministrativeAccess**: Whether admin privileges required
-   **IsSuppressed**: Whether automatic updates are suppressed
-   **SuppressionMessage**: Message explaining suppression
-   **IsSelected**: Whether package is selected for bulk operations
-   **IsBusy**: Whether operation is in progress
-   **IsQueued**: Whether operation is queued
-   **QueueStatus**: Current queue status message
-   **LastOperationMessage**: Last operation result message
-   **LastOperationSucceeded**: Whether last operation succeeded

### PackageMaintenanceOperationViewModel

Represents a single maintenance operation:

-   **Id**: Unique operation identifier
-   **Item**: Package being operated on
-   **Kind**: Operation kind (Update, Remove, ForceRemove)
-   **Status**: Operation status (Pending, Waiting, Running, Succeeded, Failed)
-   **Message**: Current status message
-   **EnqueuedAt**: When operation was queued
-   **StartedAt**: When operation started
-   **CompletedAt**: When operation completed
-   **Output**: Operation output transcript
-   **Errors**: Operation error transcript
-   **LogFilePath**: Path to operation log file

### PackageMaintenanceResult

Represents the result of a maintenance operation:

-   **Operation**: Operation type
-   **Manager**: Package manager used
-   **PackageId**: Package identifier
-   **Success**: Whether operation succeeded
-   **Summary**: Operation summary message
-   **RequestedVersion**: Version that was requested
-   **StatusBefore**: Package status before operation
-   **StatusAfter**: Package status after operation
-   **InstalledVersion**: Installed version after operation
-   **LatestVersion**: Latest available version
-   **Attempted**: Whether operation was attempted
-   **Output**: Operation output lines
-   **Errors**: Operation error lines
-   **ExitCode**: Process exit code
-   **LogFilePath**: Path to operation log file

### MaintenanceSuppressionEntry

Represents a suppression of automatic updates:

-   **Manager**: Package manager
-   **PackageId**: Package identifier
-   **Reason**: Suppression reason code
-   **Message**: User-friendly suppression message
-   **ExitCode**: Exit code that triggered suppression
-   **LatestKnownVersion**: Latest version known at suppression time
-   **RequestedVersion**: Version that was requested

## State Management

### ViewModel Properties

-   `Packages`: Observable collection of package view models
-   `Operations`: Observable collection of operation view models
-   `Warnings`: Observable collection of warning messages
-   `ManagerFilters`: Observable collection of manager filter options
-   `IsBusy`: Whether any operation is in progress
-   `SearchText`: Search filter text
-   `SelectedManager`: Selected manager filter
-   `UpdatesOnly`: Whether to show only packages with updates
-   `SelectedPackage`: Currently selected package
-   `SelectedOperation`: Currently selected operation
-   `ActiveSection`: Active view section (Packages, Queue, Automation)
-   `IsPackageDetailsVisible`: Package details dialog visibility
-   `IsOperationDetailsVisible`: Operation details dialog visibility
-   `AreWarningsVisible`: Warnings panel visibility
-   `SummaryText`: Dynamic summary text
-   `LastRefreshedDisplay`: Last refresh timestamp display

### Operation Queue State

-   `_pendingOperations`: Queue of pending operation requests
-   `_isProcessingOperations`: Whether queue processor is running
-   `_operationLock`: Lock for thread-safe queue access

### Suppression State

-   Suppressions stored in `UserPreferencesService`
-   Resolved on inventory refresh
-   Applied to package view models
-   Warnings generated for suppressed packages

## Integration Points

### Activity Log Service

All operations log to `ActivityLogService` with:

-   Category: "Maintenance" or "Maintenance automation"
-   Message: Operation description
-   Details: Context information (package names, versions, exit codes, etc.)

### Main ViewModel

Status messages are relayed to `MainViewModel.SetStatusMessage()` for display in the main application status bar.

### Work Tracker

Operations register with `IAutomationWorkTracker`:

-   Work type: `AutomationWorkType.Maintenance`
-   Description: Operation-specific description
-   Token: GUID for tracking operation lifecycle

### User Preferences Service

Suppressions are stored in `UserPreferencesService`:

-   Persisted across application restarts
-   Resolved on inventory refresh
-   Automatically cleaned up when conditions change

### Maintenance Auto Update Scheduler

Automation settings are managed by `MaintenanceAutoUpdateScheduler`:

-   Schedules automated update runs
-   Executes updates at configured intervals
-   Manages target package selection
-   Tracks last run time

## Best Practices

### For Users

1.  **Review Suppressions**: Check warnings to understand why packages are suppressed
2.  **Monitor Queue**: Watch operation queue for failures that need attention
3.  **Use Automation**: Configure automation for packages you want to keep updated
4.  **Check Logs**: Review operation logs for detailed information
5.  **Retry Failed**: Use "Retry failed" to retry operations that failed due to transient issues

### For Developers

1.  **Handle Failures Gracefully**: Always check for non-actionable failures and suppress appropriately
2.  **Detect Installer Busy**: Always check for installer busy conditions and retry with backoff
3.  **Log Comprehensively**: Log all operations with full context for troubleshooting
4.  **Validate Versions**: Always normalize and validate version strings
5.  **Handle Elevation**: Always check elevation requirements and handle gracefully

## Technical Notes

### Script Execution

-   Scripts are executed via `PowerShellInvoker` which handles:
    -   PowerShell 7+ preference (falls back to Windows PowerShell)
    -   Execution policy management
    -   Output/error stream capture
    -   JSON payload extraction

### Script Paths

-   **Update Script**: `automation/scripts/update-catalog-package.ps1`
-   **Remove Script**: `automation/scripts/remove-catalog-package.ps1`
-   Scripts can be overridden via environment variables:
    -   `TIDYWINDOW_PACKAGE_UPDATE_SCRIPT`
    -   `TIDYWINDOW_PACKAGE_REMOVE_SCRIPT`

### Version Normalization

-   Versions are normalized for accurate comparison:
    -   Replaces `_` and `-` with `.`
    -   Removes duplicate dots
    -   Trims leading/trailing dots
    -   Handles "unknown" and "not installed" as null

### Operation Processing

-   Operations are processed sequentially in a background task
-   Queue is thread-safe using locks
-   Operations are processed one at a time to prevent conflicts
-   Each operation has its own cancellation token

### Suppression Persistence

-   Suppressions are stored in user preferences
-   Format: `MaintenanceSuppressionEntry` with manager, package ID, reason, message, exit code, versions
-   Automatically cleaned up when conditions change
-   Resolved on each inventory refresh

## Future Enhancements

Potential improvements:

-   Batch operation optimization (parallel processing where safe)
-   Operation scheduling (schedule operations for specific times)
-   Operation history (persistent history of all operations)
-   Package dependency tracking
-   Rollback capability for failed updates
-   Operation templates (predefined operation sets)
-   Integration with package manager notifications
-   Advanced filtering and sorting options

