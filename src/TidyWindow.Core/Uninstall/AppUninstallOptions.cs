using System.Collections.Generic;

namespace TidyWindow.Core.Uninstall;

public sealed record AppUninstallOptions
{
    public static AppUninstallOptions Default { get; } = new();

    public bool DryRun { get; init; }

    public bool EnableWingetFallback { get; init; }

    public bool WingetOnly { get; init; }

    public string? OperationId { get; init; }

    public IReadOnlyDictionary<string, string>? MetadataOverrides { get; init; }
}
