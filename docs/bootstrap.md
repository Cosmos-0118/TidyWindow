# Bootstrap Page Documentation

## Overview

The Bootstrap page is a core feature of TidyWindow that enables users to detect, install, repair, and uninstall package managers (winget, Chocolatey, and Scoop) on their Windows system. It provides a unified interface for managing the foundational tools required for package orchestration and automation workflows.

## Purpose

The Bootstrap page serves as the initial setup and verification hub for package managers. It ensures that users have the necessary tools installed and properly configured before proceeding with package installation and management tasks.

## Architecture

### Component Structure

The Bootstrap feature is built using the MVVM (Model-View-ViewModel) pattern with the following components:

```
┌─────────────────────────────────────────────────────────────┐
│                    BootstrapPage.xaml                       │
│                    (View - UI Layer)                        │
└───────────────────────┬─────────────────────────────────────┘
                        │
                        ▼
┌─────────────────────────────────────────────────────────────┐
│                 BootstrapViewModel.cs                       │
│              (ViewModel - Business Logic)                   │
└───────┬───────────────────────────────┬─────────────────────┘
        │                               │
        ▼                               ▼
┌───────────────────┐         ┌──────────────────────────┐
│ PackageManager    │         │ PackageManager           │
│ Detector          │         │ Installer                │
│ (Detection)       │         │ (Install/Repair/Remove)  │
└─────────┬─────────┘         └──────────┬───────────────┘
          │                              │
          ▼                              ▼
┌─────────────────────────────────────────────────────────┐
│         PowerShell Scripts (Automation Layer)           │
│  • bootstrap-package-managers.ps1                       │
│  • install-package-manager.ps1                          │
│  • remove-package-manager.ps1                           │
└─────────────────────────────────────────────────────────┘
```

### Key Classes

#### `BootstrapViewModel`

-   **Location**: `src/TidyWindow.App/ViewModels/BootstrapViewModel.cs`
-   **Responsibilities**:
    -   Manages UI state (busy indicators, manager collection)
    -   Coordinates detection, installation, and uninstallation operations
    -   Handles error reporting and activity logging
    -   Updates manager status after operations

#### `PackageManagerDetector`

-   **Location**: `src/TidyWindow.Core/PackageManagers/PackageManagerDetector.cs`
-   **Responsibilities**:
    -   Executes PowerShell detection script
    -   Parses JSON results from detection
    -   Merges detection results with fallback detection logic
    -   Provides detection results as `PackageManagerInfo` objects

#### `PackageManagerInstaller`

-   **Location**: `src/TidyWindow.Core/PackageManagers/PackageManagerInstaller.cs`
-   **Responsibilities**:
    -   Executes installation/repair scripts
    -   Executes uninstallation scripts
    -   Normalizes manager names (handles aliases)
    -   Returns `PowerShellInvocationResult` with operation outcomes

#### `PackageManagerEntryViewModel`

-   **Location**: `src/TidyWindow.App/ViewModels/PackageManagerEntryViewModel.cs`
-   **Responsibilities**:
    -   Represents individual package manager in UI
    -   Tracks installation status and operation history
    -   Provides computed properties for UI binding (action labels, visibility)
    -   Formats and displays status messages

## User Interface

### Layout Structure

The Bootstrap page uses a two-column layout:

**Left Column:**

-   **Detection Controls Card**: Checkboxes for including Scoop/Chocolatey and "Run detection" button
-   **Manager Inventory Card**: ListView displaying detected package managers with their status

**Right Column:**

-   **PowerShell Runtime Card**: Information about PowerShell 7+ requirement
-   **Quick Guidance Card**: Usage instructions
-   **Status Tips Card**: Visual indicators for manager states

### Visual States

Each package manager entry displays:

-   **Name**: Display name (e.g., "Windows Package Manager client", "Chocolatey CLI")
-   **Status Badge**:
    -   🟢 Green "Installed" for detected managers
    -   🟡 Yellow "Missing" for undetected managers
-   **Status Message**: Detailed information about detection results
-   **Action Buttons**:
    -   "Install" or "Repair" button (green) for missing or installed managers
    -   "Uninstall" button (red) for installed managers
-   **Operation Feedback**: Last operation message and success/failure indicators

### Loading States

Three overlay loaders provide visual feedback during operations:

-   `BootstrapInstallLoader`: Shown during install/repair operations
-   `BootstrapUninstallLoader`: Shown during uninstall operations
-   `BootstrapUpdateLoader`: Shown during detection operations

## Workflow

### Detection Workflow

1. **User Configuration**

    - User selects which optional managers to include (Scoop, Chocolatey)
    - winget is always included as it's Windows-managed

2. **Detection Execution**

    - `BootstrapViewModel.DetectAsync()` is invoked
    - `PackageManagerDetector.DetectAsync()` executes the PowerShell script
    - Script path: `automation/scripts/bootstrap-package-managers.ps1`
    - Parameters passed: `IncludeScoop`, `IncludeChocolatey`

