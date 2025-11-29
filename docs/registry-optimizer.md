# Registry Optimizer Page Documentation

## Overview

The Registry Optimizer page provides a safe and controlled interface for applying Windows registry tweaks. It allows users to enable or disable system optimizations, customize registry values, and manage presets of related tweaks. All operations are protected by automatic restore points and rollback capabilities.

## Purpose

The Registry Optimizer serves as a centralized control panel for:

-   **Registry Tweaks**: Apply curated Windows registry optimizations safely
-   **Custom Values**: Configure numeric and string registry values with validation
-   **Preset Management**: Apply predefined collections of related tweaks
-   **State Tracking**: Monitor current registry values and detect changes
-   **Safe Rollback**: Automatic restore points with rollback capability

## Safety Features

### Why Registry Optimizer Operations Are Safe

The Registry Optimizer implements multiple safety mechanisms to protect system stability:

#### 1. **Automatic Restore Point Creation**

Before applying any registry changes, the system automatically creates a restore point:

-   **Automatic Creation**: Restore point is created immediately after successful application
-   **Complete State Capture**: Restore point includes all previous states and revert operations
-   **Persistent Storage**: Restore points saved to `%ProgramData%\TidyWindow\RegistryBackups\`
-   **JSON Format**: Human-readable JSON format for easy inspection
-   **Unique Identification**: Each restore point has a unique GUID and timestamp
-   **Revert Operations**: Restore point contains exact operations needed to revert changes

This ensures that every registry change can be undone, even if the application is closed.

#### 2. **Automatic Rollback Dialog with Countdown**

After creating a restore point, a rollback dialog appears automatically:

-   **30-Second Countdown**: Default 30-second countdown timer (minimum 5 seconds)
-   **Auto-Revert**: Automatically reverts changes if user doesn't respond
-   **User Choice**: User can choose to "Keep changes" or "Revert now"
-   **Visual Countdown**: Clear countdown display showing remaining seconds
-   **Topmost Window**: Dialog appears on top to ensure visibility
-   **Non-Blocking**: User can continue working while countdown runs

This provides a safety net that automatically protects users from unintended changes.

#### 3. **Restore Point Management**

The system manages restore points intelligently:

-   **Maximum Limit**: Keeps up to 10 most recent restore points
-   **Automatic Pruning**: Older restore points are automatically deleted
-   **Latest Tracking**: Always tracks the most recent restore point
-   **Manual Restore**: Users can manually restore the latest snapshot at any time
-   **Persistent Storage**: Restore points survive application restarts

This ensures users always have recent restore points available without disk space concerns.

#### 4. **Custom Value Validation**

All custom registry values are validated before application:

-   **Type Validation**: Validates numeric values for range constraints
-   **Range Checking**: Enforces minimum and maximum value constraints
-   **Format Validation**: Ensures values are in correct format
-   **Real-Time Feedback**: Validation errors shown immediately
-   **Prevents Invalid Operations**: Apply button disabled if validation errors exist
-   **Clear Error Messages**: User-friendly error messages explain validation failures

This prevents invalid registry values from being applied.

#### 5. **Baseline State Tracking**

The system tracks baseline states for all tweaks:

-   **Baseline Recording**: Records current state when changes are applied
-   **Change Detection**: Detects when current state differs from baseline
-   **Revert Capability**: Can revert to baseline state without restore point
-   **State Persistence**: Baseline states persist across application restarts
-   **Custom Value Baselines**: Tracks baseline custom values separately

This allows quick reversion without needing restore points.

#### 6. **Sequential Operation Execution**

Registry operations are executed sequentially:

-   **One at a Time**: Operations execute one after another
-   **Error Isolation**: Failures in one operation don't affect others
-   **Complete Logging**: Full output and errors captured for each operation
-   **Cancellation Support**: Operations can be cancelled if needed
-   **Result Tracking**: Success/failure tracked for each operation

This ensures predictable execution and prevents registry conflicts.

#### 7. **Comprehensive Error Handling**

All operations include robust error handling:

-   **Try-Catch Blocks**: All script executions wrapped in error handling
-   **Error Aggregation**: Errors from all operations collected and reported
-   **Graceful Degradation**: Partial failures don't crash the application
-   **Error Logging**: All errors logged to Activity Log
-   **User Feedback**: Clear error messages displayed to users

#### 8. **No-Change Detection**

The system detects when no changes are needed:

-   **State Comparison**: Compares current state with target state
-   **Skip Unnecessary Operations**: Skips operations when already in desired state
-   **Efficiency**: Prevents unnecessary registry writes
-   **Clear Messaging**: Informs users when no changes are needed

#### 9. **Preset Customization Tracking**

The system tracks when presets are customized:

-   **Customization Detection**: Detects when user modifies a preset
-   **Visual Indicators**: Shows when preset differs from original
-   **Revert to Preset**: Can revert to original preset state
-   **State Preservation**: Preserves customizations across sessions

## Architecture

### Component Structure

```
┌─────────────────────────────────────────────────────────────┐
│            RegistryOptimizerPage.xaml                       │
│                    (View - Container)                       │
└───────────────────────┬─────────────────────────────────────┘
                        │
                        ▼
