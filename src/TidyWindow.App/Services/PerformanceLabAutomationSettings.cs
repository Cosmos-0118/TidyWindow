using System;
using System.Collections.Generic;

namespace TidyWindow.App.Services;

public sealed record PerformanceLabAutomationSnapshot(
    bool ApplyUltimatePlan,
    bool ApplyServiceTemplate,
    bool ApplyHardwareFix,
    bool ApplyKernelPreset,
    bool ApplyVbsDisable,
    bool ApplyEtwCleanup,
    bool ApplyPagefilePreset,
    bool ApplySchedulerPreset,
    bool ApplyAutoTune,
    string ServiceTemplateId,
    string PagefilePresetId,
    string TargetPagefileDrive,
    int? PagefileInitialMb,
    int? PagefileMaxMb,
    bool RunWorkingSetSweep,
    bool SweepPinnedApps,
    string SchedulerPresetId,
    string SchedulerProcessNames,
    string AutoTuneProcessNames,
    string AutoTunePresetId,
    string EtwMode)
{
    public static PerformanceLabAutomationSnapshot Empty => new(
        ApplyUltimatePlan: false,
        ApplyServiceTemplate: false,
        ApplyHardwareFix: false,
        ApplyKernelPreset: false,
        ApplyVbsDisable: false,
        ApplyEtwCleanup: false,
        ApplyPagefilePreset: false,
        ApplySchedulerPreset: false,
        ApplyAutoTune: false,
        ServiceTemplateId: "Balanced",
        PagefilePresetId: "SystemManaged",
        TargetPagefileDrive: "C:",
        PagefileInitialMb: null,
        PagefileMaxMb: null,
        RunWorkingSetSweep: false,
        SweepPinnedApps: false,
        SchedulerPresetId: "Balanced",
        SchedulerProcessNames: string.Empty,
        AutoTuneProcessNames: string.Empty,
        AutoTunePresetId: "LatencyBoost",
        EtwMode: "Minimal");

    public bool HasActions => ApplyUltimatePlan || ApplyServiceTemplate || ApplyHardwareFix || ApplyKernelPreset || ApplyVbsDisable || ApplyEtwCleanup || ApplyPagefilePreset || ApplySchedulerPreset || ApplyAutoTune;

    public PerformanceLabAutomationSnapshot Normalize()
    {
        var safeDrive = string.IsNullOrWhiteSpace(TargetPagefileDrive) ? "C:" : TargetPagefileDrive.Trim();
        var safeServiceTemplate = string.IsNullOrWhiteSpace(ServiceTemplateId) ? "Balanced" : ServiceTemplateId.Trim();
        var safePagefilePreset = string.IsNullOrWhiteSpace(PagefilePresetId) ? "SystemManaged" : PagefilePresetId.Trim();
        var safeSchedulerPreset = string.IsNullOrWhiteSpace(SchedulerPresetId) ? "Balanced" : SchedulerPresetId.Trim();
        var safeSchedulerList = string.IsNullOrWhiteSpace(SchedulerProcessNames) ? string.Empty : SchedulerProcessNames.Trim();
        var safeAutoTuneList = string.IsNullOrWhiteSpace(AutoTuneProcessNames) ? string.Empty : AutoTuneProcessNames.Trim();
        var safeAutoTunePreset = string.IsNullOrWhiteSpace(AutoTunePresetId) ? "LatencyBoost" : AutoTunePresetId.Trim();
        var safeEtwMode = string.IsNullOrWhiteSpace(EtwMode) ? "Minimal" : EtwMode.Trim();

        int? Clamp(int? value)
        {
            if (value is null)
            {
                return null;
            }

            return Math.Clamp(value.Value, 0, 1048576); // cap at 1 TB to avoid runaway values
        }

        return this with
        {
            ServiceTemplateId = safeServiceTemplate,
            PagefilePresetId = safePagefilePreset,
            TargetPagefileDrive = safeDrive,
            PagefileInitialMb = Clamp(PagefileInitialMb),
            PagefileMaxMb = Clamp(PagefileMaxMb),
            SchedulerPresetId = safeSchedulerPreset,
            SchedulerProcessNames = safeSchedulerList,
            AutoTuneProcessNames = safeAutoTuneList,
            AutoTunePresetId = safeAutoTunePreset,
            EtwMode = safeEtwMode
        };
    }
}

public sealed record PerformanceLabAutomationSettings(bool AutomationEnabled, long LastBootMarker, DateTimeOffset? LastRunUtc, PerformanceLabAutomationSnapshot Snapshot)
{
    public static PerformanceLabAutomationSettings Default => new(false, 0, null, PerformanceLabAutomationSnapshot.Empty);

    public PerformanceLabAutomationSettings Normalize()
    {
        var marker = LastBootMarker < 0 ? 0 : LastBootMarker;
        var snapshot = Snapshot?.Normalize() ?? PerformanceLabAutomationSnapshot.Empty;
        return new PerformanceLabAutomationSettings(AutomationEnabled && snapshot.HasActions, marker, LastRunUtc, snapshot);
    }

    public PerformanceLabAutomationSettings WithRun(DateTimeOffset timestamp, long bootMarker)
    {
        return this with { LastRunUtc = timestamp, LastBootMarker = bootMarker };
    }
}

public sealed record PerformanceLabAutomationActionResult(string Name, bool Succeeded, string Message);

public sealed record PerformanceLabAutomationRunResult(DateTimeOffset ExecutedAtUtc, long BootMarker, IReadOnlyList<PerformanceLabAutomationActionResult> Actions, bool WasSkipped, string SkipReason)
{
    public static PerformanceLabAutomationRunResult Completed(DateTimeOffset timestamp, long bootMarker, IReadOnlyList<PerformanceLabAutomationActionResult> actions)
        => new(timestamp, bootMarker, actions, false, string.Empty);

    public static PerformanceLabAutomationRunResult Skipped(DateTimeOffset timestamp, long bootMarker, string reason)
        => new(timestamp, bootMarker, Array.Empty<PerformanceLabAutomationActionResult>(), true, reason);
}