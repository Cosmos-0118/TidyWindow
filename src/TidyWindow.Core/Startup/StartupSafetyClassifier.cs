using System;
using System.Collections.Concurrent;
using System.IO;

namespace TidyWindow.Core.Startup;

/// <summary>
/// Centralized classification helpers for startup entries.
/// Keeps UI + control paths consistent so we can avoid accidental disabling of system-critical items.
/// </summary>
public static class StartupSafetyClassifier
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, ClassificationCacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record ClassificationCacheEntry(bool IsSystemCritical, bool IsSafeToDisable, DateTimeOffset CachedAt);

    private static class RuleSets
    {
        public static readonly string[] SystemPathMarkers =
        {
            "\\program files\\windows defender",
            "\\program files\\windows security",
            "\\program files\\common files\\microsoft shared"
        };

        public static readonly string[] SecurityNameMarkers =
        {
            "defender",
            "security",
            "antimal",
            "msmpeng",
            "sense"
        };

        public static readonly string[] DriverVendorMarkers =
        {
            "intel",
            "advanced micro devices",
            "amd",
            "nvidia",
            "realtek",
            "qualcomm",
            "mediatek"
        };
    }

    /// <summary>
    /// Returns true when the startup entry looks safe to disable for typical users.
    /// System-critical entries are always excluded.
    /// </summary>
    public static bool IsSafeToDisable(StartupItem item)
    {
        var classification = Classify(item);
        return classification.IsSafeToDisable;
    }

    /// <summary>
    /// Returns true when the startup entry looks core to Windows / drivers / security,
    /// where disabling is likely to break the system or materially reduce protection.
    /// </summary>
    public static bool IsSystemCritical(StartupItem item)
    {
        var classification = Classify(item);
        return classification.IsSystemCritical;
    }

    private static ClassificationCacheEntry Classify(StartupItem? item)
    {
        if (item is null)
        {
            return new ClassificationCacheEntry(false, false, DateTimeOffset.UtcNow);
        }

        var key = item.Id ?? string.Empty;
        var now = DateTimeOffset.UtcNow;

        if (!string.IsNullOrEmpty(key)
            && Cache.TryGetValue(key, out var cached)
            && now - cached.CachedAt < CacheTtl)
        {
            return cached;
        }

        var isSystemCritical = ComputeIsSystemCriticalCore(item);
        var isSafeToDisable = !isSystemCritical && ComputeIsSafeToDisableCore(item);
        var fresh = new ClassificationCacheEntry(isSystemCritical, isSafeToDisable, now);

        if (!string.IsNullOrEmpty(key))
        {
            Cache[key] = fresh;
        }

        return fresh;
    }

    private static bool ComputeIsSafeToDisableCore(StartupItem item)
    {
        if (item is null)
        {
            return false;
        }

        // "Safe to disable" is meant to be conservative but useful in practice.
        // We prioritize user-scope entries that live in typical user/app install locations and are not system-critical.
        var isUserScope = string.Equals(item.UserContext, "CurrentUser", StringComparison.OrdinalIgnoreCase);
        var isSupportedSource = item.SourceKind is StartupItemSourceKind.RunKey or StartupItemSourceKind.RunOnce or StartupItemSourceKind.StartupFolder;
        if (!isUserScope || !isSupportedSource)
        {
            return false;
        }

        var publisher = (item.Publisher ?? string.Empty).Trim();
        var exePath = NormalizePath(item.ExecutablePath);

        // Avoid treating network or unknown paths as "safe" by default.
        if (exePath.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // If it lives under the user's profile / appdata, it's generally an app entry (still could be malware,
        // but that's a different concern than "don't break Windows").
        if (IsUnderUserProfile(exePath))
        {
            return true;
        }

        // Trusted signature is a strong signal even if the path is Program Files.
        if (item.SignatureStatus == StartupSignatureStatus.SignedTrusted)
        {
            return true;
        }

        // If it's clearly third-party (not Microsoft) and not in Windows paths, treat as reasonably safe.
        if (!IsMicrosoftPublisher(publisher) && !IsUnderSystemPath(exePath))
        {
            return true;
        }

        // Default: not safe.
        return false;
    }

    private static bool ComputeIsSystemCriticalCore(StartupItem item)
    {
        if (item is null)
        {
            return false;
        }

        var publisher = (item.Publisher ?? string.Empty).Trim();
        var exePath = NormalizePath(item.ExecutablePath);

        // Anything running directly from core Windows folders is considered critical.
        if (!string.IsNullOrWhiteSpace(exePath) && IsUnderSystemPath(exePath))
        {
            return true;
        }

        // Microsoft security components are sensitive even when installed under Program Files.
        if (IsMicrosoftPublisher(publisher) && LooksLikeSecurityComponent(exePath))
        {
            return true;
        }

        // Services: only treat as critical when they are clearly OS / driver / security components.
        if (item.SourceKind == StartupItemSourceKind.Service)
        {
            var isMachineScope = !string.Equals(item.UserContext, "CurrentUser", StringComparison.OrdinalIgnoreCase);
            if (!isMachineScope)
            {
                return false;
            }

            if (IsMicrosoftPublisher(publisher)
                || IsDriverVendor(publisher)
                || LooksLikeSecurityComponent(exePath)
                || IsUnderSystemPath(exePath))
            {
                return true;
            }

            return false;
        }

        // Machine scheduled tasks that point into Windows folders and are from Microsoft are typically OS tasks.
        if (item.SourceKind == StartupItemSourceKind.ScheduledTask
            && !string.Equals(item.UserContext, "CurrentUser", StringComparison.OrdinalIgnoreCase)
            && IsMicrosoftPublisher(publisher)
            && IsUnderSystemPath(exePath))
        {
            return true;
        }

        return false;
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch
        {
            return path.Trim().Trim('"');
        }
    }

    private static bool IsUnderSystemPath(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }

        var normalized = exePath.Replace('/', '\\');

        // Important OS locations.
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
        if (!string.IsNullOrWhiteSpace(windows)
            && normalized.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // These are outside %WINDIR% but still OS/security critical.
        foreach (var marker in RuleSets.SystemPathMarkers)
        {
            if (normalized.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnderUserProfile(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }

        var normalized = exePath.Replace('/', '\\');

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
        if (!string.IsNullOrWhiteSpace(userProfile)
            && normalized.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).TrimEnd('\\');
        if (!string.IsNullOrWhiteSpace(localAppData)
            && normalized.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData).TrimEnd('\\');
        if (!string.IsNullOrWhiteSpace(roaming)
            && normalized.StartsWith(roaming, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool IsMicrosoftPublisher(string publisher)
        => publisher.Contains("microsoft", StringComparison.OrdinalIgnoreCase);

    private static bool IsDriverVendor(string publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher))
        {
            return false;
        }

        foreach (var marker in RuleSets.DriverVendorMarkers)
        {
            if (publisher.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeSecurityComponent(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }

        foreach (var marker in RuleSets.SecurityNameMarkers)
        {
            if (exePath.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

