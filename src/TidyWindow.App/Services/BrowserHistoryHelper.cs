using System;
using System.IO;

namespace TidyWindow.App.Services;

internal static class BrowserHistoryHelper
{
    private static readonly string EdgeUserDataRoot = BuildEdgeUserDataRoot();
    private static readonly string ChromeUserDataRoot = BuildChromeUserDataRoot();

    public static bool TryGetBrowserProfile(string? candidatePath, out BrowserProfile profile)
    {
        profile = default;

        if (TryGetProfile(candidatePath, EdgeUserDataRoot, profileFilter: null, out var edgeProfile))
        {
            profile = new BrowserProfile(BrowserKind.Edge, edgeProfile);
            return true;
        }

        if (TryGetProfile(candidatePath, ChromeUserDataRoot, IsChromeProfile, out var chromeProfile))
        {
            profile = new BrowserProfile(BrowserKind.Chrome, chromeProfile);
            return true;
        }

        return false;
    }

    private static bool TryGetProfile(string? candidatePath, string root, Func<string, bool>? profileFilter, out string profileDirectory)
    {
        profileDirectory = string.Empty;

        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        if (!candidatePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var relative = candidatePath.Substring(root.Length)
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

        if (profileFilter is not null && !profileFilter(profileSegment))
        {
            return false;
        }

        var candidate = Path.Combine(root, profileSegment);
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

    private static string BuildChromeUserDataRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            return string.Empty;
        }

        return Path.Combine(localAppData, "Google", "Chrome", "User Data");
    }

    private static bool IsChromeProfile(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.StartsWith("Guest Profile", StringComparison.OrdinalIgnoreCase);
    }
}
