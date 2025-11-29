using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace TidyWindow.Core.Maintenance;

/// <summary>
/// Stores user preferences for automatically updating maintenance packages.
/// </summary>
public sealed record MaintenanceAutomationSettings
{
    public const int MinimumIntervalMinutes = 60;
    public const int MaximumIntervalMinutes = 10080; // 7 days
    private const int DefaultIntervalMinutes = 720; // 12 hours

    public static MaintenanceAutomationSettings Default { get; } = new(false, false, DefaultIntervalMinutes, null, ImmutableArray<MaintenanceAutomationTarget>.Empty);

    public MaintenanceAutomationSettings(
        bool automationEnabled,
        bool updateAllPackages,
        int intervalMinutes,
        DateTimeOffset? lastRunUtc,
        IEnumerable<MaintenanceAutomationTarget>? targets)
    {
        AutomationEnabled = automationEnabled;
        UpdateAllPackages = updateAllPackages;
        IntervalMinutes = Clamp(intervalMinutes);
        LastRunUtc = lastRunUtc;
        Targets = NormalizeTargets(targets);
    }

    public MaintenanceAutomationSettings(
        bool automationEnabled,
        bool updateAllPackages,
        int intervalMinutes,
        DateTimeOffset? lastRunUtc,
        ImmutableArray<MaintenanceAutomationTarget> targets)
        : this(automationEnabled, updateAllPackages, intervalMinutes, lastRunUtc, (IEnumerable<MaintenanceAutomationTarget>)targets)
    {
    }

    public bool AutomationEnabled { get; init; }

    public bool UpdateAllPackages { get; init; }

    public int IntervalMinutes { get; init; }

    public DateTimeOffset? LastRunUtc { get; init; }

    public ImmutableArray<MaintenanceAutomationTarget> Targets { get; init; } = ImmutableArray<MaintenanceAutomationTarget>.Empty;

    public MaintenanceAutomationSettings WithLastRun(DateTimeOffset? timestamp)
        => this with { LastRunUtc = timestamp };

    public MaintenanceAutomationSettings WithTargets(IEnumerable<MaintenanceAutomationTarget>? targets)
        => this with { Targets = NormalizeTargets(targets) };

    public MaintenanceAutomationSettings Normalize()
        => this with
        {
            IntervalMinutes = Clamp(IntervalMinutes),
            Targets = NormalizeTargets(Targets)
        };

    private static int Clamp(int value)
    {
        if (value <= 0)
        {
            value = DefaultIntervalMinutes;
        }

        return Math.Clamp(value, MinimumIntervalMinutes, MaximumIntervalMinutes);
    }

    private static ImmutableArray<MaintenanceAutomationTarget> NormalizeTargets(IEnumerable<MaintenanceAutomationTarget>? targets)
    {
        if (targets is null)
        {
            return ImmutableArray<MaintenanceAutomationTarget>.Empty;
        }

        var map = new Dictionary<string, MaintenanceAutomationTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            if (target is null)
            {
                continue;
            }

            var normalized = target.Normalize();
            if (!normalized.IsValid)
            {
                continue;
            }

            map[normalized.Key] = normalized;
        }

        var result = map.Values
            .OrderBy(static target => target.Manager, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static target => target.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        return result.IsDefault ? ImmutableArray<MaintenanceAutomationTarget>.Empty : result;
    }
}

/// <summary>
/// Represents a package that should be auto-updated when an update is detected.
/// </summary>
public sealed record MaintenanceAutomationTarget
{
    public MaintenanceAutomationTarget(string manager, string packageId, string? label)
    {
        Manager = NormalizeManager(manager);
        PackageId = NormalizePackageId(packageId);
        Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
    }

    public string Manager { get; init; }

    public string PackageId { get; init; }

    public string? Label { get; init; }

    public string Key => BuildKey(Manager, PackageId);

    public bool IsValid => !string.IsNullOrWhiteSpace(Manager) && !string.IsNullOrWhiteSpace(PackageId);

    public MaintenanceAutomationTarget Normalize()
        => new(Manager, PackageId, Label);

    public static string BuildKey(string manager, string packageId)
    {
        var normalizedManager = NormalizeManager(manager);
        var normalizedPackageId = NormalizePackageId(packageId);
        return string.IsNullOrWhiteSpace(normalizedManager) || string.IsNullOrWhiteSpace(normalizedPackageId)
            ? string.Empty
            : normalizedManager + "|" + normalizedPackageId;
    }

    private static string NormalizeManager(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string NormalizePackageId(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
