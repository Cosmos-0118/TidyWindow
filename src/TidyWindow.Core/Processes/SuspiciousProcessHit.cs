using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace TidyWindow.Core.Processes;

/// <summary>
/// Represents a single detection emitted by the Threat Watch service.
/// </summary>
public sealed record SuspiciousProcessHit
{
    public SuspiciousProcessHit(
        string id,
        string processName,
        string filePath,
        SuspicionLevel level,
        IEnumerable<string>? matchedRules,
        DateTimeOffset observedAtUtc,
        string? hash,
        string? source,
        string? notes)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Identifier must be provided.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(processName))
        {
            throw new ArgumentException("Process name must be provided.", nameof(processName));
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path must be provided.", nameof(filePath));
        }

        Id = NormalizeId(id);
        ProcessName = processName.Trim();
        FilePath = filePath.Trim();
        Level = level;
        MatchedRules = matchedRules is null
            ? ImmutableArray<string>.Empty
            : matchedRules
                .Where(static rule => !string.IsNullOrWhiteSpace(rule))
                .Select(static rule => rule.Trim())
                .ToImmutableArray();
        ObservedAtUtc = observedAtUtc == default ? DateTimeOffset.UtcNow : observedAtUtc;
        Hash = string.IsNullOrWhiteSpace(hash) ? null : hash.Trim();
        Source = string.IsNullOrWhiteSpace(source) ? null : source.Trim();
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
    }

    public string Id { get; init; }

    public string ProcessName { get; init; }

    public string FilePath { get; init; }

    public SuspicionLevel Level { get; init; }

    public IReadOnlyList<string> MatchedRules { get; init; }

    public DateTimeOffset ObservedAtUtc { get; init; }

    public string? Hash { get; init; }

    public string? Source { get; init; }

    public string? Notes { get; init; }

    public static string NormalizeId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Trim().ToLowerInvariant();
    }
}
