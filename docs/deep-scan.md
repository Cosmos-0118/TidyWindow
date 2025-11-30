# Deep Scan Page Documentation

## Overview

The Deep Scan page provides a powerful file system analyzer that identifies the largest files and folders on your system. It helps users discover space-consuming items that may be candidates for cleanup. The scan operation is read-only and completely safe, while deletion operations require explicit user confirmation with clear warnings.

## Purpose

The Deep Scan page serves as a diagnostic tool for:

-   **Large File Discovery**: Find files and folders exceeding size thresholds
-   **Space Analysis**: Identify what's consuming disk space
-   **Targeted Cleanup**: Focus cleanup efforts on the largest items
-   **Category Classification**: Automatically categorize findings by type
-   **Filtered Scanning**: Scan specific locations with customizable filters

## Safety Features

### Why Deep Scan Operations Are Safe

The Deep Scan page implements multiple safety mechanisms to protect user data:

#### 1. **Read-Only Scanning**

The scan operation itself is completely read-only:

-   **No Modifications**: Scanning never modifies, moves, or deletes files
-   **File System Enumeration Only**: Only reads file metadata (size, path, attributes)
-   **No Registry Changes**: Does not modify system settings
-   **No Network Activity**: Operates entirely on local file system
-   **Safe to Cancel**: Can be cancelled at any time without side effects
-   **No Side Effects**: Scanning has zero impact on system state

This ensures that running a scan is completely safe and cannot damage your system or data.

#### 2. **Explicit Deletion Confirmation**

All deletions require explicit user confirmation:

-   **Warning Dialog**: Every deletion shows a clear warning dialog
-   **Permanent Deletion Notice**: Dialog explicitly states deletion is permanent
-   **User Responsibility**: Dialog makes clear that deletion is user's responsibility
-   **Default to "No"**: Dialog defaults to "No" to prevent accidental deletion
-   **Item-Specific Warning**: Warning includes the specific file/folder name
-   **No Batch Deletion**: Each item must be confirmed individually

This prevents accidental deletions and ensures users understand the consequences.

#### 3. **Permanent Deletion Warning**

The deletion confirmation dialog clearly states:

-   **Permanent Nature**: "Deleting this file/folder is permanent"
-   **No Recycle Bin**: Files are not moved to Recycle Bin
-   **User Responsibility**: "Your responsibility" message
-   **Cannot Undo**: Makes clear that deletion cannot be undone
-   **Visual Warning**: Uses warning icon to draw attention

This ensures users understand that deletions are permanent and irreversible.

#### 4. **Read-Only Attribute Handling**

The system safely handles read-only files:

-   **Automatic Clearing**: Automatically clears read-only flags before deletion
-   **Recursive Clearing**: For directories, clears read-only flags recursively
-   **Error Handling**: Gracefully handles permission errors
-   **No Force Deletion**: Does not force deletion of protected system files
-   **Safe Attribute Modification**: Only modifies read-only attributes, not other attributes

This allows deletion of read-only items while respecting system protections.

#### 5. **Existence Verification**

Before deletion, the system verifies file existence:

-   **Pre-Deletion Check**: Verifies file/directory exists before attempting deletion
-   **Missing Item Handling**: Gracefully handles items that no longer exist
-   **Result Update**: Automatically removes missing items from results
-   **No Error on Missing**: Missing items don't cause errors

This prevents errors when items are deleted externally or no longer exist.

#### 6. **Comprehensive Error Handling**

All operations include robust error handling:

-   **Exception Catching**: Catches all file system exceptions
-   **User-Friendly Messages**: Converts technical errors to user-friendly messages
-   **Graceful Degradation**: Partial failures don't crash the application
-   **Error Reporting**: Clear error messages displayed to users
-   **Permission Errors**: Handles permission denied errors gracefully

#### 7. **No Automatic Actions**

The system never performs automatic deletions:

-   **User-Initiated Only**: Deletions only occur when user explicitly clicks delete
-   **No Background Deletion**: No automatic cleanup in background
-   **No Scheduled Deletion**: No scheduled or delayed deletions
-   **Manual Confirmation Required**: Every deletion requires manual confirmation
-   **No Bulk Operations**: No automatic bulk deletion of similar items

