using System;
using System.Collections.Generic;
using TidyWindow.App.ViewModels;
using TidyWindow.Core.Cleanup;

namespace TidyWindow.App.ViewModels.Preview;

public sealed class CleanupPreviewFilter : IPreviewFilter
{
    private readonly HashSet<string> _activeExtensions = new(StringComparer.OrdinalIgnoreCase);

    public CleanupItemKind SelectedItemKind { get; set; } = CleanupItemKind.Both;

    public CleanupExtensionFilterMode ExtensionFilterMode { get; set; } = CleanupExtensionFilterMode.None;

    public DateTime? MinimumAgeThresholdUtc { get; set; }

    public void SetActiveExtensions(IEnumerable<string> extensions)
    {
        _activeExtensions.Clear();
        if (extensions is null)
        {
            return;
        }

        foreach (var extension in extensions)
        {
            var normalized = NormalizeExtension(extension);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                _activeExtensions.Add(normalized);
            }
        }
    }

    public bool Matches(CleanupPreviewItemViewModel item)
    {
        if (item is null)
        {
            return false;
        }

        var isHistoryItem = string.Equals(item.Classification, "History", StringComparison.OrdinalIgnoreCase);

        switch (SelectedItemKind)
        {
            case CleanupItemKind.Files when item.IsDirectory:
                return false;
            case CleanupItemKind.Folders when !item.IsDirectory && !isHistoryItem:
                return false;
        }

        if (MinimumAgeThresholdUtc is { } thresholdUtc && !isHistoryItem)
        {
            var lastModifiedUtc = item.Model.LastModifiedUtc;
            if (lastModifiedUtc != DateTime.MinValue && lastModifiedUtc > thresholdUtc)
            {
                return false;
            }
        }

        if (item.IsDirectory)
        {
            if (SelectedItemKind == CleanupItemKind.Files)
            {
                return false;
            }

            if (ExtensionFilterMode == CleanupExtensionFilterMode.IncludeOnly && _activeExtensions.Count > 0)
            {
                return false;
            }

            return true;
        }

        if (ExtensionFilterMode == CleanupExtensionFilterMode.None || _activeExtensions.Count == 0)
        {
            return true;
        }

        var extension = NormalizeExtension(item.Extension);
        return ExtensionFilterMode switch
        {
            CleanupExtensionFilterMode.IncludeOnly => _activeExtensions.Contains(extension),
            CleanupExtensionFilterMode.Exclude => !_activeExtensions.Contains(extension),
            _ => true
        };
    }

    internal static string NormalizeExtension(string? extension)
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
