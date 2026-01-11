using System;
using TidyWindow.Core.Automation;

namespace TidyWindow.App.Services;

public sealed record AutoTuneAutomationSettings(bool AutomationEnabled, string ProcessNames, string PresetId, DateTimeOffset? LastRunUtc)
{
    public static AutoTuneAutomationSettings Default => new(false, string.Empty, "LatencyBoost", null);

    public AutoTuneAutomationSettings Normalize()
    {
        var processes = string.IsNullOrWhiteSpace(ProcessNames) ? string.Empty : ProcessNames.Trim();
        var preset = string.IsNullOrWhiteSpace(PresetId) ? "LatencyBoost" : PresetId.Trim();
        return this with { ProcessNames = processes, PresetId = preset };
    }

    public AutoTuneAutomationSettings WithLastRun(DateTimeOffset timestamp) => this with { LastRunUtc = timestamp };
}

public sealed record AutoTuneAutomationRunResult(DateTimeOffset ExecutedAtUtc, PowerShellInvocationResult? InvocationResult, bool WasSkipped, string? SkipReason)
{
    public static AutoTuneAutomationRunResult Create(DateTimeOffset timestamp, PowerShellInvocationResult result)
        => new(timestamp, result, false, null);

    public static AutoTuneAutomationRunResult Skipped(DateTimeOffset timestamp, string? reason)
        => new(timestamp, null, true, reason);
}