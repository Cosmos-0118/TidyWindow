using System;

namespace TidyWindow.Core.Processes.ThreatWatch;

/// <summary>
/// Represents an auto-start artifact evaluated by Threat Watch.
/// </summary>
public sealed record StartupEntrySnapshot
{
    public StartupEntrySnapshot(
        string entryId,
        string processName,
        string executablePath,
        StartupEntryLocation location,
        string? arguments,
        string? source,
        string? description)
    {
        if (string.IsNullOrWhiteSpace(entryId))
        {
            throw new ArgumentException("Entry identifier is required.", nameof(entryId));
        }

        EntryId = entryId.Trim();
        ProcessName = string.IsNullOrWhiteSpace(processName) ? "unknown.exe" : processName.Trim();
        ExecutablePath = string.IsNullOrWhiteSpace(executablePath) ? string.Empty : executablePath.Trim();
        Location = location;
        Arguments = string.IsNullOrWhiteSpace(arguments) ? null : arguments.Trim();
        Source = string.IsNullOrWhiteSpace(source) ? null : source.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
    }

    public string EntryId { get; init; }

    public string ProcessName { get; init; }

    public string ExecutablePath { get; init; }

    public StartupEntryLocation Location { get; init; }

    public string? Arguments { get; init; }

    public string? Source { get; init; }

    public string? Description { get; init; }
}

public enum StartupEntryLocation
{
    Unknown = 0,
    RunKey = 1,
    StartupFolder = 2,
    ScheduledTask = 3,
    Services = 4
}
