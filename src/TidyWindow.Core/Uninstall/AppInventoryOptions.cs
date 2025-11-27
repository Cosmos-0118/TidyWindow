using System;

namespace TidyWindow.Core.Uninstall;

public sealed record AppInventoryOptions
{
    public static AppInventoryOptions Default { get; } = new();

    public bool IncludeSystemComponents { get; init; }

    public bool IncludeUpdates { get; init; }

    public bool IncludeWinget { get; init; } = true;

    public bool IncludeUserEntries { get; init; } = true;

    public bool ForceRefresh { get; init; }

    public bool DryRun { get; init; }
}
