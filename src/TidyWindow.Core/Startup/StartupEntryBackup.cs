using System;

namespace TidyWindow.Core.Startup;

public sealed record StartupEntryBackup(
    string Id,
    StartupItemSourceKind SourceKind,
    string? RegistryRoot,
    string? RegistrySubKey,
    string? RegistryValueName,
    string? RegistryValueData,
    string? FileOriginalPath,
    string? FileBackupPath,
    string? TaskPath,
    bool? TaskEnabled,
    string? ServiceName,
    int? ServiceStartValue,
    int? ServiceDelayedAutoStart,
    DateTimeOffset CreatedAtUtc);
