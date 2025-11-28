using System;

namespace TidyWindow.Core.Processes.AntiSystem;

/// <summary>
/// Lightweight immutable view of a running process used by the Anti-System detection pipeline.
/// </summary>
public sealed record RunningProcessSnapshot
{
    public RunningProcessSnapshot(
        int processId,
        string processName,
        string filePath,
        string? commandLine,
        int? parentProcessId,
        string? parentProcessName,
        int? grandParentProcessId,
        string? grandParentProcessName,
        DateTimeOffset? startedAtUtc,
        bool isElevated)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            throw new ArgumentException("Process name is required.", nameof(processName));
        }

        ProcessId = processId;
        ProcessName = processName.Trim();
        FilePath = string.IsNullOrWhiteSpace(filePath) ? string.Empty : filePath.Trim();
        CommandLine = string.IsNullOrWhiteSpace(commandLine) ? null : commandLine.Trim();
        ParentProcessId = parentProcessId;
        ParentProcessName = NormalizeProcessName(parentProcessName);
        GrandParentProcessId = grandParentProcessId;
        GrandParentProcessName = NormalizeProcessName(grandParentProcessName);
        StartedAtUtc = startedAtUtc;
        IsElevated = isElevated;
    }

    public int ProcessId { get; init; }

    public string ProcessName { get; init; }

    public string FilePath { get; init; }

    public string? CommandLine { get; init; }

    public int? ParentProcessId { get; init; }

    public string? ParentProcessName { get; init; }

    public int? GrandParentProcessId { get; init; }

    public string? GrandParentProcessName { get; init; }

    public DateTimeOffset? StartedAtUtc { get; init; }

    public bool IsElevated { get; init; }

    public string NormalizedProcessName => NormalizeProcessName(ProcessName) ?? ProcessName.ToLowerInvariant();

    public static string? NormalizeProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return name.Trim().ToLowerInvariant();
    }
}
