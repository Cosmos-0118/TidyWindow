using System;

namespace TidyWindow.Core.Processes;

/// <summary>
/// Captures the persisted preference (Keep vs. Auto-stop) for a specific process.
/// </summary>
public sealed record ProcessPreference
{
    public ProcessPreference(
        string processIdentifier,
        ProcessActionPreference action,
        ProcessPreferenceSource source,
        DateTimeOffset updatedAtUtc,
        string? notes,
        string? serviceIdentifier = null)
    {
        if (string.IsNullOrWhiteSpace(processIdentifier))
        {
            throw new ArgumentException("Process identifier must be provided.", nameof(processIdentifier));
        }

        ProcessIdentifier = NormalizeProcessIdentifier(processIdentifier);
        Action = action;
        Source = source;
        UpdatedAtUtc = updatedAtUtc;
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();

        if (!string.IsNullOrWhiteSpace(serviceIdentifier))
        {
            var normalizedService = ProcessCatalogEntry.NormalizeServiceIdentifier(serviceIdentifier);
            if (normalizedService is null)
            {
                throw new ArgumentException("Service identifier is invalid.", nameof(serviceIdentifier));
            }

            ServiceIdentifier = normalizedService;
        }
    }

    public string ProcessIdentifier { get; init; }

    public ProcessActionPreference Action { get; init; }

    public ProcessPreferenceSource Source { get; init; }

    public DateTimeOffset UpdatedAtUtc { get; init; }

    public string? Notes { get; init; }

    public string? ServiceIdentifier { get; init; }

    public static string NormalizeProcessIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Trim().ToLowerInvariant();
    }
}
