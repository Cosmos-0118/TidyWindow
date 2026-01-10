# Essentials Page Documentation

## Overview

The Essentials page provides a curated collection of high-impact Windows maintenance and repair automations. These scripts address common system issues including network connectivity, disk health, Windows Update problems, Defender issues, storage cleanup, and more. The page emphasizes safety, transparency, and user control through sequential execution, restore-point guidance, and detailed logging.

## Purpose

The Essentials page serves as a centralized hub for:

-   **System Repairs**: Automated fixes for common Windows issues
-   **Maintenance Tasks**: Proactive system health checks and optimizations
-   **Diagnostics**: Analysis tools to identify system problems
-   **Safe Automation**: All operations include safety features and can be previewed before execution

## Safety Features

### Why Essentials Automations Are Safe

The Essentials automations are designed with multiple layers of safety to protect your system:

#### 1. **Dry-Run Mode**

Dry-run support is currently exposed only for **Browser Reset & Cache Cleanup** (preview which caches, policies, or repairs would run for selected browsers). Other tasks run live once queued. Expanding dry-run/diagnostic-only toggles to additional tasks is a planned enhancement.

#### 2. **System Restore Points**

Use the **System Restore Manager** task to create a fresh checkpoint before running high-impact automations. System Health offers an optional restore-point toggle; other tasks do not currently enforce restore-point creation at queue time. Rolling back is possible if a recent checkpoint exists.

#### 3. **Sequential Execution**

Operations run one at a time, never in parallel:

-   Prevents resource conflicts
-   Ensures system stability
-   Allows proper error handling
-   Provides clear operation status

#### 4. **Cancellation Support**

All operations can be cancelled at any time:

-   Pending operations can be cancelled before they start
-   Running operations can be stopped (with graceful shutdown where possible)
-   Cancellation is logged for audit purposes

#### 5. **Comprehensive Logging**

Every operation captures output and error transcripts that can be reviewed in the Queue view. Activity Log entries summarize runs. Persisted export of transcripts/JSON reports is a future improvement.

#### 6. **Safe Defaults**

Review task options before queuing. Several tasks enable most toggles by default, so high-impact actions may run unless you opt them off. Safe/conservative presets are recommended and planned for future releases.

#### 7. **Guard Rails**

Built-in protections prevent dangerous operations:

-   **Path Validation**: Scripts validate paths before operations
-   **Service State Checks**: Services are checked before restart
-   **Disk Space Verification**: Cleanup operations verify available space
-   **Permission Checks**: Operations verify required permissions

#### 8. **Transparent Operation**

Full visibility into what's happening:

-   Real-time progress indicators
-   Status messages for each operation phase
-   Detailed descriptions of what each task does
-   Documentation links for deeper understanding

## Architecture

### Component Structure

```
┌─────────────────────────────────────────────────────────────┐
│                    EssentialsPage.xaml                      │
│                    (View - Container)                       │
└───────────────────────┬─────────────────────────────────────┘
                        │
                        ▼
┌─────────────────────────────────────────────────────────────┐
│                 EssentialsViewModel.cs                      │
│              (ViewModel - Business Logic)                   │
└───────┬───────────────────────────────┬─────────────────────┘
        │                               │
        ▼                               ▼
┌───────────────────┐         ┌──────────────────────────┐
│ EssentialsTask    │         │ EssentialsTaskQueue      │
│ Catalog           │         │ (Sequential Processing)  │
│ (Task Definitions) │         │                          │
└─────────┬─────────┘         └──────────-┬──────────────┘
          │                               │
          ▼                               ▼
┌─────────────────────────────────────────────────────────┐
│         PowerShell Scripts (Automation Layer)           │
│  • automation/essentials/*.ps1                          │
│  • Dry-run support                                      │
│  • Safety checks                                        │
│  • Transcript logging                                   │
└─────────────────────────────────────────────────────────┘
```

### Key Classes

#### `EssentialsViewModel`

