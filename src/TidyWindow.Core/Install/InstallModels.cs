using System;
using System.Collections.Immutable;

namespace TidyWindow.Core.Install;

public sealed record InstallPackageDefinition(
    string Id,
    string Name,
    string Manager,
    string Command,
    bool RequiresAdmin,
    string Summary,
    string? Homepage,
    ImmutableArray<string> Tags)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Id) && !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Manager) && !string.IsNullOrWhiteSpace(Command);
}

public sealed record InstallBundleDefinition(
    string Id,
    string Name,
    string Description,
    ImmutableArray<string> PackageIds)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Id) && !string.IsNullOrWhiteSpace(Name);
}

public enum InstallQueueStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

public sealed record InstallAttemptResult(
    int AttemptNumber,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    bool IsSuccess,
    ImmutableArray<string> Output,
    ImmutableArray<string> Errors,
    string? Summary);
