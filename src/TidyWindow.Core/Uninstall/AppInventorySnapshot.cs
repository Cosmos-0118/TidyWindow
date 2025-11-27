using System;
using System.Collections.Immutable;

namespace TidyWindow.Core.Uninstall;

public sealed record AppInventorySnapshot
{
    public AppInventorySnapshot(
        ImmutableArray<InstalledApp> apps,
        ImmutableArray<string> warnings,
        DateTimeOffset generatedAt,
        TimeSpan duration,
        bool isDryRun,
        bool isCacheHit,
        AppInventoryOptions options,
        ImmutableArray<string> plan)
    {
        Apps = apps;
        Warnings = warnings;
        GeneratedAt = generatedAt;
        Duration = duration;
        IsDryRun = isDryRun;
        IsCacheHit = isCacheHit;
        Options = options;
        Plan = plan;
    }

    public ImmutableArray<InstalledApp> Apps { get; init; }

    public ImmutableArray<string> Warnings { get; init; }

    public DateTimeOffset GeneratedAt { get; init; }

    public TimeSpan Duration { get; init; }

    public bool IsDryRun { get; init; }

    public bool IsCacheHit { get; init; }

    public AppInventoryOptions Options { get; init; }

    public ImmutableArray<string> Plan { get; init; }
}
