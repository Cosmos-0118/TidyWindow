using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace TidyWindow.Core.Uninstall;

public sealed record InstalledApp(
    string Name,
    string? Version,
    string? Publisher,
    string? InstallLocation,
    string? UninstallString,
    string? QuietUninstallString,
    bool IsWindowsInstaller,
    string? ProductCode,
    string? InstallerType,
    ImmutableArray<string> SourceTags,
    ImmutableArray<string> InstallerHints,
    string? RegistryKey,
    bool IsSystemComponent,
    string? ReleaseType,
    long? EstimatedSizeBytes,
    string? InstallDate,
    string? DisplayIcon,
    string? Language,
    string? WingetId,
    string? WingetSource,
    string? WingetVersion,
    string? WingetAvailableVersion,
    ImmutableDictionary<string, string> Metadata)
{
    public bool HasQuietUninstall => !string.IsNullOrWhiteSpace(QuietUninstallString);

    public bool HasWingetMetadata => !string.IsNullOrWhiteSpace(WingetId);

    public static readonly InstalledApp Empty = new(
        Name: "Unknown",
        Version: null,
        Publisher: null,
        InstallLocation: null,
        UninstallString: null,
        QuietUninstallString: null,
        IsWindowsInstaller: false,
        ProductCode: null,
        InstallerType: "Unknown",
        SourceTags: ImmutableArray<string>.Empty,
        InstallerHints: ImmutableArray<string>.Empty,
        RegistryKey: null,
        IsSystemComponent: false,
        ReleaseType: null,
        EstimatedSizeBytes: null,
        InstallDate: null,
        DisplayIcon: null,
        Language: null,
        WingetId: null,
        WingetSource: null,
        WingetVersion: null,
        WingetAvailableVersion: null,
        Metadata: ImmutableDictionary<string, string>.Empty);
}