┌─────────────────────────────────────────────────────────────┐
│         RegistryOptimizerViewModel.cs                       │
│              (ViewModel - Business Logic)                   │
└───────┬───────────────────────────────┬─────────────────────┘
        │                               │
        ▼                               ▼
┌───────────────────┐         ┌──────────────────────────┐
│ RegistryOptimizer │         │ RegistryPreference       │
│ Service           │         │ Service                  │
│ (Operations)      │         │ (State Persistence)      │
└─────────┬─────────┘         └──────────────────────────┘
          │
          ▼
┌─────────────────────────────────────────────────────────┐
│         PowerShell Scripts                              │
│  • automation/registry/*.ps1                            │
│  • Registry modification operations                      │
│  • Revert operations                                    │
└─────────────────────────────────────────────────────────┘
```

### Key Classes

#### `RegistryOptimizerViewModel`

-   **Location**: `src/TidyWindow.App/ViewModels/RegistryOptimizerViewModel.cs`
-   **Responsibilities**:
    -   Manages tweak and preset collections
    -   Coordinates registry operations
    -   Handles restore point creation and restoration
    -   Manages rollback dialog display
    -   Tracks pending changes and validation state
    -   Persists user preferences

#### `RegistryOptimizerService`

-   **Location**: `src/TidyWindow.Core/Maintenance/RegistryOptimizerService.cs`
-   **Responsibilities**:
    -   Loads tweak and preset definitions from configuration
    -   Builds operation plans from selections
    -   Executes registry operations sequentially
    -   Creates and manages restore points
    -   Applies restore points to revert changes
    -   Prunes old restore points

#### `RegistryTweakCardViewModel`

-   **Location**: `src/TidyWindow.App/ViewModels/RegistryOptimizerViewModel.cs`
-   **Responsibilities**:
    -   Represents a single registry tweak in the UI
    -   Manages selection state and custom values
    -   Validates custom values
    -   Tracks baseline state
    -   Displays current registry state
    -   Manages snapshot entries

#### `RegistryPresetViewModel`

-   **Location**: `src/TidyWindow.App/ViewModels/RegistryOptimizerViewModel.cs`
-   **Responsibilities**:
    -   Represents a preset collection of tweaks
    -   Applies preset states to tweaks
    -   Tracks preset customization

## User Interface

### Layout Structure

**Header Section**:

-   **Title**: "Registry optimizer"
-   **Headline**: Dynamic headline text
-   **Icon**: 🧩 emoji

**Two-Column Layout**:

-   **Primary Column**: Tweak list and details
-   **Secondary Column**: Presets and action buttons

### Primary Column

**Lead Card**:

-   **Title**: "Stage registry defaults safely"
-   **Description**: Explains the purpose and safety features
-   **Safety Notice**: Mentions automatic restore points

**Tweaks Section**:

-   **Header**: "Tweaks" with description
-   **Tweak Cards**: List of available registry tweaks

**Tweak Card Features**:

-   **Icon**: Category-specific icon (🧰 default)
-   **Title**: Tweak name
-   **Summary**: Brief description
-   **Risk Level**: Risk indicator (Safe, Low, Medium, High)
-   **Category**: Tweak category
-   **Toggle Switch**: Enable/disable toggle
-   **Current Value**: Shows current registry value (if detectable)
-   **Recommended Value**: Shows recommended value
-   **Custom Value Input**: Text box for custom values (if supported)
-   **Validation Error**: Error message if custom value is invalid
-   **Details Button**: Expands to show detailed information
-   **Snapshot Entries**: Shows registry paths and values observed

**Tweak Details Panel** (Expandable):

-   **Registry Paths**: All registry paths affected
-   **Current Values**: Current values at each path
-   **Recommended Values**: Recommended values
-   **Documentation Link**: Link to detailed documentation (if available)
-   **Constraints**: Min/max values for custom inputs

### Secondary Column

**Action Buttons**:

-   **Apply**: Applies pending changes (disabled if validation errors)
-   **Revert**: Reverts to baseline state
-   **Restore**: Restores from latest restore point

**Status Indicators**:

-   **Pending Notice**: Shows when changes are pending
-   **Synced Notice**: Shows when registry is in sync
-   **Restore Point Info**: Shows latest restore point timestamp

**Presets Section**:

-   **Header**: "Presets" with description
-   **Preset List**: List of available presets
-   **Preset Cards**: Show preset name, description, and icon

**Preset Card Features**:

-   **Icon**: Preset-specific icon
-   **Name**: Preset name
-   **Description**: Preset description
-   **Default Indicator**: Shows if preset is default

### Rollback Dialog

**Layout**:

-   **Icon**: 🛡️ shield emoji
-   **Header**: "Registry restore point created"
-   **Description**: Explains that changes can be reverted
-   **Countdown**: Shows remaining seconds (30s default)
-   **Action Buttons**: "Keep changes" and "Revert now"

**Behavior**:

-   **Auto-Revert**: Automatically reverts if countdown reaches zero
-   **User Choice**: User can keep changes or revert immediately
-   **Topmost**: Dialog appears on top of all windows

## Workflow

### Applying Registry Tweaks

1.  **User Selection**

    -   User toggles tweaks on/off or enters custom values
    -   Pending changes are tracked
    -   Validation errors are shown immediately

2.  **Validation**

    -   Custom values are validated for type and range
    -   Apply button is disabled if validation errors exist
    -   Error messages guide user to fix issues

3.  **Plan Building**

    -   `RegistryOptimizerService.BuildPlan()` creates operation plan
    -   Plan includes apply operations and revert operations
    -   Operations are skipped if already in desired state

4.  **Operation Execution**

    -   `RegistryOptimizerService.ApplyAsync()` executes plan
    -   Operations execute sequentially via PowerShell scripts
    -   Output and errors are captured for each operation

5.  **Restore Point Creation**

    -   `SaveRestorePointAsync()` creates restore point automatically
    -   Restore point includes:
        -   Previous and target states for each tweak
        -   Revert operations to undo changes
        -   Timestamp and unique ID
    -   Restore point saved to `%ProgramData%\TidyWindow\RegistryBackups\`

6.  **Rollback Dialog**

    -   Dialog appears automatically after restore point creation
    -   30-second countdown begins
    -   User can:
        -   Click "Keep changes" to dismiss dialog
        -   Click "Revert now" to immediately revert
        -   Wait for countdown to auto-revert

7.  **State Update**

    -   Baseline states are updated to reflect applied changes
    -   Pending changes are cleared
    -   UI reflects new registry state

### Restoring from Restore Point

1.  **User Action**

    -   User clicks "Restore" button
    -   `RestoreLastSnapshotAsync()` is invoked

2.  **Restore Point Application**

    -   `RegistryOptimizerService.ApplyRestorePointAsync()` executes
    -   Revert operations from restore point are executed
    -   Registry is restored to previous state

3.  **State Synchronization**

    -   Tweak states are updated to match restored registry
    -   Baseline states are updated
    -   Pending changes are cleared

4.  **Restore Point Cleanup**

    -   Restore point is deleted after successful restoration
    -   Latest restore point is updated

### Reverting to Baseline

1.  **User Action**

    -   User clicks "Revert" button
    -   `RevertChangesCommand` is invoked

2.  **State Reversion**

    -   Each tweak reverts to its baseline state
    -   Custom values revert to baseline values
    -   No registry operations are executed

3.  **UI Update**

    -   Pending changes are cleared
    -   UI reflects baseline state

### Applying Presets

1.  **User Selection**

    -   User selects a preset from the list
    -   `SelectedPreset` property is updated

2.  **Preset Application**

    -   `ApplyPreset()` applies preset states to tweaks
    -   Each tweak's selection state is set according to preset
    -   Pending changes are updated

3.  **Customization Detection**

    -   System detects if user modifies preset
    -   `IsPresetCustomized` flag is set
    -   Visual indicators show customization

## Safety Mechanisms in Detail

### Restore Point Format

Restore points are stored as JSON files with the following structure:

```json
{
    "id": "guid",
    "createdUtc": "timestamp",
    "selections": [
        {
            "tweakId": "tweak-id",
            "previousState": false,
            "targetState": true
        }
    ],
    "operations": [
        {
            "tweakId": "tweak-id",
            "name": "Tweak Name",
            "targetState": false,
            "scriptPath": "path/to/revert.ps1",
            "parameters": {
                "param1": "value1"
            }
        }
    ]
}
```

### Rollback Dialog Countdown

The rollback dialog implements a safety countdown:

-   **Default Duration**: 30 seconds
-   **Minimum Duration**: 5 seconds (enforced)
-   **Countdown Display**: Updates every second
-   **Auto-Revert**: Triggers revert when countdown reaches zero
-   **User Override**: User can revert or keep at any time

### Restore Point Pruning

The system automatically manages restore point storage:

-   **Maximum Count**: 10 restore points
-   **Pruning Trigger**: After each restore point creation
-   **Sorting**: By last write time (newest first)
-   **Deletion**: Oldest restore points deleted first
-   **Error Handling**: Pruning failures are ignored (best-effort)

### Custom Value Validation

Custom values are validated according to tweak constraints:

**Numeric Values**:

-   Must be valid numbers (double precision)
-   Must be within min/max range (if specified)
-   Supports decimal values

**String Values**:

-   Must not be empty (if required)
-   Format validation (if specified)

**Validation Feedback**:

-   Real-time validation on input change
-   Clear error messages
-   Apply button disabled on validation errors

### Operation Plan Building

The plan building process ensures safety:

-   **State Comparison**: Only creates operations for state changes
-   **Revert Operations**: Always includes revert operations
-   **Parameter Merging**: Merges base parameters with custom overrides
-   **Script Resolution**: Resolves script paths relative to application

### Sequential Execution

Operations execute one at a time:

-   **Order Preservation**: Operations execute in plan order
-   **Error Isolation**: One failure doesn't stop subsequent operations
-   **Cancellation Support**: Operations can be cancelled
-   **Result Tracking**: Each operation's result is tracked

## Data Models

### RegistryTweakDefinition

Represents a registry tweak definition:

-   **Id**: Unique tweak identifier
-   **Name**: Tweak display name
-   **Category**: Tweak category
-   **Summary**: Brief description
-   **RiskLevel**: Risk level (Safe, Low, Medium, High)
-   **Icon**: Icon emoji or identifier
-   **DefaultState**: Default enabled/disabled state
-   **DocumentationLink**: Link to documentation
-   **Constraints**: Custom value constraints (type, min, max, default)
-   **Detection**: Registry value detection configuration
-   **EnableOperation**: Operation to enable tweak
-   **DisableOperation**: Operation to disable tweak

### RegistryPresetDefinition

Represents a preset collection:

-   **Id**: Unique preset identifier
-   **Name**: Preset display name
-   **Description**: Preset description
-   **Icon**: Preset icon
-   **IsDefault**: Whether preset is default
-   **States**: Dictionary of tweak ID to enabled state

### RegistryRestorePoint

Represents a restore point:

-   **Id**: Unique restore point identifier (GUID)
-   **FilePath**: Path to restore point JSON file
-   **CreatedUtc**: When restore point was created
-   **Selections**: Array of selection states
-   **Operations**: Array of revert operations

### RegistryOperationPlan

Represents a plan of operations:

-   **ApplyOperations**: Operations to apply changes
-   **RevertOperations**: Operations to revert changes
-   **HasWork**: Whether plan has any operations

### RegistryOperationResult

Represents the result of executing operations:

-   **Executions**: Array of execution summaries
-   **IsSuccess**: Whether all operations succeeded
-   **SucceededCount**: Count of successful operations
-   **FailedCount**: Count of failed operations
-   **AggregateErrors**: Combined error messages

## State Management

### ViewModel Properties

-   `Tweaks`: Observable collection of tweak view models
-   `Presets`: Observable collection of preset view models
-   `SelectedPreset`: Currently selected preset
-   `IsBusy`: Whether operation is in progress
-   `HasPendingChanges`: Whether there are unapplied changes
-   `HasValidationErrors`: Whether there are validation errors
-   `IsPresetCustomized`: Whether current preset is customized
-   `Headline`: Dynamic headline text
-   `LastOperationSummary`: Summary of last operation
-   `LatestRestorePoint`: Latest restore point (if any)
-   `HasRestorePoint`: Whether restore point exists
-   `LatestRestorePointLocalTime`: Local time of latest restore point

### Tweak State Properties

-   `IsSelected`: Whether tweak is enabled
-   `IsBaselineEnabled`: Baseline enabled state
-   `HasPendingChanges`: Whether tweak has unapplied changes
-   `CustomValue`: Custom value input (if supported)
-   `CustomValueIsValid`: Whether custom value is valid
-   `CustomValueError`: Custom value validation error
-   `SupportsCustomValue`: Whether tweak supports custom values
-   `RecommendedValue`: Recommended registry value
-   `IsStatePending`: Whether state is being loaded
-   `StateError`: Error loading current state
-   `SnapshotEntries`: Registry paths and values observed

### State Persistence

-   **User Preferences**: Tweak states and custom values persisted via `RegistryPreferenceService`
-   **Restore Points**: Restore points persisted to disk in JSON format
-   **Baseline States**: Baseline states tracked in memory and persisted

## Integration Points

### Activity Log Service

All operations log to `ActivityLogService` with:

-   Category: "Registry"
-   Message: Operation description
-   Details: Context information (tweak names, restore point paths, etc.)

### Main ViewModel

Status messages are relayed to `MainViewModel.SetStatusMessage()` for display in the main application status bar.

### Registry Preference Service

User preferences are managed by `RegistryPreferenceService`:

-   Tweak selection states
-   Custom values
-   Selected preset ID
-   Persisted across application restarts

## Best Practices

### For Users

1.  **Review Before Applying**: Review all tweaks before clicking Apply
2.  **Respond to Rollback Dialog**: Don't ignore the rollback dialog; choose to keep or revert
3.  **Test Incrementally**: Apply tweaks in small batches to test effects
4.  **Use Presets**: Start with presets for common configurations
5.  **Check Restore Points**: Verify restore points are being created
6.  **Validate Custom Values**: Ensure custom values are valid before applying

### For Developers

1.  **Always Create Restore Points**: Never apply registry changes without restore points
2.  **Validate Inputs**: Always validate custom values before use
3.  **Handle Errors Gracefully**: Wrap all registry operations in error handling
4.  **Log Operations**: Log all registry operations with full context
5.  **Test Revert Operations**: Ensure revert operations work correctly
6.  **Document Tweaks**: Provide clear documentation for each tweak

## Technical Notes

### Script Execution

-   Scripts are executed via `PowerShellInvoker` which handles:
    -   PowerShell 7+ preference (falls back to Windows PowerShell)
    -   Execution policy management
    -   Output/error stream capture
    -   Result path specification

### Configuration Loading

-   **Configuration Path**: `data/cleanup/registry-defaults.json`
-   **Lazy Loading**: Configuration loaded on first access
-   **Thread Safety**: Configuration loading is thread-safe
-   **Error Handling**: Configuration errors throw exceptions

### Restore Point Storage

-   **Location**: `%ProgramData%\TidyWindow\RegistryBackups\`
-   **Format**: JSON files with timestamp and GUID in filename
-   **Naming**: `YYYYMMDD-HHmmssfff-{guid}.json`
-   **Pruning**: Automatic pruning keeps maximum 10 restore points

### Custom Value Parsing

-   **Numeric Values**: Parsed as double precision
-   **Culture Support**: Supports both current culture and invariant culture
-   **Range Validation**: Min/max constraints enforced
-   **Type Conversion**: Converts to appropriate script parameter type

### Operation Plan Building

-   **State Resolution**: Resolves enable/disable operations based on target state
-   **Parameter Merging**: Merges base parameters with custom overrides
-   **Script Path Resolution**: Resolves script paths relative to application directory
-   **Revert Operations**: Always includes revert operations for safety

## Future Enhancements

Potential improvements:

-   Restore point export/import
-   Restore point comparison
-   Batch restore point management
-   Tweak dependency tracking
-   Custom preset creation
-   Tweak scheduling
-   Registry value monitoring
-   Advanced validation rules
-   Tweak templates
-   Integration with Windows System Restore
