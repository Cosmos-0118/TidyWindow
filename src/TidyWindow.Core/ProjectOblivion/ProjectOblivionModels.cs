using System;
using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace TidyWindow.Core.ProjectOblivion;

public sealed record ProjectOblivionInventorySnapshot(
    ImmutableArray<ProjectOblivionApp> Apps,
    ImmutableArray<string> Warnings,
    DateTimeOffset GeneratedAt);

public sealed record ProjectOblivionApp(
    string AppId,
    string Name,
    string? Version,
    string? Publisher,
    string? Source,
    string? Scope,
    string? InstallRoot,
    ImmutableArray<string> InstallRoots,
    string? UninstallCommand,
    string? QuietUninstallCommand,
    string? PackageFamilyName,
    long? EstimatedSizeBytes,
    ImmutableArray<string> ArtifactHints,
    ImmutableArray<ProjectOblivionManagerHint> ManagerHints,
    ImmutableArray<string> ProcessHints,
    ImmutableArray<string> ServiceHints,
    ProjectOblivionRegistryInfo? Registry,
    ImmutableArray<string> Tags,
    string? Confidence);

public sealed record ProjectOblivionManagerHint(
    string Manager,
    string PackageId,
    string? InstalledVersion,
    string? AvailableVersion,
    string? Source);

public sealed record ProjectOblivionRegistryInfo(
    string? Hive,
    string? KeyPath,
    string? DisplayIcon,
    string? InstallDate,
    string? InstallLocation);

public sealed record ProjectOblivionRunRequest(
    string AppId,
    string? InventoryPath = null,
    string? SelectionPath = null,
    bool AutoSelectAll = false,
    bool WaitForSelection = false,
    int SelectionTimeoutSeconds = 600,
    bool DryRun = false);

public sealed record ProjectOblivionRunEvent(
    string Type,
    DateTimeOffset Timestamp,
    JsonObject? Payload,
    string Raw);