-   **Location**: `src/TidyWindow.App/ViewModels/EssentialsViewModel.cs`
-   **Responsibilities**:
    -   Manages pivot navigation (Tasks, Queue, Settings)
    -   Coordinates task queuing and execution
    -   Handles task option configuration
    -   Tracks operation status and progress
    -   Manages task details and run dialogs

#### `EssentialsTaskCatalog`

-   **Location**: `src/TidyWindow.Core/Maintenance/EssentialsTaskCatalog.cs`
-   **Responsibilities**:
    -   Defines all available essential tasks
    -   Provides task metadata (name, description, options)
    -   Resolves script paths for tasks
    -   Validates task definitions

#### `EssentialsTaskQueue`

-   **Location**: `src/TidyWindow.Core/Maintenance/EssentialsTaskQueue.cs`
-   **Responsibilities**:
    -   Manages sequential execution queue
    -   Processes operations one at a time
    -   Handles cancellation and retry logic
    -   Persists operation state
    -   Raises events for status changes

#### `EssentialsTaskDefinition`

-   **Location**: `src/TidyWindow.Core/Maintenance/EssentialsTaskDefinition.cs`
-   **Responsibilities**:
    -   Describes a single essential task
    -   Includes metadata (ID, name, category, summary)
    -   Defines available options
    -   Resolves script paths

## User Interface

### Pivot Navigation

The Essentials page uses three pivot views:

1.  **Task Shelf**: Browse and queue essential tasks
2.  **Queue + Output**: Monitor execution and view transcripts
3.  **Automation Settings**: Configure scheduled automation

### Task Shelf View

**Layout**:

-   **Hero Section**: Overview of essentials automation
-   **Task Grid**: Tile layout of available tasks

**Task Card Features**:

-   Task name and category badge
-   Summary description
-   Duration hint (estimated runtime)
-   Status chip (Running, Waiting, Completed, Error)
-   Progress bar (for active operations)
-   Highlight bullets (key features)
-   Action buttons:
    -   "Details" button (opens task details)
    -   "Queue run" button (opens run configuration dialog)

**Task Details Dialog**:

-   Full task description
-   Documentation link
-   Available options with descriptions
-   Option toggles for customization
-   "Queue run" button

**Run Configuration Dialog**:

-   Task name and summary
-   Configurable options
-   Option descriptions
-   "Queue run" or "Set" button (for automation mode)

### Queue + Output View

**Layout**:

-   **Hero Section**: Queue statistics and controls
-   **Operation Timeline**: List of queued/running/completed operations
-   **Operation Details**: Selected operation transcript

**Operation Card Features**:

-   Task name
-   Status label (color-coded)
-   Operation message
-   Attempt count (if retried)
-   Completion timestamp
-   "View details" button
-   "Cancel" button (for active operations)

**Operation Details Panel**:

-   Full output transcript
-   Error transcript (if any)
-   Expandable/collapsible sections
-   Scrollable content

**Queue Controls**:

-   "Retry failed" button
-   "Clear completed" button
-   "Stop active run" button

### Automation Settings View

**Layout**:

-   **Automation Toggle**: Enable/disable scheduled automation
-   **Task Selection**: Multi-select list of tasks to automate
-   **Schedule Configuration**: Frequency and timing options
-   **Save/Cancel** buttons

## Available Tasks

### 1. Network Reset & Cache Flush

-   **Category**: Network
-   **Purpose**: Flushes DNS, ARP, and TCP caches; resets network stacks
-   **Duration**: 3-6 minutes (longer with adapter refresh)
-   **Options**:
    -   Restart adapters after resets
    -   Force DHCP release/renew
    -   Reset Winsock catalog
    -   Reset IP stack
-   **Safety**: Non-destructive; only resets network configuration

### 2. System Health Scanner (SFC & DISM)