#### 8. **Safe File System Enumeration**

The scanning process uses safe enumeration:

-   **Exception Handling**: Catches and ignores non-critical file system exceptions
-   **Access Denied Handling**: Gracefully skips inaccessible files/directories
-   **Path Too Long Handling**: Handles paths exceeding Windows limits
-   **Reparse Point Skipping**: Automatically skips symbolic links and junctions
-   **System File Protection**: Respects system file protections

This ensures scanning doesn't crash on problematic file system entries.

#### 9. **Cancellation Support**

Scans can be cancelled safely:

-   **Cancellation Tokens**: Uses cancellation tokens throughout scan process
-   **Immediate Stop**: Cancellation stops scan immediately
-   **No Partial State**: No partial state left after cancellation
-   **Safe to Restart**: Can restart scan after cancellation without issues

## Architecture

### Component Structure

```
┌─────────────────────────────────────────────────────────────┐
│            DeepScanPage.xaml                                │
│                    (View - Container)                       │
└───────────────────────┬─────────────────────────────────────┘
                        │
                        ▼
┌─────────────────────────────────────────────────────────────┐
│         DeepScanViewModel.cs                                │
│              (ViewModel - Business Logic)                   │
└───────┬───────────────────────────────┬─────────────────────┘
        │                               │
        ▼                               ▼
┌───────────────────┐         ┌──────────────────────────┐
│ DeepScanService   │         │ MainViewModel            │
│ (Scan Execution)  │         │ (Status Messages)        │
└─────────┬─────────┘         └──────────────────────────┘
          │
          ▼
┌─────────────────────────────────────────────────────────┐
│         File System Enumeration                         │
│  • Directory traversal                                  │
│  • File size calculation                                │
│  • Category classification                              │
│  • Progress reporting                                   │
└─────────────────────────────────────────────────────────┘
```

### Key Classes

#### `DeepScanViewModel`

-   **Location**: `src/TidyWindow.App/ViewModels/DeepScanViewModel.cs`
-   **Responsibilities**:
    -   Manages scan configuration (path, filters, thresholds)
    -   Coordinates scan execution
    -   Handles deletion operations with confirmation
    -   Manages pagination of results
    -   Tracks scan progress and results
    -   Provides preset location options

#### `DeepScanService`

-   **Location**: `src/TidyWindow.Core/Diagnostics/DeepScanService.cs`
-   **Responsibilities**:
    -   Executes file system scans
    -   Enumerates directories and files
    -   Calculates file and directory sizes
    -   Applies filters and thresholds
    -   Reports progress during scanning
    -   Classifies findings by category

#### `DeepScanItemViewModel`

-   **Location**: `src/TidyWindow.App/ViewModels/DeepScanViewModel.cs`
-   **Responsibilities**:
    -   Represents a single finding in the UI
    -   Formats size and date displays
    -   Provides file/directory metadata

## User Interface

### Layout Structure

**Header Section**:

-   **Title**: "Deep scan analyzer"
-   **Subtitle**: "Zero in on the heaviest files and reclaim space fast."
-   **Icon**: 🔍 emoji

**Summary Card**:

-   **Summary Text**: Shows scan results summary or "Run a scan to surface large files and folders."
-   **Last Scanned**: Displays timestamp of last scan
-   **Run Scan Button**: Primary action button to start scan

**Filters Section**:

-   **Target Path**: Text input and browse button for scan location
-   **Preset Locations**: Dropdown with common locations (User profile, Downloads, Desktop, etc.)
-   **Minimum Size**: Numeric input for minimum file size in MB
-   **Max Items**: Numeric input for maximum number of results
-   **Include Hidden**: Checkbox to include hidden files
-   **Include Directories**: Checkbox to include directories in results
-   **Name Filter**: Text input for filtering by filename
-   **Match Mode**: Dropdown (Contains, StartsWith, EndsWith, Exact)
-   **Case Sensitive**: Checkbox for case-sensitive matching

**Results Section**:

