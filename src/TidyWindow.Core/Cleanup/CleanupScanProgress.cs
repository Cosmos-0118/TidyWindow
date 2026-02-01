namespace TidyWindow.Core.Cleanup;

/// <summary>
/// Represents incremental progress during a cleanup scan/preview operation.
/// </summary>
public readonly struct CleanupScanProgress
{
    public CleanupScanProgress(int completedTargets, int totalTargets, string? currentCategory, long totalBytesScanned, int totalFilesScanned)
    {
        CompletedTargets = completedTargets < 0 ? 0 : completedTargets;
        TotalTargets = totalTargets < 0 ? 0 : totalTargets;
        CurrentCategory = currentCategory ?? string.Empty;
        TotalBytesScanned = totalBytesScanned < 0 ? 0 : totalBytesScanned;
        TotalFilesScanned = totalFilesScanned < 0 ? 0 : totalFilesScanned;
    }

    /// <summary>
    /// Gets the number of cleanup targets (categories) that have been scanned.
    /// </summary>
    public int CompletedTargets { get; }

    /// <summary>
    /// Gets the total number of cleanup targets (categories) to scan.
    /// </summary>
    public int TotalTargets { get; }

    /// <summary>
    /// Gets the category name currently being scanned.
    /// </summary>
    public string CurrentCategory { get; }

    /// <summary>
    /// Gets the total bytes scanned across all targets so far.
    /// </summary>
    public long TotalBytesScanned { get; }

    /// <summary>
    /// Gets the total files scanned across all targets so far.
    /// </summary>
    public int TotalFilesScanned { get; }

    /// <summary>
    /// Gets the progress percentage (0-100).
    /// </summary>
    public int ProgressPercent => TotalTargets > 0 ? (int)((double)CompletedTargets / TotalTargets * 100) : 0;
}