-   **Category**: Integrity
-   **Purpose**: Runs SFC and DISM to repair Windows components
-   **Duration**: 30-60 minutes
-   **Options**:
    -   Run SFC /scannow
    -   Run DISM Check/Scan
    -   Run DISM RestoreHealth
    -   Component cleanup
    -   Analyze component store
    -   Create restore point after repairs
-   **Safety**: Creates restore points; uses Windows built-in repair tools

### 3. Disk Checkup & Repair (CHKDSK & SMART)

-   **Category**: Storage
-   **Purpose**: Schedules CHKDSK scans and collects SMART data
-   **Duration**: 8-18 minutes (scan); 30+ minutes (offline repair after reboot)
-   **Options**:
    -   Attempt repairs (/f)
    -   Include surface scan (/r)
    -   Schedule repair if volume is busy
    -   Collect SMART telemetry
-   **Safety**: Scans first; repairs require explicit opt-in; may require reboot

### 4. RAM Purge

-   **Category**: Performance
-   **Purpose**: Frees standby memory and trims working sets
-   **Duration**: 2-4 minutes
-   **Options**:
    -   Clear standby memory lists
    -   Trim heavy working sets
    -   Pause SysMain during purge
-   **Safety**: Memory-only operations; no data loss risk

### 5. System Restore Manager

-   **Category**: Recovery
-   **Purpose**: Creates, lists, and prunes restore points
-   **Duration**: 4-8 minutes (when creating restore point)
-   **Options**: None (single-purpose task)
-   **Safety**: Only manages restore points; no system changes

### 6. Network Fix Suite (Advanced)

-   **Category**: Network
-   **Purpose**: Advanced adapter resets and diagnostics
-   **Duration**: 6-12 minutes
-   **Options**:
    -   Diagnostics only (skip remediation)
    -   Run traceroute
    -   Run pathping
    -   Re-register DNS
-   **Safety**: Diagnostic mode available; remediation is optional

### 7. App Repair Helper (Store/UWP)

-   **Category**: Apps
-   **Purpose**: Resets Microsoft Store infrastructure and re-registers AppX packages
-   **Duration**: 7-12 minutes
-   **Options**:
    -   Reset Microsoft Store cache
    -   Re-register Store components
    -   Repair App Installer
    -   Re-register built-in apps
    -   Refresh AppX frameworks
    -   Repair licensing services
    -   Limit repairs to current user
-   **Safety**: Only affects Store/UWP apps; no system files modified

### 8. Browser Reset & Cache Cleanup

-   **Category**: Apps
-   **Purpose**: Clears caches, resets policies, and optionally repairs Microsoft Edge across Edge, Chrome, Brave, Firefox, and Opera
-   **Duration**: 4-10 minutes (longer if installer repair runs)
-   **Options**:
    -   Include Microsoft Edge (enables WebView cleanup/repair)
    -   Include Google Chrome
    -   Include Brave
    -   Include Firefox
    -   Include Opera
    -   Force close selected browsers
    -   Clear browser profile caches
    -   Clear Edge WebView2 runtime caches
    -   Reset browser policy keys (requires admin for HKLM scope)
    -   Run Edge installer repair
    -   Dry run (preview only)
-   **Safety**: Dry-run previews available; policy resets scoped to chosen browsers; repair uses the signed Microsoft Edge installer

### 9. Windows Update Repair Toolkit

-   **Category**: Updates
-   **Purpose**: Resets Windows Update services, caches, and components
-   **Duration**: 25-45 minutes
-   **Options**:
    -   Reset update services
    -   Reset update components
    -   Re-register update DLLs
    -   Run DISM RestoreHealth
    -   Run SFC /scannow
    -   Trigger Windows Update scan
    -   Reset WU policies
    -   Reset network stack
-   **Safety**: Creates restore point before major operations

### 10. Windows Defender Repair & Deep Scan

-   **Category**: Security
-   **Purpose**: Restores Defender services, updates signatures, runs scans
-   **Duration**: 15-30 minutes
-   **Options**:
    -   Use full scan (vs quick scan)
    -   Skip threat scan
    -   Skip signature update
    -   Skip service heal
    -   Skip real-time heal