-   **Results List**: Paginated list of findings
-   **Pagination Controls**: Previous/Next page buttons
-   **Page Display**: "Page X of Y" indicator

**Finding Card Features**:

-   **Name**: File or folder name
-   **Path**: Full path to item
-   **Size**: Formatted size (B, KB, MB, GB, TB)
-   **Modified**: Last modified date
-   **Category**: Category badge (Games, Videos, Documents, etc.)
-   **Kind**: "File" or "Folder" indicator
-   **Open Folder Button**: Opens containing folder in Explorer
-   **Delete Button**: Deletes the item (with confirmation)

**Scan Overlay**:

-   **Progress Display**: Shows scan progress during execution
-   **Animated Indicators**: Visual feedback during scanning
-   **Current Path**: Shows currently scanned path
-   **Processed Count**: Number of items processed
-   **Processed Size**: Total size processed

## Workflow

### Running a Scan

1.  **Configuration**

    -   User sets target path (or selects preset)
    -   User configures filters (minimum size, max items, name filters)
    -   User sets scan options (include hidden, include directories)

2.  **Scan Initiation**

    -   User clicks "Run scan" button
    -   `RefreshCommand` is invoked
    -   `DeepScanService.RunScanAsync()` is called

3.  **Scan Execution**

    -   Service resolves root path
    -   Enumerates file system starting from root
    -   Calculates sizes for files and directories
    -   Applies filters (size threshold, name filters)
    -   Maintains priority queue of largest items
    -   Reports progress via `IProgress<DeepScanProgressUpdate>`

4.  **Progress Updates**

    -   Progress updates emitted at intervals (600ms default)
    -   Candidate updates emitted more frequently (220ms)
    -   Updates include:
        -   Current findings (top candidates)
        -   Processed entry count
        -   Processed size
        -   Current path being scanned
        -   Category totals

5.  **Result Display**

    -   Findings sorted by size (largest first)
    -   Results paginated (100 items per page)
    -   Summary updated with total count and size
    -   Category totals displayed

### Deleting an Item

1.  **User Action**

    -   User clicks delete button on a finding
    -   `DeleteButton_OnClick` handler invoked

2.  **Confirmation Dialog**

    -   Warning dialog displayed:
        -   Message: "We cannot tell whether '{name}' is important. Deleting this file/folder is permanent and your responsibility."
        -   Title: "Confirm permanent deletion"
        -   Buttons: Yes/No
        -   Default: No
        -   Icon: Warning

3.  **User Decision**

    -   If user clicks "No": Operation cancelled, no deletion
    -   If user clicks "Yes": Proceed to deletion

4.  **Deletion Execution**

    -   `DeleteFindingAsync()` invoked
    -   Existence verified
    -   Read-only flags cleared (if needed)
    -   File or directory deleted
    -   Result removed from findings list

5.  **Result Update**

    -   Finding removed from results
    -   Summary updated
    -   Status message displayed

### Opening Containing Folder

1.  **User Action**

    -   User clicks "Open folder" button
    -   `OpenContainingFolderCommand` invoked

2.  **Explorer Launch**

    -   Windows Explorer launched with `/select` argument
    -   File/folder selected in Explorer
    -   Error handling for launch failures

## Safety Mechanisms in Detail

### Deletion Confirmation Dialog

The deletion confirmation dialog provides multiple safety layers:

**Dialog Content**:

```
Title: "Confirm permanent deletion"
Message: "We cannot tell whether '{item.Name}' is important.
          Deleting this {file/folder} is permanent and your responsibility.

          Do you want to continue?"
Buttons: Yes / No
Default: No
Icon: Warning
```

**Safety Features**:

-   **Explicit Warning**: Clear statement about permanence
-   **User Responsibility**: Makes user responsible for decision
-   **Default to No**: Prevents accidental confirmation
-   **Item-Specific**: Shows exact item name
-   **Visual Warning**: Warning icon draws attention

### Read-Only Flag Handling

The system safely handles read-only files and directories:

**File Handling**:

```csharp
// Check if file is read-only
var attributes = File.GetAttributes(filePath);
if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
{
    // Clear read-only flag
    File.SetAttributes(filePath, attributes & ~FileAttributes.ReadOnly);
}
// Then delete
File.Delete(filePath);
```

