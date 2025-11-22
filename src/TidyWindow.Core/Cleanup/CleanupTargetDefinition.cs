using System;

namespace TidyWindow.Core.Cleanup;

internal enum CleanupTargetType
{
    Directory,
    File
}

internal sealed class CleanupTargetDefinition
{
    public CleanupTargetDefinition(string? classification, string? category, string? path, string? notes, CleanupTargetType targetType = CleanupTargetType.Directory)
    {
        Classification = string.IsNullOrWhiteSpace(classification) ? "Other" : classification.Trim();
        Category = string.IsNullOrWhiteSpace(category) ? "Unknown" : category.Trim();
        RawPath = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        Notes = string.IsNullOrWhiteSpace(notes)
            ? "Dry run only. No files were deleted."
            : notes.Trim();
        TargetType = targetType;
    }

    public string Classification { get; }

    public string Category { get; }

    public string? RawPath { get; }

    public string Notes { get; }

    public CleanupTargetType TargetType { get; }

    public CleanupTargetDefinition WithCategory(string category)
    {
        return new CleanupTargetDefinition(Classification, category, RawPath, Notes, TargetType);
    }

    public CleanupTargetDefinition WithPath(string? path)
    {
        return new CleanupTargetDefinition(Classification, Category, path, Notes, TargetType);
    }

    public CleanupTargetDefinition WithNotes(string? notes)
    {
        return new CleanupTargetDefinition(Classification, Category, RawPath, notes, TargetType);
    }

    public CleanupTargetDefinition WithTargetType(CleanupTargetType targetType)
    {
        return new CleanupTargetDefinition(Classification, Category, RawPath, Notes, targetType);
    }
}
