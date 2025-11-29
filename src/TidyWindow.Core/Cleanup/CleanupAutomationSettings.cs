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

    public static CleanupAutomationSettings Default { get; } = new(
        automationEnabled: false,
        intervalMinutes: DefaultIntervalMinutes,
        deletionMode: CleanupAutomationDeletionMode.SkipLocked,
        includeDownloads: true,
        includeBrowserHistory: false,
        lastRunUtc: null);

    public CleanupAutomationSettings(
        bool automationEnabled,
        int intervalMinutes,
        CleanupAutomationDeletionMode deletionMode,
        bool includeDownloads,
        bool includeBrowserHistory,
        DateTimeOffset? lastRunUtc)
    {
        AutomationEnabled = automationEnabled;
        IntervalMinutes = ClampInterval(intervalMinutes);
        DeletionMode = deletionMode;
        IncludeDownloads = includeDownloads;
        IncludeBrowserHistory = includeBrowserHistory;
        LastRunUtc = lastRunUtc;
    }

    public bool AutomationEnabled { get; init; }

    public int IntervalMinutes { get; init; }

    public CleanupAutomationDeletionMode DeletionMode { get; init; }

    public bool IncludeDownloads { get; init; }

    public bool IncludeBrowserHistory { get; init; }

    public DateTimeOffset? LastRunUtc { get; init; }

    public CleanupAutomationSettings WithInterval(int intervalMinutes)
        => this with { IntervalMinutes = ClampInterval(intervalMinutes) };

    public CleanupAutomationSettings WithLastRun(DateTimeOffset? timestamp)
        => this with { LastRunUtc = timestamp };

    public CleanupAutomationSettings WithDeletionMode(CleanupAutomationDeletionMode mode)
        => this with { DeletionMode = mode };

    public CleanupAutomationSettings Normalize()
    {
        return this with
        {
            IntervalMinutes = ClampInterval(IntervalMinutes)
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
}