using System;

namespace TidyWindow.Core.Cleanup;

/// <summary>
/// Configures optional safeguards used during cleanup deletion operations.
/// </summary>
public sealed record CleanupDeletionOptions
{
    public static CleanupDeletionOptions Default { get; } = new();

    /// <summary>
    /// Skips deleting items flagged as hidden.
    /// </summary>
    public bool SkipHiddenItems { get; init; }

    /// <summary>
    /// Skips deleting items flagged as system.
    /// </summary>
    public bool SkipSystemItems { get; init; }

    /// <summary>
    /// Skips deleting items modified within <see cref="RecentItemThreshold"/>.
    /// </summary>
    public bool SkipRecentItems { get; init; }

    /// <summary>
    /// Minimum age for items before they are eligible for deletion when <see cref="SkipRecentItems"/> is enabled.
    /// </summary>
    public TimeSpan RecentItemThreshold { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Maximum number of retries when file system operations fail with transient errors.
    /// </summary>
    public int MaxRetryCount { get; init; } = 2;

    /// <summary>
    /// Delay between retries when an error occurs.
    /// </summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Attempts to move items to the recycle bin instead of deleting permanently when possible.
    /// </summary>
    public bool PreferRecycleBin { get; init; }

    /// <summary>
    /// Falls back to permanent deletion if moving to the recycle bin fails.
    /// </summary>
    public bool AllowPermanentDeleteFallback { get; init; } = true;

    internal CleanupDeletionOptions Sanitize()
    {
        var retryCount = MaxRetryCount < 0 ? 0 : MaxRetryCount;
        var retryDelay = RetryDelay < TimeSpan.Zero ? TimeSpan.Zero : RetryDelay;
        var threshold = RecentItemThreshold < TimeSpan.Zero ? TimeSpan.Zero : RecentItemThreshold;

        if (retryCount == MaxRetryCount && retryDelay == RetryDelay && threshold == RecentItemThreshold)
        {
            return this;
        }

        return this with
        {
            MaxRetryCount = retryCount,
            RetryDelay = retryDelay,
            RecentItemThreshold = threshold
        };
    }
}
