using System;
using System.Collections.Generic;
using System.Linq;

namespace TidyWindow.Core.Cleanup;

public sealed class CleanupReport
{
    public static CleanupReport Empty { get; } = new(Array.Empty<CleanupTargetReport>());

    public CleanupReport(IReadOnlyList<CleanupTargetReport>? targets)
    {
        Targets = targets ?? Array.Empty<CleanupTargetReport>();
    }

    public IReadOnlyList<CleanupTargetReport> Targets { get; }

    public long TotalSizeBytes => Targets.Sum(static t => t.TotalSizeBytes);

    public int TotalItemCount => Targets.Sum(static t => t.ItemCount);

    public double TotalSizeMegabytes => TotalSizeBytes / 1_048_576d;
}

public sealed class CleanupTargetReport
{
    public CleanupTargetReport(
        string? category,
        string? path,
        bool exists,
        int itemCount,
        long totalSizeBytes,
        IReadOnlyList<CleanupPreviewItem>? preview,
        string? notes = null,
        bool dryRun = true,
        string? classification = null,
        IReadOnlyList<string>? warnings = null)
    {
        Category = string.IsNullOrWhiteSpace(category) ? "Unknown" : category;
        Path = path ?? string.Empty;
        Exists = exists;
        ItemCount = itemCount < 0 ? 0 : itemCount;
        TotalSizeBytes = totalSizeBytes < 0 ? 0 : totalSizeBytes;
        Preview = preview ?? Array.Empty<CleanupPreviewItem>();
        Notes = notes ?? string.Empty;
        DryRun = dryRun;
        Classification = string.IsNullOrWhiteSpace(classification) ? "Other" : classification;
        Warnings = warnings is null
            ? Array.Empty<string>()
            : warnings.Where(static warning => !string.IsNullOrWhiteSpace(warning))
                .Select(static warning => warning.Trim())
                .ToArray();
    }

    public string Category { get; }

    public string Path { get; }

    public string Classification { get; }

    public bool Exists { get; }

    public int ItemCount { get; }

    public long TotalSizeBytes { get; }

    public bool DryRun { get; }

    public IReadOnlyList<CleanupPreviewItem> Preview { get; }

    public string Notes { get; }

    public IReadOnlyList<string> Warnings { get; }

    public bool HasWarnings => Warnings.Count > 0;

    public double TotalSizeMegabytes => TotalSizeBytes / 1_048_576d;
}

public sealed class CleanupPreviewItem
{
    public CleanupPreviewItem(
        string? name,
        string? fullName,
        long sizeBytes,
        DateTime? lastModifiedUtc,
        bool isDirectory,
        string? extension,
        bool isHidden = false,
        bool isSystem = false,
        bool wasModifiedRecently = false,
        double confidence = 0d,
        IReadOnlyList<string>? signals = null,
        DateTime? lastAccessUtc = null,
        DateTime? creationUtc = null)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "(unknown)" : name;
        FullName = fullName ?? string.Empty;
        SizeBytes = sizeBytes < 0 ? 0 : sizeBytes;
        LastModifiedUtc = lastModifiedUtc ?? DateTime.MinValue;
        IsDirectory = isDirectory;
        Extension = NormalizeExtension(extension);
        IsHidden = isHidden;
        IsSystem = isSystem;
        WasModifiedRecently = wasModifiedRecently;
        Confidence = double.IsNaN(confidence) ? 0d : Math.Clamp(confidence, 0d, 1d);
        LastAccessUtc = lastAccessUtc ?? DateTime.MinValue;
        CreationUtc = creationUtc ?? DateTime.MinValue;
        Signals = signals is null
            ? Array.Empty<string>()
            : signals.Where(static signal => !string.IsNullOrWhiteSpace(signal))
                .Select(static signal => signal.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }

    public string Name { get; }

    public string FullName { get; }

    public long SizeBytes { get; }

    public DateTime LastModifiedUtc { get; }

    public DateTime LastAccessUtc { get; }

    public DateTime CreationUtc { get; }

    public bool IsDirectory { get; }

    public string Extension { get; }

    public bool IsHidden { get; }

    public bool IsSystem { get; }

    public bool WasModifiedRecently { get; }

    public double Confidence { get; }

    public IReadOnlyList<string> Signals { get; }

    public bool HasSignals => Signals.Count > 0;

    public double SizeMegabytes => SizeBytes / 1_048_576d;

    private static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        var trimmed = extension.Trim();
        if (!trimmed.StartsWith(".", StringComparison.Ordinal))
        {
            trimmed = "." + trimmed;
        }

        return trimmed.ToLowerInvariant();
    }
}
