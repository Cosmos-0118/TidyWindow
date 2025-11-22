using System;
using System.IO;

namespace TidyWindow.App.Services;

internal static class BrowserHistoryHelper
{
    private static readonly string EdgeUserDataRoot = BuildEdgeUserDataRoot();

    public static bool TryGetEdgeProfileDirectory(string? candidatePath, out string profileDirectory)
    {
        profileDirectory = string.Empty;
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(EdgeUserDataRoot))
        {
            return false;
        }

        if (!candidatePath.StartsWith(EdgeUserDataRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var relative = candidatePath.Substring(EdgeUserDataRoot.Length)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(relative))
        {
            return false;
        }

        var separatorIndex = relative.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        if (separatorIndex <= 0)
        {
            return false;
        }

        var profileSegment = relative[..separatorIndex];
        if (string.IsNullOrWhiteSpace(profileSegment))
        {
            return false;
        }

        var candidate = Path.Combine(EdgeUserDataRoot, profileSegment);
        if (!Directory.Exists(candidate))
        {
            return false;
        }

        profileDirectory = candidate;
        return true;
    }

    private static string BuildEdgeUserDataRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            return string.Empty;
        }

        return Path.Combine(localAppData, "Microsoft", "Edge", "User Data");
    }
}
