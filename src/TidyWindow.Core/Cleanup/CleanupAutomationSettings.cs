using System;

namespace TidyWindow.Core.Cleanup;

/// <summary>
/// Stores automation preferences for unattended cleanup runs.
/// </summary>
public enum CleanupAutomationDeletionMode
{
    SkipLocked,
    MoveToRecycleBin,
    ForceDelete
}

/// <summary>
/// Defines how cleanup automation runs are scheduled and executed.
/// </summary>
public sealed record CleanupAutomationSettings
{
    public const int MinimumIntervalMinutes = 60; // 1 hour
    public const int MaximumIntervalMinutes = 43_200; // 30 days
    private const int DefaultIntervalMinutes = 1_440; // 1 day
    public const int MinimumTopItemCount = 50;
    public const int MaximumTopItemCount = 50_000;
    private const int DefaultTopItemCount = 200;

    public static CleanupAutomationSettings Default { get; } = new(
        automationEnabled: false,
        intervalMinutes: DefaultIntervalMinutes,
        deletionMode: CleanupAutomationDeletionMode.SkipLocked,
        includeDownloads: true,
        includeBrowserHistory: false,
        topItemCount: DefaultTopItemCount,
        lastRunUtc: null);

    public CleanupAutomationSettings(
        bool automationEnabled,
        int intervalMinutes,
        CleanupAutomationDeletionMode deletionMode,
        bool includeDownloads,
        bool includeBrowserHistory,
        int topItemCount,
        DateTimeOffset? lastRunUtc)
    {
        AutomationEnabled = automationEnabled;
        IntervalMinutes = ClampInterval(intervalMinutes);
        DeletionMode = deletionMode;
        IncludeDownloads = includeDownloads;
        IncludeBrowserHistory = includeBrowserHistory;
        TopItemCount = ClampTopItemCount(topItemCount);
        LastRunUtc = lastRunUtc;
    }

    public bool AutomationEnabled { get; init; }

    public int IntervalMinutes { get; init; }

    public CleanupAutomationDeletionMode DeletionMode { get; init; }

    public bool IncludeDownloads { get; init; }

    public bool IncludeBrowserHistory { get; init; }

    public int TopItemCount { get; init; }

    public DateTimeOffset? LastRunUtc { get; init; }

    public CleanupAutomationSettings WithInterval(int intervalMinutes)
        => this with { IntervalMinutes = ClampInterval(intervalMinutes) };

    public CleanupAutomationSettings WithLastRun(DateTimeOffset? timestamp)
        => this with { LastRunUtc = timestamp };

    public CleanupAutomationSettings WithDeletionMode(CleanupAutomationDeletionMode mode)
        => this with { DeletionMode = mode };

    public CleanupAutomationSettings WithTopItemCount(int count)
        => this with { TopItemCount = ClampTopItemCount(count) };

    public CleanupAutomationSettings Normalize()
    {
        return this with
        {
            IntervalMinutes = ClampInterval(IntervalMinutes),
            TopItemCount = ClampTopItemCount(TopItemCount)
        };
    }

    private static int ClampInterval(int intervalMinutes)
    {
        if (intervalMinutes <= 0)
        {
            intervalMinutes = DefaultIntervalMinutes;
        }

        return Math.Clamp(intervalMinutes, MinimumIntervalMinutes, MaximumIntervalMinutes);
    }

    private static int ClampTopItemCount(int count)
    {
        if (count <= 0)
        {
            count = DefaultTopItemCount;
        }

        return Math.Clamp(count, MinimumTopItemCount, MaximumTopItemCount);
    }
}