using System;

namespace TidyWindow.App.Services.Cleanup;

public sealed class CleanupSuggestion
{
    public CleanupSuggestion(string id, CleanupSuggestionKind kind, string path, string title, string description, bool isSafe)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Kind = kind;
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Title = title ?? throw new ArgumentNullException(nameof(title));
        Description = description ?? string.Empty;
        IsSafe = isSafe;
    }

    public string Id { get; }

    public CleanupSuggestionKind Kind { get; }

    public string Path { get; }

    public string Title { get; }

    public string Description { get; }

    public bool IsSafe { get; }
}