-   **Safety**: Uses Windows Defender's built-in repair mechanisms

### 11. Print Spooler Recovery Suite

-   **Category**: Printing
-   **Purpose**: Clears jammed queues and rebuilds spooler services
-   **Duration**: 5-12 minutes
-   **Options**:
    -   Stop & restart spooler services
    -   Clear print queue
    -   Remove stale printer drivers
    -   Re-register spooler DLLs
    -   Reset print isolation policies
-   **Safety**: Dry-run mode available; only affects print subsystem

## Workflow

### Task Queuing

1.  **User Action**

    -   User clicks "Queue run" on a task card
    -   Run configuration dialog opens

2.  **Configuration**

    -   User reviews task description
    -   User configures options (if available)
    -   User clicks "Queue run"

3.  **Queue Operation**

    -   `EssentialsTaskQueue.Enqueue()` is called
    -   Operation is created with parameters
    -   Operation is added to processing channel
    -   Status is updated in UI

4.  **Execution**
    -   Background task processes queue sequentially
    -   PowerShell script is executed with parameters
    -   Output and errors are captured
    -   Status updates are raised

### Operation Processing

1.  **Queue Processing**

    -   Background task (`ProcessQueueAsync`) reads from channel
    -   Operations are processed one at a time (sequential)
    -   Each operation executes its PowerShell script

2.  **Status Updates**

    -   `OperationChanged` event is raised for each state change
    -   ViewModel's `OnQueueOperationChanged` handler updates UI
    -   Operation view models are created/updated
    -   Progress indicators are refreshed

3.  **Operation States**:
    -   **Pending**: Queued, awaiting processing
    -   **Running**: Currently executing
    -   **Succeeded**: Execution completed successfully
    -   **Failed**: Execution failed (can be retried)
    -   **Cancelled**: User cancelled operation

### Cancellation

1.  **User Action**

    -   User clicks "Cancel" on an active operation
    -   `CancelOperationCommand` is invoked

2.  **Cancellation Processing**
    -   `EssentialsTaskQueue.Cancel()` requests cancellation
    -   Operation's cancellation token is triggered
    -   Script execution is stopped (if possible)
    -   Operation status changes to Cancelled

### Retry Failed Operations

1.  **User Action**

    -   User clicks "Retry failed" button
    -   `RetryFailedCommand` is invoked

2.  **Retry Processing**
    -   `EssentialsTaskQueue.RetryFailed()` finds all failed operations
    -   Operations are reset (status → Pending, attempt count incremented)
    -   Operations are re-queued to processing channel

## Safety Mechanisms in Detail

### Dry-Run Implementation

Dry-run mode is implemented at the script level:

```powershell
if ($script:DryRunMode) {
    Write-TidyOutput -Message "[DryRun] Would run: $Description"
    return 0  # Exit without executing
}
```

Scripts check the dry-run flag before executing any destructive operations, logging what would happen instead of performing the action.

### System Restore Points

Restore points are created using Windows' built-in `Checkpoint-Computer` cmdlet:

```powershell
New-TidySystemRestorePoint -Description 'TidyWindow safety checkpoint'
```

This creates a standard Windows restore point that can be used to roll back system changes.

### Sequential Execution

The queue uses `System.Threading.Channels` with a single reader:

-   Only one operation processes at a time
-   Pending operations wait for the current operation to complete
-   Prevents resource conflicts and system instability

### Error Handling

All scripts use:

-   `Set-StrictMode -Version Latest`: Catches common PowerShell errors
-   `$ErrorActionPreference = 'Stop'`: Stops on first error
-   Try-catch blocks around critical operations
-   Error logging to transcripts

### Transcript Logging

Every operation produces:

-   **Output Transcript**: All `Write-Output` messages
-   **Error Transcript**: All `Write-Error` messages
-   **JSON Summary**: Structured operation results
-   **Activity Log Entry**: Human-readable summary

