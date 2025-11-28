using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace TidyWindow.Core.Processes;

/// <summary>
/// Immutable snapshot of the Known Processes catalog.
/// </summary>
public sealed record ProcessCatalogSnapshot(
    string SourcePath,
    DateTimeOffset LoadedAtUtc,
    IReadOnlyList<ProcessCatalogCategory> Categories,
    IReadOnlyList<ProcessCatalogEntry> Entries)
{
    public static ProcessCatalogSnapshot Empty { get; } = new(
        string.Empty,
        DateTimeOffset.MinValue,
        ImmutableArray<ProcessCatalogCategory>.Empty,
        ImmutableArray<ProcessCatalogEntry>.Empty);
}

/// <summary>
/// Logical grouping used for catalog tabs and filters.
/// </summary>
public sealed record ProcessCatalogCategory(
    string Key,
    string Name,
    string? Description,
    bool IsCaution,
    int Order);
