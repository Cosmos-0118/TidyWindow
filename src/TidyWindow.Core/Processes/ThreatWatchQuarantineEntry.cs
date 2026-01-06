using System;
using System.IO;
using TidyWindow.Core.Processes.ThreatWatch;

namespace TidyWindow.Core.Processes;

/// <summary>
/// Represents a persisted quarantine record captured by Threat Watch actions.
/// </summary>
public sealed record ThreatWatchQuarantineEntry
{
    public ThreatWatchQuarantineEntry(
        string id,
        string processName,
        string filePath,
        string? notes,
        string? addedBy,
        DateTimeOffset quarantinedAtUtc,
        ThreatIntelVerdict? verdict,
        string? verdictSource,
        string? verdictDetails,
        string? sha256)
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
        Verdict = verdict;
        VerdictSource = string.IsNullOrWhiteSpace(verdictSource) ? null : verdictSource.Trim();
        VerdictDetails = string.IsNullOrWhiteSpace(verdictDetails) ? null : verdictDetails.Trim();
        Sha256 = string.IsNullOrWhiteSpace(sha256) ? null : sha256.Trim();
    }

    public string Id { get; init; }

    public string ProcessName { get; init; }

    public string FilePath { get; init; }

    public string? Notes { get; init; }

    public string? AddedBy { get; init; }

    public DateTimeOffset QuarantinedAtUtc { get; init; }

    public ThreatIntelVerdict? Verdict { get; init; }

    public string? VerdictSource { get; init; }

    public string? VerdictDetails { get; init; }

    public string? Sha256 { get; init; }

    public static ThreatWatchQuarantineEntry Create(
        string processName,
        string filePath,
        string? notes = null,
        string? addedBy = null,
        DateTimeOffset? quarantinedAtUtc = null,
        ThreatIntelVerdict? verdict = null,
        string? verdictSource = null,
        string? verdictDetails = null,
        string? sha256 = null)
    {
        return new ThreatWatchQuarantineEntry(
            id: string.Empty,
            processName,
            filePath,
            notes,
            addedBy,
            quarantinedAtUtc ?? DateTimeOffset.UtcNow,
            verdict,
            verdictSource,
            verdictDetails,
            sha256);
    }

    public ThreatWatchQuarantineEntry Normalize()
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
