# PathPilot Page Documentation

## Overview

PathPilot is a system-wide runtime management tool that allows users to control which version of a runtime (Python, Node.js, .NET, PowerShell, etc.) is prioritized on the Windows PATH. It provides a visual interface for discovering installed runtimes, reviewing PATH resolution order, and safely switching between different installations.

## Purpose

PathPilot serves as a centralized control panel for:

-   **Runtime Discovery**: Automatically detects installed runtimes across the system
-   **PATH Management**: Visualizes and controls which runtime version is active on PATH
-   **Version Switching**: Safely promotes a specific runtime installation to the top of PATH
-   **System-Wide Control**: Manages machine-level PATH (affects all Windows accounts)

## Safety Features

### Why PathPilot Operations Are Safe

PathPilot is designed with multiple safety mechanisms to protect system integrity:

#### 1. **Automatic Registry Backup**

Before making any PATH changes, PathPilot automatically creates a backup:

-   **Backup Location**: `%ProgramData%\TidyWindow\PathPilot\backup-YYYYMMDD-HHMMSS.reg`
-   **Backup Method**: Uses `reg.exe export` to capture the entire registry key
-   **Fallback**: If registry export fails, creates a manual `.reg` file with the current PATH value
-   **Backup Key**: `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment`

This backup can be imported via `reg.exe import` to restore the previous PATH state if needed.

#### 2. **Machine Scope Warning**

PathPilot requires explicit acknowledgment before making changes:

-   **Warning Dialog**: Users must acknowledge that changes affect all Windows accounts
-   **One-Time Acknowledgment**: Warning is shown once per session
-   **Clear Messaging**: Explains that HKLM + machine PATH modifications are system-wide

This prevents accidental changes that could affect other users on the system.

#### 3. **Registry Snapshot**

The system takes a snapshot before editing:

-   **Snapshot Target**: `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment`
-   **Documentation**: UI clearly states that snapshots are taken before PATH edits
-   **Audit Trail**: Backup path is logged in operation results

#### 4. **Path Validation**

Multiple validation checks ensure safe operations:

-   **Path Existence**: Verifies target directory exists before switching
-   **Executable Verification**: Confirms executable file exists at target path
-   **Normalization**: Normalizes paths to handle case-insensitive comparisons
-   **Duplicate Detection**: Identifies and handles duplicate PATH entries

#### 5. **No-Change Detection**

PathPilot detects when no change is needed:

-   **Already First**: If target is already first on PATH, no changes are made
-   **No Backup Created**: Backups are only created when PATH actually changes
-   **Status Reporting**: Clear messages indicate when no action was taken

#### 6. **Comprehensive Logging**

All operations are logged with full context:

-   **Operation Logs**: Detailed logs saved to `%ProgramData%\TidyWindow\PathPilot\`
-   **Backup Path**: Logged in switch results for easy restoration
-   **Activity Log**: All operations appear in Activity Log with details
-   **Export Capabilities**: JSON and Markdown exports for audit trails

#### 7. **Read-Only Inventory**

Inventory collection is completely safe:

-   **No Modifications**: Inventory scan only reads system state
-   **Discovery Only**: Scans files, directories, and registry without changes
-   **Safe Probes**: Registry probes are read-only operations

#### 8. **Incremental PATH Updates**

PATH modifications are minimal and targeted:

-   **Targeted Changes**: Only moves target directory to front; preserves other entries
-   **Duplicate Removal**: Removes duplicate entries for the same runtime
-   **Order Preservation**: Maintains order of non-runtime PATH entries

## Architecture

### Component Structure

```
┌─────────────────────────────────────────────────────────────┐
│                    PathPilotPage.xaml                       │
│                    (View - Container)                       │
└───────────────────────┬─────────────────────────────────────┘
                        │
                        ▼
┌─────────────────────────────────────────────────────────────┐
│                 PathPilotViewModel.cs                       │
│              (ViewModel - Business Logic)                   │
└───────┬───────────────────────────────┬─────────────────────┘
        │                               │
        ▼                               ▼
┌───────────────────┐         ┌──────────────────────────┐
│ PathPilotInventory│         │ PathPilot Switch         │
│ Service           │         │ Operation                │
│ (Discovery)       │         │ (PATH Modification)       │
└─────────┬─────────┘         └──────────┬───────────────┘
          │                              │
          ▼                              ▼