3. **Script Processing**

    - PowerShell script checks for managers in common locations
    - Checks PATH environment variable
    - Verifies executable existence
    - Returns JSON array with detection results

4. **Result Parsing**

    - C# code parses JSON output
    - Merges with fallback detection logic (for edge cases)
    - Creates `PackageManagerInfo` objects

5. **UI Update**
    - `UpdateManagers()` synchronizes detected managers with UI collection
    - Existing entries are updated, new ones added, removed ones deleted
    - Status messages and badges reflect current state

### Installation/Repair Workflow

1. **User Action**

    - User clicks "Install" or "Repair" button for a manager
    - `BootstrapViewModel.InstallAsync()` is invoked with manager entry

2. **Validation**

    - Checks if manager allows install/repair (winget is Windows-managed)
    - Verifies manager is not currently busy

3. **Operation Execution**

    - Sets busy state and shows loader overlay
    - `PackageManagerInstaller.InstallOrRepairAsync()` executes script
    - Script path: `automation/scripts/install-package-manager.ps1`
    - Parameter: `Manager` (normalized name)

4. **Script Behavior**

    - **Scoop**: Downloads and executes official bootstrap script from `get.scoop.sh`
    - **Chocolatey**: Downloads and executes official install script from `community.chocolatey.org`
    - **winget**: Returns informational message (cannot be installed via automation)
    - Handles elevation requests for Chocolatey (requires admin)

5. **Result Processing**

    - Parses output and error streams
    - Updates manager entry with operation result
    - Logs to activity log service
    - Shows success/failure message

6. **Auto-Refresh**
    - After successful install, automatically runs detection again
    - Updates UI with new installation status

### Uninstallation Workflow

1. **User Action**

    - User clicks "Uninstall" button for an installed manager
    - `BootstrapViewModel.UninstallAsync()` is invoked

2. **Validation**

    - Verifies manager is installed
    - Checks if manager allows uninstall

3. **Operation Execution**

    - Sets busy state and shows uninstall loader
    - `PackageManagerInstaller.UninstallAsync()` executes script
    - Script path: `automation/scripts/remove-package-manager.ps1`

4. **Script Behavior**

    - **Scoop**: Runs Scoop's uninstall script or removes directories manually
    - **Chocolatey**: Uses `choco uninstall chocolatey` command
    - **winget**: Removes App Installer package via `Remove-AppxPackage`
    - Handles elevation for Chocolatey and winget (admin required)

5. **Result Processing**
    - Updates manager entry status
    - Logs operation result
    - Auto-refreshes detection if successful

## PowerShell Scripts

### bootstrap-package-managers.ps1

**Purpose**: Detects installed package managers on the system.

**Parameters**:

-   `IncludeScoop` (switch): Include Scoop in detection
-   `IncludeChocolatey` (switch): Include Chocolatey in detection

**Output**: JSON array with detection results:

```json
[
    {
        "Name": "winget",
        "DisplayName": "Windows Package Manager client",
        "Found": true,
        "Notes": "Windows Package Manager client",
        "CommandPath": "C:\\...\\winget.exe",
        "InstalledVersion": "1.5.0"
    }
]
```

**Detection Logic**:

-   **winget**: Checks `%LOCALAPPDATA%\Microsoft\WindowsApps\winget.exe` and PATH
-   **Chocolatey**: Checks `%ChocolateyInstall%\bin\choco.exe` and common install paths
-   **Scoop**: Checks `%SCOOP%\shims\scoop.exe` and user profile locations

### install-package-manager.ps1

**Purpose**: Installs or repairs a package manager.

**Parameters**:

-   `Manager` (string, required): Manager name (winget, choco, scoop)
-   `Elevated` (switch): Indicates elevated execution
-   `ResultPath` (string): Optional path for result JSON file

**Manager-Specific Behavior**:

**Scoop**:

-   If installed: Runs `scoop update` to repair
-   If missing: Downloads bootstrap script from `https://get.scoop.sh`
-   Sets execution policy to `RemoteSigned` if needed
-   Executes bootstrap script (with `-RunAsAdmin` if elevated)

**Chocolatey**:

-   If installed: Runs `choco upgrade chocolatey -y` to repair
-   If missing: Downloads install script from `https://community.chocolatey.org/install.ps1`
-   Requires administrator privileges (requests elevation if needed)
-   Sets execution policy to `Bypass` for installation

**winget**:

-   Returns informational message (cannot be installed via automation)
-   Directs users to Microsoft Store

### remove-package-manager.ps1

**Purpose**: Uninstalls a package manager.

**Parameters**: Same as install script

**Manager-Specific Behavior**:

**Scoop**:

