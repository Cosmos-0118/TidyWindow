using System;
using System.IO;

namespace TidyWindow.Core.Processes;

/// <summary>
/// Represents a persisted trust override so Anti-System ignores known safe processes or hashes.
/// </summary>
public sealed record AntiSystemWhitelistEntry
{
    public AntiSystemWhitelistEntry(
        string id,
        AntiSystemWhitelistEntryKind kind,
        string value,
        string? notes,
        string? addedBy,
        DateTimeOffset addedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(value));
        }

        Id = string.IsNullOrWhiteSpace(id)
            ? CreateIdentifier(kind, NormalizeValue(kind, value))
            : id.Trim();
        Kind = kind;
        Value = NormalizeValue(kind, value);
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        AddedBy = string.IsNullOrWhiteSpace(addedBy) ? null : addedBy.Trim();
        AddedAtUtc = addedAtUtc == default ? DateTimeOffset.UtcNow : addedAtUtc;
    }

    public string Id { get; init; }

    public AntiSystemWhitelistEntryKind Kind { get; init; }

    public string Value { get; init; }

    public string? Notes { get; init; }

    public string? AddedBy { get; init; }

    public DateTimeOffset AddedAtUtc { get; init; }

    public static AntiSystemWhitelistEntry CreateDirectory(string directoryPath, string? notes = null, string? addedBy = null, DateTimeOffset? addedAtUtc = null)
    {
        return new AntiSystemWhitelistEntry(
            id: string.Empty,
            AntiSystemWhitelistEntryKind.Directory,
            directoryPath,
            notes,
            addedBy,
            addedAtUtc ?? DateTimeOffset.UtcNow);
    }

    public static AntiSystemWhitelistEntry CreateProcess(string processName, string? notes = null, string? addedBy = null, DateTimeOffset? addedAtUtc = null)
    {
        return new AntiSystemWhitelistEntry(
            id: string.Empty,
            AntiSystemWhitelistEntryKind.ProcessName,
            processName,
            notes,
            addedBy,
            addedAtUtc ?? DateTimeOffset.UtcNow);
    }

    public static AntiSystemWhitelistEntry CreateHash(string sha256, string? notes = null, string? addedBy = null, DateTimeOffset? addedAtUtc = null)
    {
        return new AntiSystemWhitelistEntry(
            id: string.Empty,
            AntiSystemWhitelistEntryKind.FileHash,
            sha256,
            notes,
            addedBy,
            addedAtUtc ?? DateTimeOffset.UtcNow);
    }

    public AntiSystemWhitelistEntry Normalize()
    {
        var normalizedValue = NormalizeValue(Kind, Value);
        var normalizedId = CreateIdentifier(Kind, normalizedValue);
        var normalizedTimestamp = AddedAtUtc == default ? DateTimeOffset.UtcNow : AddedAtUtc;

        return this with
        {
            Id = normalizedId,
            Value = normalizedValue,
            AddedAtUtc = normalizedTimestamp
        };
    }

    public bool Matches(string? filePath, string? sha256, string? processName)
    {
        return Kind switch
        {
            AntiSystemWhitelistEntryKind.Directory => MatchesDirectory(filePath),
            AntiSystemWhitelistEntryKind.FileHash => MatchesHash(sha256),
            AntiSystemWhitelistEntryKind.ProcessName => MatchesProcess(processName),
            _ => false
        };
    }

    public static string CreateIdentifier(AntiSystemWhitelistEntryKind kind, string normalizedValue)
    {
        var prefix = kind.ToString().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalizedValue)
            ? prefix
            : $"{prefix}:{normalizedValue}";
    }

    public static string NormalizeValue(AntiSystemWhitelistEntryKind kind, string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return string.Empty;
        }

        return kind switch
        {
            AntiSystemWhitelistEntryKind.Directory => NormalizeDirectory(rawValue),
            AntiSystemWhitelistEntryKind.FileHash => rawValue.Trim().ToLowerInvariant(),
            AntiSystemWhitelistEntryKind.ProcessName => rawValue.Trim().ToLowerInvariant(),
            _ => rawValue.Trim()
        };
    }

    private bool MatchesDirectory(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        var normalizedPath = NormalizeDirectory(Path.GetDirectoryName(filePath) ?? filePath);
        return normalizedPath.StartsWith(Value, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesHash(string? sha256)
    {
        if (string.IsNullOrWhiteSpace(sha256))
        {
            return false;
        }

        return string.Equals(Value, sha256.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var normalizedName = processName.Trim().ToLowerInvariant();
        return string.Equals(Value, normalizedName, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            var normalized = Path.GetFullPath(path.Trim());
            if (!normalized.EndsWith(Path.DirectorySeparatorChar) && !normalized.EndsWith(Path.AltDirectorySeparatorChar))
            {
                normalized += Path.DirectorySeparatorChar;
            }

            return normalized;
        }
        catch
        {
            var sanitized = path.Trim();
            if (!sanitized.EndsWith(Path.DirectorySeparatorChar) && !sanitized.EndsWith(Path.AltDirectorySeparatorChar))
            {
                sanitized += Path.DirectorySeparatorChar;
            }

            return sanitized;
        }
    }
}

public enum AntiSystemWhitelistEntryKind
{
    Directory = 0,
    FileHash = 1,
    ProcessName = 2
}
