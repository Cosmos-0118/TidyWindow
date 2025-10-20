namespace TidyWindow.Core.Cleanup;

/// <summary>
/// Represents incremental progress during a cleanup deletion operation.
/// </summary>
public readonly struct CleanupDeletionProgress
{
    public CleanupDeletionProgress(int completed, int total, string? currentPath)
    {
        Completed = completed < 0 ? 0 : completed;
        Total = total < 0 ? 0 : total;
        CurrentPath = currentPath ?? string.Empty;
    }

    /// <summary>
    /// Gets the number of items processed so far.
    /// </summary>
    public int Completed { get; }

    /// <summary>
    /// Gets the total number of items scheduled for deletion.
    /// </summary>
    public int Total { get; }

    /// <summary>
    /// Gets the path currently being processed.
    /// </summary>
    public string CurrentPath { get; }
}