┌─────────────────────────────────────────────────────────┐
│         PowerShell Script                               │
│  • automation/scripts/Get-PathPilotInventory.ps1        │
│  • Runtime discovery                                    │
│  • PATH switching with backup                           │
│  • Export generation                                    │
└─────────────────────────────────────────────────────────┘
```

### Key Classes

#### `PathPilotViewModel`

-   **Location**: `src/TidyWindow.App/ViewModels/PathPilotViewModel.cs`
-   **Responsibilities**:
    -   Manages runtime inventory display
    -   Coordinates inventory refresh operations
    -   Handles runtime switching with safety checks
    -   Manages dialog states (installations, details, resolution order)
    -   Handles export operations (JSON/Markdown)
    -   Tracks machine scope warning acknowledgment

#### `PathPilotInventoryService`

-   **Location**: `src/TidyWindow.Core/PathPilot/PathPilotInventoryService.cs`
-   **Responsibilities**:
    -   Executes PowerShell inventory script
    -   Parses JSON results into strongly-typed models
    -   Handles runtime switching operations
    -   Generates export files (JSON/Markdown)
    -   Maps script results to domain models

#### `PathPilotRuntimeCardViewModel`

-   **Location**: `src/TidyWindow.App/ViewModels/PathPilotViewModel.cs`
-   **Responsibilities**:
    -   Represents a single runtime in the UI
    -   Displays runtime status and installations
    -   Provides computed properties for UI binding
    -   Builds status badges and summary text

#### `PathPilotInstallationViewModel`

-   **Location**: `src/TidyWindow.App/ViewModels/PathPilotViewModel.cs`
-   **Responsibilities**:
    -   Represents a single installation of a runtime
    -   Displays installation details (version, architecture, source)
    -   Provides switch request for promoting installation

## User Interface

### Layout Structure

**Header Section**:

-   **Title**: "PathPilot switchboard"
-   **Summary**: Dynamic summary of runtime status
-   **Last Refreshed**: Timestamp of last inventory scan
-   **Action Buttons**: "Scan runtimes", "Export JSON", "Export Markdown"

**Statistics Cards**:

-   **Total Runtimes**: Count of detected runtimes
-   **Missing Runtimes**: Count of runtimes with no installations
-   **Drift Detected**: Count of runtimes not matching desired version

**Runtime Cards Grid**:

-   Virtualized tile layout (1-3 columns based on viewport width)
-   Each card represents one runtime (Python, Node.js, .NET, etc.)

### Runtime Card Features

**Card Header**:

-   Runtime friendly name (e.g., "Python", "Node.js", ".NET")
-   Version chip showing active or desired version
-   Color-coded indicator based on runtime type

**Card Body**:

-   **Display Name**: Full runtime name/description
-   **Summary**: Active installation summary or installation count
-   **Status Badges**: Color-coded badges:
    -   🟢 **Active**: Runtime has active PATH resolution
    -   🟡 **Missing**: No installations detected
    -   🟠 **Drift**: Active version doesn't match desired
    -   🔵 **Duplicates**: Multiple PATH entries detected
    -   ⚠️ **Unknown PATH winner**: Active path doesn't match known installation

**Card Details**:

-   **Active Path**: Currently active executable path (if available)
-   **PATH Entry**: Directory currently on PATH (if available)
-   **Resolution Order**: Count of candidates observed on PATH

**Card Actions**:

-   **Switch Button**: Opens installations dialog (enabled only if installations exist)
-   **Details Button**: Opens details dialog
-   **Resolution Order Button**: Shows PATH resolution order (if available)

### Installations Dialog

**Layout**:

-   **Header**: Runtime name, display name, status badges
-   **Active Path Section**: Shows currently active executable and PATH entry
-   **Installations List**: Scrollable list of all detected installations

**Installation Card Features**:

-   **Version Display**: Version label (e.g., "Python 3.11.5")
-   **Architecture**: Architecture badge (x64, x86, ARM64, etc.)
-   **Source**: Installation source (Registry, PathGlob, Directory, etc.)
-   **Directory**: Installation directory path
-   **Active Indicator**: Badge showing if this installation is currently active
-   **Switch Button**: Promotes this installation to top of PATH
-   **Notes**: Additional information about the installation

### Details Dialog

**Layout**:

-   **Header**: Runtime name and status badges
-   **Details Section**: Comprehensive runtime information
-   **Active Resolution**: Current PATH resolution details
-   **Installations Summary**: Overview of all installations

### Resolution Order Dialog

**Layout**:

-   **Header**: Runtime name
-   **Resolution Order List**: Ordered list of PATH candidates
-   Shows which directories would be checked in order when resolving the runtime executable

### Machine Scope Warning Dialog

**Layout**:

-   **Warning Message**: Explains that changes affect all Windows accounts
-   **Acknowledgment**: User must confirm understanding
-   **Action Buttons**: "Confirm" or "Cancel"

## Workflow

### Inventory Refresh

1.  **User Action**

    -   User clicks "Scan runtimes" button
    -   `RefreshCommand` is invoked

2.  **Inventory Collection**

    -   `PathPilotInventoryService.GetInventoryAsync()` executes PowerShell script
    -   Script path: `automation/scripts/Get-PathPilotInventory.ps1`
    -   Script reads runtime configuration from `data/catalog/runtime-inventory.json`

3.  **Runtime Discovery**

    -   Script scans system using discovery methods:
        -   **Path Globs**: File patterns (e.g., `C:\Python*\python.exe`)
        -   **Directory Globs**: Directory patterns (e.g., `C:\Program Files\Nodejs\*`)
        -   **Registry Probes**: Registry keys (e.g., `HKLM\SOFTWARE\Python\PythonCore\*\InstallPath`)
    -   For each runtime, discovers all installations
    -   Determines which installation is currently active on PATH

4.  **Result Processing**

    -   JSON payload is parsed from script output
    -   Runtimes are mapped to `PathPilotRuntime` models
    -   Installations are mapped to `PathPilotInstallation` models
    -   Status is calculated (missing, drifted, duplicates, unknown active)

5.  **UI Update**

    -   `ApplySnapshot()` updates runtime collection
    -   Runtime cards are created/updated
    -   Summary and statistics are refreshed
    -   Warnings are displayed if any

### Runtime Switching

1.  **User Action**

    -   User clicks "Switch" on a runtime card
    -   Installations dialog opens
    -   User selects an installation and clicks "Switch"

2.  **Machine Scope Warning**

    -   If not previously acknowledged, warning dialog appears
    -   User must confirm that changes affect all Windows accounts
    -   Warning is dismissed after acknowledgment

3.  **Switch Operation**

    -   `PathPilotInventoryService.SwitchRuntimeAsync()` is called
    -   Script parameters: `SwitchRuntimeId`, `SwitchInstallPath`
    -   PowerShell script executes switch operation

4.  **Backup Creation**

    -   Script calls `Backup-MachinePath()` function
    -   Attempts `reg.exe export` of registry key
    -   Falls back to manual `.reg` file creation if export fails
    -   **Abort on Failure**: If backup fails, switch operation is aborted

5.  **PATH Modification**

    -   Script reads current machine PATH from registry
    -   Normalizes and filters PATH entries
    -   Removes duplicate entries for the same runtime
    -   Prepends target directory to PATH
    -   Updates registry: `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment`
    -   Updates environment variable via `[System.Environment]::SetEnvironmentVariable()`

6.  **Verification**

    -   Script refreshes inventory to verify switch
    -   C# code attempts up to 3 refresh attempts with delays
    -   Verifies that switch result is reflected in new snapshot

7.  **UI Update**

    -   Snapshot is applied with switch result
    -   Active installation is updated in UI
    -   Status badges are refreshed
    -   Success message is displayed

### Export Operations

1.  **User Action**

    -   User clicks "Export JSON" or "Export Markdown"
    -   `ExportJsonAsync()` or `ExportMarkdownAsync()` is invoked

2.  **Export Generation**

    -   `PathPilotInventoryService.ExportInventoryAsync()` is called
    -   Script generates export file with format parameter
    -   Default location: `%ProgramData%\TidyWindow\PathPilot\exports\pathpilot-report-YYYYMMDD-HHMMSS.{json|md}`

3.  **File Creation**

    -   JSON: Structured data with all runtime information
    -   Markdown: Human-readable report with formatted sections
    -   File is saved with timestamp in filename

4.  **Result Notification**

    -   Success message shows file path
    -   Activity log entry created with file path

## Runtime Discovery

### Discovery Methods

PathPilot uses three discovery methods to find runtime installations:

#### 1. **Path Globs**

File pattern matching:

-   Example: `C:\Python*\python.exe`
-   Finds all files matching the pattern
-   Source: "PathGlob"

#### 2. **Directory Globs**

Directory pattern matching:

-   Example: `C:\Program Files\Nodejs\*\node.exe`
-   Finds directories matching pattern, then checks for executable
-   Source: "Directory"

#### 3. **Registry Probes**

Registry key inspection:

-   Example: `HKLM\SOFTWARE\Python\PythonCore\*\InstallPath`
-   Reads registry values to find installation paths
-   Source: "Registry"

### Runtime Configuration

Runtimes are defined in JSON configuration files:

-   **Main Config**: `data/catalog/runtime-inventory.json`
-   **Includes**: References to additional runtime definition files
-   **Runtime Definitions**: JSON objects describing each runtime

**Runtime Definition Structure**:

```json
{
    "id": "python",
    "displayName": "Python",
    "executableName": "python.exe",
    "desiredVersion": "3.11",
    "description": "Python programming language",
    "discovery": {
        "pathGlobs": ["C:\\Python*\\python.exe"],
        "directoryGlobs": ["C:\\Program Files\\Python*"],
        "registryProbes": [
            {
                "key": "HKLM\\SOFTWARE\\Python\\PythonCore",
                "valueName": "InstallPath"
            }
        ]
    }
}
```

## Safety Mechanisms in Detail

### Backup Implementation

The backup function uses two methods:

1.  **Registry Export** (Primary):

    ```powershell
    reg.exe export "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment" $backupPath /y
    ```

    -   Creates a standard Windows `.reg` file
    -   Can be imported with `reg.exe import` to restore

2.  **Manual Backup** (Fallback):
    -   Creates `.reg` file manually if export fails
    -   Includes Windows Registry Editor header
    -   Contains current PATH value

### PATH Modification Logic

The switch operation:

1.  **Reads Current PATH**: Gets machine PATH from registry
2.  **Normalizes Target**: Normalizes target directory path
3.  **Filters Entries**: Removes duplicate runtime entries
4.  **Prepends Target**: Adds target directory to front
5.  **Preserves Order**: Maintains order of other entries
6.  **Updates Registry**: Writes new PATH value
7.  **Updates Environment**: Updates environment variable

### Validation Checks

Before switching:

-   **Executable Exists**: Verifies target executable file exists
-   **Directory Exists**: Verifies target directory exists
-   **Path Normalization**: Normalizes paths for comparison
-   **Backup Success**: Aborts if backup cannot be created

### No-Change Detection

PathPilot detects when switching is unnecessary:

-   **Already First**: If target is already first on PATH, no changes made
-   **No Backup**: Backup is only created when PATH actually changes
-   **Status Message**: Clear message indicates no action was taken

## Data Models

### PathPilotInventorySnapshot

Represents the complete state of runtime inventory:

-   **Runtimes**: Array of detected runtimes
-   **MachinePath**: Current machine PATH information
-   **Warnings**: Array of warning messages
-   **GeneratedAt**: Timestamp of inventory generation

### PathPilotRuntime

Represents a single runtime:

-   **Id**: Unique runtime identifier
-   **Name**: Display name
-   **ExecutableName**: Executable filename (e.g., "python.exe")
-   **DesiredVersion**: Target version (optional)
-   **Description**: Runtime description
-   **Installations**: Array of detected installations
-   **Status**: Runtime status flags
-   **ActiveResolution**: Currently active PATH resolution
-   **ResolutionOrder**: Ordered list of PATH candidates

### PathPilotInstallation

Represents a single installation:

-   **Id**: Unique installation identifier
-   **Directory**: Installation directory
-   **ExecutablePath**: Full path to executable
-   **Version**: Detected version (optional)
-   **Architecture**: Architecture (x64, x86, ARM64, etc.)
-   **Source**: Discovery source (Registry, PathGlob, Directory)
-   **IsActive**: Whether this installation is currently active
-   **Notes**: Additional notes about the installation

### PathPilotSwitchResult

Represents the result of a switch operation:

-   **RuntimeId**: Runtime that was switched
-   **TargetDirectory**: Directory promoted to PATH
-   **TargetExecutable**: Executable path
-   **InstallationId**: Matched installation ID (if any)
-   **BackupPath**: Path to backup file created
-   **LogPath**: Path to operation log
-   **PathUpdated**: Whether PATH was actually modified
-   **Success**: Whether operation succeeded
-   **Message**: Status message
-   **PreviousPath**: PATH value before switch
-   **UpdatedPath**: PATH value after switch
-   **Timestamp**: When switch occurred

## State Management

### ViewModel Properties

-   `Runtimes`: Observable collection of runtime card view models
-   `Warnings`: Observable collection of warning messages
-   `IsBusy`: Whether any operation is in progress
-   `IsInventoryLoading`: Inventory refresh in progress
-   `IsSwitchingPath`: PATH switch operation in progress
-   `IsMachineScopeWarningOpen`: Machine scope warning dialog visibility
-   `LastRefreshedAt`: Timestamp of last inventory refresh
-   `Headline`: Dynamic headline text
-   `Summary`: Dynamic summary text
-   `InstallationsDialogRuntime`: Runtime shown in installations dialog
-   `DetailsDialogRuntime`: Runtime shown in details dialog
-   `ResolutionDialogRuntime`: Runtime shown in resolution order dialog

### Runtime Card View Model State

-   `RuntimeId`: Unique runtime identifier
-   `DisplayName`: Runtime display name
-   `FriendlyName`: User-friendly name (e.g., "Python", "Node.js")
-   `ExecutableName`: Executable filename
-   `DesiredVersion`: Target version
-   `ActiveExecutablePath`: Currently active executable path
-   `ActiveVersionLabel`: Version label for active installation
-   `PathEntry`: Directory currently on PATH
-   `StatusBadges`: Collection of status badges
-   `Installations`: Collection of installation view models
-   `ResolutionOrder`: Ordered list of PATH candidates
-   `Summary`: Summary text for the runtime
-   `IsMissing`: Whether runtime has no installations
-   `IsDrifted`: Whether active version doesn't match desired
-   `HasDuplicates`: Whether duplicate PATH entries exist
-   `HasUnknownActive`: Whether active path doesn't match known installation

### Installation View Model State

-   `RuntimeId`: Parent runtime identifier
-   `RuntimeName`: Parent runtime name
-   `InstallationId`: Unique installation identifier
-   `Directory`: Installation directory
-   `ExecutablePath`: Full executable path
-   `VersionDisplay`: Formatted version label
-   `Architecture`: Architecture badge text
-   `Source`: Discovery source
-   `IsActive`: Whether installation is currently active
-   `Notes`: Additional notes
-   `SwitchRequest`: Request object for switching to this installation

## Integration Points

### Activity Log Service

All operations log to `ActivityLogService` with:

-   Category: "PathPilot"
-   Message: Operation description
-   Details: Context information (runtime names, paths, backup locations)

### Main ViewModel

Status messages are relayed to `MainViewModel.SetStatusMessage()` for display in the main application status bar.

### Work Tracker

Operations register with `IAutomationWorkTracker`:

-   Work type: `AutomationWorkType.Maintenance`
-   Description: Operation-specific description
-   Token: GUID for tracking operation lifecycle

## Best Practices

### For Users

1.  **Review Before Switching**: Always review installations dialog before switching
2.  **Understand Machine Scope**: Remember that changes affect all Windows accounts
3.  **Keep Backups**: Backup files are saved automatically; keep them for rollback
4.  **Refresh After Changes**: Refresh inventory after manual PATH changes
5.  **Check Status Badges**: Review status badges to understand runtime state

### For Developers

1.  **Always Backup**: Never modify PATH without creating a backup first
2.  **Validate Paths**: Always verify paths exist before using them
3.  **Normalize Paths**: Use case-insensitive path normalization for comparisons
4.  **Log Operations**: Log all PATH modifications with full context
5.  **Handle Failures**: Abort operations if backup creation fails

## Technical Notes

### Script Execution

-   Script is executed via `PowerShellInvoker` which handles:
    -   PowerShell 7+ preference (falls back to Windows PowerShell)
    -   Execution policy management
    -   Output/error stream capture
    -   JSON payload extraction

### Path Resolution

Script paths are resolved relative to `AppContext.BaseDirectory` with fallback to parent directories. Script can be overridden via `TIDYWINDOW_PATHPILOT_SCRIPT` environment variable.

### Registry Operations

-   **Read Operations**: Use PowerShell registry provider (`Registry::HKLM\...`)
-   **Write Operations**: Use `Set-ItemProperty` and `[System.Environment]::SetEnvironmentVariable()`
-   **Backup Operations**: Use `reg.exe export` for standard Windows backup format

### PATH Normalization

-   Paths are normalized to uppercase for case-insensitive comparison
-   Trailing slashes are removed
-   Environment variables are expanded
-   Relative paths are resolved to absolute paths

### Export Formats

**JSON Export**:

-   Structured data with all runtime information
-   Includes timestamps, warnings, and full installation details
-   Suitable for programmatic analysis

**Markdown Export**:

-   Human-readable report format
-   Includes formatted sections for each runtime
-   Shows active installations and PATH resolution order
-   Includes backup information from switch operations

## Future Enhancements

Potential improvements:

-   User-level PATH management (in addition to machine-level)
-   PATH entry reordering (not just promotion)
-   Runtime version pinning
-   Automatic drift detection and alerts
-   PATH entry cleanup (removing invalid entries)
-   Runtime installation tracking
-   Integration with package managers for version management

