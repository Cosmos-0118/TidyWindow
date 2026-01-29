using System;
using System.Collections.Generic;

namespace TidyWindow.Core.Startup;

public enum StartupItemSourceKind
{
    Unknown = 0,
    RunKey = 1,
    RunOnce = 2,
    StartupFolder = 3,
    ScheduledTask = 4,
    Service = 5,
    PackagedTask = 6,
    Winlogon = 7,
    ActiveSetup = 8,
    ShellFolder = 9,
    ExplorerRun = 10,
    AppInitDll = 11,
    ImageFileExecutionOptions = 12,
    BootExecute = 13
}

public enum StartupSignatureStatus
{
    Unknown = 0,
    Unsigned = 1,
    Signed = 2,
    SignedTrusted = 3
}

public enum StartupImpact
{
    Unknown = 0,
    Low = 1,
    Medium = 2,
    High = 3
}

public sealed record StartupInventoryOptions
{
    public static StartupInventoryOptions Default { get; } = new();

    public bool IncludeRunKeys { get; init; } = true;

    public bool IncludeRunOnce { get; init; } = true;

    public bool IncludeStartupFolders { get; init; } = true;

    public bool IncludeScheduledTasks { get; init; } = true;

    public bool IncludeServices { get; init; } = true;

    public bool IncludePackagedApps { get; init; } = true;

    /// <summary>
    /// When false, disabled tasks/services are filtered out.
    /// </summary>
    public bool IncludeDisabled { get; init; } = true;

    /// <summary>
    /// Include entries that only exist in Explorer's StartupApproved state (no live Run/StartupFolder item).
    /// </summary>
    public bool IncludeStartupApprovedOrphans { get; init; } = true;

    public static StartupInventoryOptions ForThreatWatch() => Default with
    {
        IncludeDisabled = false
    };
}

public sealed record StartupItem(
    string Id,
    string Name,
    string ExecutablePath,
    StartupItemSourceKind SourceKind,
    string SourceTag,
    string? Arguments,
    string? RawCommand,
    bool IsEnabled,
    string? EntryLocation,
    string? Publisher,
    StartupSignatureStatus SignatureStatus,
    StartupImpact Impact,
    long? FileSizeBytes,
    DateTimeOffset? LastModifiedUtc,
    string? UserContext);

public sealed record StartupInventorySnapshot(
    IReadOnlyList<StartupItem> Items,
    IReadOnlyList<string> Warnings,
    DateTimeOffset GeneratedAt,
    bool IsPartial)
{
    public static StartupInventorySnapshot Empty { get; } = new(Array.Empty<StartupItem>(), Array.Empty<string>(), DateTimeOffset.MinValue, false);
}