**Directory Handling**:

-   Recursively clears read-only flags on all files and subdirectories
-   Uses stack-based traversal for efficiency
-   Handles errors gracefully (continues on individual failures)

### Error Handling

All operations include comprehensive error handling:

**Exception Types Handled**:

-   `IOException`: General I/O errors
-   `UnauthorizedAccessException`: Permission denied
-   `DirectoryNotFoundException`: Directory doesn't exist
-   `FileNotFoundException`: File doesn't exist
-   `NotSupportedException`: Unsupported operations
-   `SecurityException`: Security violations

**Error Reporting**:

-   Errors converted to user-friendly messages
-   Status messages displayed in main status bar
-   No technical stack traces shown to users
-   Graceful degradation on errors

### File System Enumeration Safety

The scanning process uses safe enumeration:

**Non-Critical Exception Handling**:

-   `UnauthorizedAccessException`: Skip inaccessible items
-   `PathTooLongException`: Skip paths exceeding limits
-   `DirectoryNotFoundException`: Skip missing directories
-   `FileNotFoundException`: Skip missing files
-   `IOException`: Skip I/O errors
-   `SecurityException`: Skip security violations

**Enumeration Options**:

-   `IgnoreInaccessible`: Skip inaccessible items
-   `AttributesToSkip`: Skip reparse points and system files
-   `ReturnSpecialDirectories`: Don't return special directories
-   `RecurseSubdirectories`: Controlled recursion

### Cancellation Support

Scans support safe cancellation:

-   **Cancellation Tokens**: Used throughout scan process
-   **Checkpoints**: Cancellation checked at key points
-   **Immediate Stop**: Scan stops as soon as cancellation requested
-   **No Partial State**: No cleanup needed after cancellation
-   **Safe to Restart**: Can restart scan immediately after cancellation

## Data Models

### DeepScanRequest

Represents a scan request:

-   **RootPath**: Starting path for scan
-   **MaxItems**: Maximum number of results
-   **MinimumSizeInMegabytes**: Minimum file size threshold
-   **IncludeHiddenFiles**: Whether to include hidden files
-   **NameFilters**: List of filename filters
-   **NameMatchMode**: Match mode (Contains, StartsWith, EndsWith, Exact)
-   **IsCaseSensitiveNameMatch**: Case sensitivity flag
-   **IncludeDirectories**: Whether to include directories

### DeepScanResult

Represents scan results:

-   **Findings**: List of discovered items
-   **RootPath**: Path that was scanned
-   **GeneratedAt**: When scan completed
-   **TotalCandidates**: Total number of findings
-   **TotalSizeBytes**: Total size of all findings
-   **TotalSizeDisplay**: Formatted total size
-   **CategoryTotals**: Size totals by category

### DeepScanFinding

Represents a single finding:

-   **Path**: Full path to file/folder
-   **Name**: File/folder name
-   **Directory**: Parent directory
-   **SizeBytes**: Size in bytes
-   **ModifiedUtc**: Last modified timestamp
-   **Extension**: File extension (empty for directories)
-   **IsDirectory**: Whether item is a directory
-   **Category**: Category classification
-   **SizeDisplay**: Formatted size string
-   **ModifiedDisplay**: Formatted date string
-   **KindDisplay**: "File" or "Folder"

### DeepScanProgressUpdate

Represents progress during scanning:

-   **Findings**: Current top candidates
-   **ProcessedEntries**: Number of items processed
-   **ProcessedSizeBytes**: Total size processed
-   **CurrentPath**: Currently scanned path
-   **LatestFinding**: Most recent finding
-   **CategoryTotals**: Size totals by category
-   **IsFinal**: Whether this is the final update
-   **ProcessedSizeDisplay**: Formatted processed size

## State Management

### ViewModel Properties