Transcripts are saved to temporary files and can be viewed in the Queue view.

## State Management

### ViewModel Properties

-   `CurrentPivot`: Active pivot view (Tasks, Queue, Settings)
-   `Tasks`: Observable collection of task view models
-   `Operations`: Observable collection of operation view models
-   `SelectedOperation`: Currently selected operation for details
-   `DetailsTask`: Task shown in details dialog
-   `IsTaskDetailsVisible`: Details dialog visibility
-   `PendingRunTask`: Task being configured for run
-   `IsRunDialogVisible`: Run configuration dialog visibility
-   `HasActiveOperations`: Whether any operations are active

### Task View Model State

-   `IsActive`: Whether task has active operation
-   `IsQueued`: Whether task has pending operation
-   `LastStatus`: Last operation status message
-   `StatusChipLabel`: Human-readable status
-   `StatusChipBrush`: Color for status chip
-   `ProgressValue`: Progress bar value (0-1)
-   `ProgressStatusText`: Progress bar text

### Operation View Model State

-   `StatusLabel`: Human-readable status
-   `Message`: Operation message
-   `CompletedAt`: Completion timestamp
-   `IsActive`: Whether operation is currently active
-   `HasErrors`: Whether operation has errors
-   `Output`: Output transcript lines
-   `Errors`: Error transcript lines
-   `IsOutputVisible`: Whether output is expanded
-   `IsCancellationRequested`: Whether cancellation was requested
-   `CanCancel`: Whether operation can be cancelled

## Integration Points

### Activity Log Service

All operations log to `ActivityLogService` with:

-   Category: "Essentials"
-   Message: Operation description
-   Details: Context information (task name, options, output, errors)

### Main ViewModel

Status messages are relayed to `MainViewModel.SetStatusMessage()` for display in the main application status bar.

### Queue State Persistence

Operation state is persisted to disk:

-   Operations survive application restarts
-   Pending operations are restored on startup
-   Completed operations are retained until cleared

## Best Practices

### For Users

1.  **Start with Dry-Run**: Use dry-run mode for storage cleanup tasks to preview changes
2.  **Create Restore Points**: Enable restore point creation for major operations
3.  **Review Transcripts**: Check operation transcripts in the Queue view for details
4.  **One at a Time**: Let operations complete before queuing more
5.  **Read Descriptions**: Review task details before queuing to understand what will happen

### For Developers

1.  **Safety First**: Always include dry-run support for destructive operations
2.  **Comprehensive Logging**: Log all operations to transcripts
3.  **Error Handling**: Use try-catch blocks and proper error reporting
4.  **User Feedback**: Provide clear status messages throughout execution
5.  **Documentation**: Include detailed descriptions and documentation links

## Technical Notes

### Script Execution

-   Scripts are executed via `PowerShellInvoker` which handles:
    -   PowerShell 7+ preference (falls back to Windows PowerShell)
    -   Execution policy management
    -   Output/error stream capture
    -   Cancellation token propagation

### Path Resolution

Script paths are resolved relative to `AppContext.BaseDirectory` with fallback to parent directories, ensuring scripts are found in both development and deployment scenarios.

### Option Parameter Building

Task options are converted to PowerShell parameters:

-   `EmitWhenTrue`: Parameter is included when option is enabled
-   `EmitWhenFalse`: Parameter is included when option is disabled (for "Skip\*" switches)

### Queue State Persistence

Operation state is serialized to JSON and persisted:

-   Operations are saved after each state change
-   State is restored on application startup
-   Running operations are reset to Pending on restore (safe default)

## Future Enhancements

Potential improvements:

-   Parallel execution for independent tasks
-   Task dependencies and prerequisites
-   Custom task definitions
-   Task templates and presets
-   Scheduled automation with conditions
-   Task result analysis and recommendations
-   Integration with Windows Event Log