-   Runs Scoop's uninstall script if available
-   Otherwise uses `scoop uninstall scoop`
-   Removes Scoop directories from common locations
-   Cleans up environment variables

**Chocolatey**:

-   Runs `choco uninstall chocolatey -y --remove-dependencies`
-   Removes `%ChocolateyInstall%` directory
-   Requires administrator privileges

**winget**:

-   Removes App Installer package via `Remove-AppxPackage`
-   Removes provisioned packages (if admin)
-   Note: Windows may reinstall during servicing updates

## Error Handling

### Detection Errors

-   **Script Execution Failure**: Throws `InvalidOperationException` with error details
-   **Invalid JSON**: Throws `InvalidOperationException` with parsing error
-   **File Not Found**: Throws `FileNotFoundException` if script path cannot be resolved

### Installation Errors

-   **Administrator Denial**: Detects UAC denial messages and provides user-friendly feedback
-   **Network Errors**: Scripts handle download failures gracefully
-   **Execution Policy**: Scripts attempt to set appropriate execution policy
-   **Operation Failures**: Errors are captured in `PowerShellInvocationResult.Errors`

### User Feedback

-   **Activity Log**: All operations are logged with details
-   **Status Messages**: Main status bar shows current operation status
-   **Manager Entry**: Each manager displays last operation message and success/failure indicator
-   **Error Messages**: User-friendly messages for common scenarios (admin required, network issues)

## State Management

### ViewModel Properties

-   `IsBusy`: Indicates any operation is in progress
-   `IsInstalling`: Install/repair operation active
-   `IsUninstalling`: Uninstall operation active
-   `IsUpdating`: Detection operation active
-   `IncludeScoop`: Checkbox state for Scoop inclusion
-   `IncludeChocolatey`: Checkbox state for Chocolatey inclusion
-   `Managers`: Observable collection of `PackageManagerEntryViewModel` objects

### Manager Entry State

-   `IsInstalled`: Current installation status
-   `IsBusy`: Manager-specific operation in progress
-   `LastOperationMessage`: Result message from last operation
-   `LastOperationSucceeded`: Boolean? indicating success/failure/null

## Integration Points

### Activity Log Service

All operations log to `ActivityLogService` with:

-   Category: "Bootstrap"
-   Message: Operation description
-   Details: Context information (manager name, parameters, output, errors)

### Work Tracker

Operations register with `IAutomationWorkTracker`:

-   Work type: `AutomationWorkType.Install`
-   Description: Operation-specific description
-   Token: GUID for tracking operation lifecycle

### Main ViewModel

Status messages are relayed to `MainViewModel.SetStatusMessage()` for display in the main application status bar.

## Best Practices

### For Users

1. **PowerShell 7+**: Ensure PowerShell 7 or newer is installed before using Bootstrap
2. **Administrator Rights**: Be prepared to accept UAC prompts for Chocolatey operations
3. **Detection First**: Always run detection before attempting install/repair
4. **Review Status**: Check status messages and activity log for operation details

### For Developers

1. **Error Handling**: Always check `PowerShellInvocationResult.IsSuccess` before processing results
2. **State Management**: Update UI state in `finally` blocks to ensure cleanup
3. **Logging**: Include context details in activity log entries
4. **User Feedback**: Provide clear, actionable error messages
5. **Auto-Refresh**: Refresh detection after successful install/uninstall operations

## Technical Notes

### PowerShell Execution

-   Scripts are executed via `PowerShellInvoker` which handles:
    -   PowerShell 7+ preference (falls back to Windows PowerShell)
    -   Execution policy management
    -   Output/error stream capture
    -   Exit code tracking

### Path Resolution

Script paths are resolved relative to `AppContext.BaseDirectory` with fallback to parent directories, ensuring scripts are found in both development and deployment scenarios.

### Manager Name Normalization

The system handles various name formats:

-   "choco" ↔ "chocolatey" ↔ "Chocolatey CLI"
-   "scoop" ↔ "Scoop package manager"
-   "winget" ↔ "Windows Package Manager client"

### Windows-Managed Managers

winget is treated specially:

-   Cannot be installed via automation
-   Cannot be uninstalled via automation (though removal script exists)
-   UI shows informational message directing users to Windows Settings/Store
-   **Missing winget?** Install or repair the Microsoft _App Installer_ package. The quickest path is opening `ms-windows-store://pdp/?productid=9NBLGGH4NNS1` (Store listing) and pressing **Get**. Offline or disconnected hosts can download the latest `Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle` plus its dependency packages from https://aka.ms/getwinget and install them with `Add-AppxPackage -Path .\<package>.msixbundle` from an elevated PowerShell prompt.

## Future Enhancements

Potential improvements:

-   Support for additional package managers
-   Batch operations (install multiple managers)
-   Health checks and diagnostics
-   Automatic repair suggestions
-   Installation history tracking
