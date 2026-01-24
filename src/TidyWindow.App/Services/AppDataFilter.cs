using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TidyWindow.App.Services;

/// <summary>
/// Filters app data roots to exclude obvious cache/temp/log folders before backup.
/// </summary>
public static class AppDataFilter
{
    private static readonly HashSet<string> ExcludedFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cache",
        "caches",
        "temp",
        "tmp",
        "logs",
        "log",
        "crashpad",
        "code cache",
        "gpucache",
        "gpu cache",
        "service worker",
        "shadercache",
        "shader cache",
        "indexeddb",
        "blob_storage",
        "local storage",
        "webviewcache",
        "appcache"
    };

    public static IReadOnlyList<string> FilterUsefulPaths(IEnumerable<string> paths)
    {
        if (paths is null)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var normalized = Normalize(raw);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            var leaf = Path.GetFileName(normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (IsExcludedFolder(leaf))
            {
                continue;
            }

            if (seen.Add(normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    private static bool IsExcludedFolder(string? folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return false;
        }

        return ExcludedFolderNames.Contains(folderName);
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        }
        catch
        {
            return string.Empty;
        }
    }
}
