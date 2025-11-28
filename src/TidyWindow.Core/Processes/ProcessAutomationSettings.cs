using System;

namespace TidyWindow.Core.Processes;

/// <summary>
/// Stores automation preferences for enforcing auto-stop actions.
/// </summary>
public sealed record ProcessAutomationSettings
{
    public const int MinimumIntervalMinutes = 5;
    public const int MaximumIntervalMinutes = 240;
    private const int DefaultIntervalMinutes = 30;

    public static ProcessAutomationSettings Default { get; } = new(false, DefaultIntervalMinutes, null);

    public ProcessAutomationSettings(bool autoStopEnabled, int autoStopIntervalMinutes, DateTimeOffset? lastRunUtc)
    {
        AutoStopEnabled = autoStopEnabled;
        AutoStopIntervalMinutes = Clamp(autoStopIntervalMinutes);
        LastRunUtc = lastRunUtc;
    }

    public bool AutoStopEnabled { get; init; }

    public int AutoStopIntervalMinutes { get; init; }

    public DateTimeOffset? LastRunUtc { get; init; }

    public ProcessAutomationSettings WithInterval(int intervalMinutes)
    {
        return this with { AutoStopIntervalMinutes = Clamp(intervalMinutes) };
    }

    public ProcessAutomationSettings WithLastRun(DateTimeOffset? timestamp)
    {
        return this with { LastRunUtc = timestamp };
    }

    public ProcessAutomationSettings Normalize()
    {
        return this with { AutoStopIntervalMinutes = Clamp(AutoStopIntervalMinutes) };
    }

    private static int Clamp(int value)
        => Math.Clamp(value <= 0 ? DefaultIntervalMinutes : value, MinimumIntervalMinutes, MaximumIntervalMinutes);
}
