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
        bool dryRun = true)
    {
        Category = string.IsNullOrWhiteSpace(category) ? "Unknown" : category;
        Path = path ?? string.Empty;
        Exists = exists;
        ItemCount = itemCount < 0 ? 0 : itemCount;
        TotalSizeBytes = totalSizeBytes < 0 ? 0 : totalSizeBytes;
        Preview = preview ?? Array.Empty<CleanupPreviewItem>();
        Notes = notes ?? string.Empty;
        DryRun = dryRun;
    }

    public string Category { get; }

    public string Path { get; }

    public bool Exists { get; }

    public int ItemCount { get; }

    public long TotalSizeBytes { get; }

    public bool DryRun { get; }

    public IReadOnlyList<CleanupPreviewItem> Preview { get; }

    public string Notes { get; }

    public double TotalSizeMegabytes => TotalSizeBytes / 1_048_576d;
}

public sealed class CleanupPreviewItem
{
    public CleanupPreviewItem(string? name, string? fullName, long sizeBytes, DateTime? lastModifiedUtc)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "(unknown)" : name;
        FullName = fullName ?? string.Empty;
        SizeBytes = sizeBytes < 0 ? 0 : sizeBytes;
        LastModifiedUtc = lastModifiedUtc ?? DateTime.MinValue;
    }

    public string Name { get; }

    public string FullName { get; }

    public long SizeBytes { get; }

    public DateTime LastModifiedUtc { get; }

    public double SizeMegabytes => SizeBytes / 1_048_576d;
}
