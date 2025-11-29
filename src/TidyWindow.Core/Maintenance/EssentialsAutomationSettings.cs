using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace TidyWindow.Core.Maintenance;

/// <summary>
/// Stores automation preferences for running Essentials tasks on a schedule.
/// </summary>
public sealed record EssentialsAutomationSettings
{
    public const int MinimumIntervalMinutes = 60;
    public const int MaximumIntervalMinutes = 43200;
    private const int DefaultIntervalMinutes = 60;

    public static EssentialsAutomationSettings Default { get; } = new(false, DefaultIntervalMinutes, null, ImmutableArray<string>.Empty);

    public EssentialsAutomationSettings(
        bool automationEnabled,
        int intervalMinutes,
        DateTimeOffset? lastRunUtc,
        IEnumerable<string>? taskIds)
    {
        AutomationEnabled = automationEnabled;
        IntervalMinutes = Clamp(intervalMinutes);
        LastRunUtc = lastRunUtc;
        TaskIds = NormalizeTasks(taskIds);
    }

    public EssentialsAutomationSettings(
        bool automationEnabled,
        int intervalMinutes,
        DateTimeOffset? lastRunUtc,
        ImmutableArray<string> taskIds)
        : this(automationEnabled, intervalMinutes, lastRunUtc, (IEnumerable<string>)taskIds)
    {
    }

    public bool AutomationEnabled { get; init; }

    public int IntervalMinutes { get; init; }

    public DateTimeOffset? LastRunUtc { get; init; }

    public ImmutableArray<string> TaskIds { get; init; } = ImmutableArray<string>.Empty;

    public EssentialsAutomationSettings WithTasks(IEnumerable<string> taskIds)
        => this with { TaskIds = NormalizeTasks(taskIds) };

    public EssentialsAutomationSettings WithInterval(int intervalMinutes)
        => this with { IntervalMinutes = Clamp(intervalMinutes) };

    public EssentialsAutomationSettings WithLastRun(DateTimeOffset? timestamp)
        => this with { LastRunUtc = timestamp };

    public EssentialsAutomationSettings Normalize()
        => this with
        {
            IntervalMinutes = Clamp(IntervalMinutes),
            TaskIds = NormalizeTasks(TaskIds)
        };

    private static int Clamp(int value)
    {
        var fallback = DefaultIntervalMinutes;
        if (value <= 0)
        {
            value = fallback;
        }

        return Math.Clamp(value, MinimumIntervalMinutes, MaximumIntervalMinutes);
    }

    private static ImmutableArray<string> NormalizeTasks(IEnumerable<string>? taskIds)
    {
        if (taskIds is null)
        {
            return ImmutableArray<string>.Empty;
        }

        var normalized = taskIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .Where(static id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        return normalized.IsDefault ? ImmutableArray<string>.Empty : normalized;
    }
}
