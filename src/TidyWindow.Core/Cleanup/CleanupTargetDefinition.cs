using System;

namespace TidyWindow.Core.Cleanup;

internal sealed class CleanupTargetDefinition
{
    public CleanupTargetDefinition(string? classification, string? category, string? path, string? notes)
    {
        Classification = string.IsNullOrWhiteSpace(classification) ? "Other" : classification.Trim();
        Category = string.IsNullOrWhiteSpace(category) ? "Unknown" : category.Trim();
        RawPath = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        Notes = string.IsNullOrWhiteSpace(notes)
            ? "Dry run only. No files were deleted."
            : notes.Trim();
    }

    public string Classification { get; }

    public string Category { get; }

    public string? RawPath { get; }

    public string Notes { get; }

    public CleanupTargetDefinition WithCategory(string category)
    {
        return new CleanupTargetDefinition(Classification, category, RawPath, Notes);
    }

    public CleanupTargetDefinition WithPath(string? path)
    {
        return new CleanupTargetDefinition(Classification, Category, path, Notes);
    }

    public CleanupTargetDefinition WithNotes(string? notes)
    {
        return new CleanupTargetDefinition(Classification, Category, RawPath, notes);
    }
}
