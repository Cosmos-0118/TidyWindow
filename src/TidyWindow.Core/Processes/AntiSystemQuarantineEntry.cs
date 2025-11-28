using System;
using System.IO;

namespace TidyWindow.Core.Processes;

/// <summary>
/// Represents a persisted quarantine record captured by Anti-System actions.
/// </summary>
public sealed record AntiSystemQuarantineEntry
{
    public AntiSystemQuarantineEntry(
        string id,
        string processName,
        string filePath,
        string? notes,
        string? addedBy,
        DateTimeOffset quarantinedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            throw new ArgumentException("Process name cannot be null or whitespace.", nameof(processName));
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path cannot be null or whitespace.", nameof(filePath));
        }

        var normalizedPath = NormalizePath(filePath);
        Id = string.IsNullOrWhiteSpace(id)
            ? CreateIdentifier(normalizedPath)
            : id.Trim();
        ProcessName = processName.Trim();
        FilePath = normalizedPath;
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        AddedBy = string.IsNullOrWhiteSpace(addedBy) ? null : addedBy.Trim();
        QuarantinedAtUtc = quarantinedAtUtc == default ? DateTimeOffset.UtcNow : quarantinedAtUtc;
    }

    public string Id { get; init; }

    public string ProcessName { get; init; }

    public string FilePath { get; init; }

    public string? Notes { get; init; }

    public string? AddedBy { get; init; }

    public DateTimeOffset QuarantinedAtUtc { get; init; }

    public static AntiSystemQuarantineEntry Create(string processName, string filePath, string? notes = null, string? addedBy = null, DateTimeOffset? quarantinedAtUtc = null)
    {
        return new AntiSystemQuarantineEntry(
            id: string.Empty,
            processName,
            filePath,
            notes,
            addedBy,
            quarantinedAtUtc ?? DateTimeOffset.UtcNow);
    }

    public AntiSystemQuarantineEntry Normalize()
    {
        var normalizedPath = NormalizePath(FilePath);
        var normalizedTimestamp = QuarantinedAtUtc == default ? DateTimeOffset.UtcNow : QuarantinedAtUtc;
        return this with
        {
            Id = CreateIdentifier(normalizedPath),
            FilePath = normalizedPath,
            QuarantinedAtUtc = normalizedTimestamp
        };
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            return path.Trim();
        }
    }

    private static string CreateIdentifier(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return Guid.NewGuid().ToString("N");
        }

        return normalizedPath.ToLowerInvariant();
    }
}