-   `TargetPath`: Scan root path
-   `MinimumSizeMb`: Minimum size threshold
-   `MaxItems`: Maximum results count
-   `IncludeHidden`: Include hidden files flag
-   `IncludeDirectories`: Include directories flag
-   `NameFilter`: Filename filter text
-   `SelectedMatchMode`: Name match mode
-   `IsCaseSensitiveMatch`: Case sensitivity flag
-   `SelectedPreset`: Selected preset location
-   `IsBusy`: Whether scan is in progress
-   `Summary`: Scan summary text
-   `LastScanned`: Timestamp of last scan
-   `VisibleFindings`: Observable collection of visible findings
-   `CurrentPage`: Current page number
-   `TotalPages`: Total number of pages
-   `PageDisplay`: "Page X of Y" string
-   `CanGoToPreviousPage`: Whether previous page is available
-   `CanGoToNextPage`: Whether next page is available

### Pagination

Results are paginated for performance:

-   **Page Size**: 100 items per page (fixed)
-   **Dynamic Updates**: Pages update as scan progresses
-   **Navigation**: Previous/Next buttons for navigation
-   **Page Display**: Shows current page and total pages

## Integration Points

### Main ViewModel

Status messages are relayed to `MainViewModel.SetStatusMessage()` for display in the main application status bar.

### File System

Direct integration with Windows file system:

-   Uses `System.IO` for file operations
-   Uses `FileSystemEnumerable` for efficient enumeration
-   Handles Windows-specific path limitations
-   Respects Windows file attributes

## Best Practices

### For Users

1.  **Review Before Deleting**: Always review findings before deleting
2.  **Start with Scans**: Run scans first to understand what's consuming space
3.  **Use Filters**: Use filters to focus on specific file types or locations
4.  **Check Categories**: Review category classifications before deleting
5.  **Verify Paths**: Verify file paths before deletion
6.  **Backup Important Data**: Backup important data before bulk deletions

### For Developers

1.  **Always Confirm Deletions**: Never delete without explicit user confirmation
2.  **Handle Read-Only Files**: Always clear read-only flags before deletion
3.  **Verify Existence**: Check file existence before attempting operations
4.  **Handle Errors Gracefully**: Catch and handle all file system exceptions
5.  **Report Progress**: Provide progress updates during long operations
6.  **Respect Cancellation**: Honor cancellation tokens throughout operations

## Technical Notes

### File System Enumeration

-   Uses `FileSystemEnumerable<T>` for efficient enumeration
-   Supports parallel processing for subdirectories
-   Uses priority queue to maintain top candidates
-   Implements debouncing for progress updates

### Size Calculation

-   File sizes: Direct from file system metadata
-   Directory sizes: Sum of all contained files recursively
-   Deduplication: Prevents double-counting files under directories
-   Formatting: Human-readable format (B, KB, MB, GB, TB)

### Category Classification

Categories are determined by:

-   **Path Analysis**: Checks path for known markers (e.g., "\\steamapps\\")
-   **Extension Matching**: Matches file extensions to categories
-   **Directory Names**: Checks for cache/temp directories
-   **System Detection**: Identifies system files and directories

**Categories**:

-   Games
-   Videos
-   Pictures
-   Music
-   Documents
-   Archives
-   Applications
-   App Data
-   Cache
-   Downloads
-   Desktop
-   System
-   Databases
-   Logs
-   Cloud Sync
-   Other

### Performance Optimizations

-   **Parallel Processing**: Parallel directory traversal when multiple subdirectories
-   **Priority Queue**: Maintains only top N candidates (by size)
-   **Progress Debouncing**: Limits progress update frequency
-   **Lazy Evaluation**: Only processes items meeting criteria
-   **Early Termination**: Stops when max items reached

### Memory Management

-   **Pagination**: Results paginated to limit memory usage
-   **Streaming Updates**: Progress updates streamed during scan
-   **Queue Management**: Priority queue limited to max items
-   **Garbage Collection**: Large objects disposed promptly

## Future Enhancements

Potential improvements:

-   Recycle Bin support for deletions
-   Batch deletion with confirmation
-   Export scan results to CSV/JSON
-   Save/load scan configurations
-   Scheduled scans
-   Size trend analysis
-   Duplicate file detection
-   Integration with cleanup page
-   Advanced filtering options
-   Scan history

