using System;
using System.Collections.Immutable;

namespace TidyWindow.Core.PathPilot;

public sealed record PathPilotInventorySnapshot(
    ImmutableArray<PathPilotRuntime> Runtimes,
    MachinePathInfo MachinePath,
    ImmutableArray<string> Warnings,
    DateTimeOffset GeneratedAt);

public sealed record MachinePathInfo(
    ImmutableArray<MachinePathEntry> Entries,
    string? RawValue);

public sealed record MachinePathEntry(int Index, string Value, string? ResolvedValue);

public sealed record PathPilotRuntime(
    string Id,
    string Name,
    string ExecutableName,
    string? DesiredVersion,
    string? Description,
    ImmutableArray<PathPilotInstallation> Installations,
    PathPilotRuntimeStatus Status,
    PathPilotActiveResolution? ActiveResolution,
    ImmutableArray<string> ResolutionOrder);

public sealed record PathPilotInstallation(
    string Id,
    string Directory,
    string ExecutablePath,
    string? Version,
    string Architecture,
    string Source,
    bool IsActive,
    ImmutableArray<string> Notes);

public sealed record PathPilotRuntimeStatus(
    bool IsMissing,
    bool HasDuplicates,
    bool IsDrifted,
    bool HasUnknownActive);

public sealed record PathPilotActiveResolution(
    string? ExecutablePath,
    string? PathEntry,
    bool MatchesKnownInstallation,
    string? InstallationId,
    string? Source);

public sealed record PathPilotSwitchRequest(
    string RuntimeId,
    string RuntimeName,
    string InstallationId,
    string ExecutablePath);

public sealed record PathPilotSwitchResult(
    string RuntimeId,
    string TargetDirectory,
    string TargetExecutable,
    string? InstallationId,
    string? BackupPath,
    string? LogPath,
    bool PathUpdated,
    bool Success,
    string? Message,
    string? PreviousPath,
    string? UpdatedPath,
    DateTimeOffset Timestamp);

public sealed record PathPilotSwitchOperationResult(
    PathPilotInventorySnapshot Snapshot,
    PathPilotSwitchResult SwitchResult);

public enum PathPilotExportFormat
{
    Json,
    Markdown
}

public sealed record PathPilotExportResult(
    PathPilotExportFormat Format,
    string FilePath,
    DateTimeOffset GeneratedAt);
