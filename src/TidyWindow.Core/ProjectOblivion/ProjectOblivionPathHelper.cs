using System;
using System.IO;

namespace TidyWindow.Core.ProjectOblivion;

public static class ProjectOblivionPathHelper
{
    public static string? NormalizeDirectoryCandidate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(trimmed);

        try
        {
            if (Directory.Exists(expanded))
            {
                return Path.GetFullPath(expanded);
            }

            if (File.Exists(expanded))
            {
                var directory = Path.GetDirectoryName(expanded);
                return string.IsNullOrWhiteSpace(directory) ? null : Path.GetFullPath(directory);
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    public static bool IsHighConfidenceInstallPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = path.Replace('/', '\\').ToLowerInvariant();
        if (normalized.Contains("start menu"))
        {
            return false;
        }

        if (normalized.Contains("\\shortcuts\\"))
        {
            return false;
        }

        return true;
    }
}
