using System;
using System.Collections.Generic;
using System.Text;

namespace TidyWindow.App.Services;

internal static class PulseGuardHeuristics
{
    private static readonly string[] MissingPowerShell7Indicators =
    {
        "pwsh is not recognized",
        "pwsh.exe is not recognized",
        "pwsh is not installed",
        "pwsh.exe was not found",
        "pwsh was not found",
        "could not find pwsh",
        "cannot find pwsh",
        "pwsh is missing",
        "powershell 7 is missing",
        "powershell 7 not installed",
        "powershell 7 is not installed",
        "no powershell 7",
        "no pwsh",
        "pwsh isn't available",
        "pwsh was not recognized"
    };

    private static readonly string[] KnownProcessMissingPhrases =
    {
        "not found",
        "does not exist",
        "cannot find",
        "missing"
    };

    private static readonly string[] LegacyPowerShellIndicators =
    {
        "powershell 5.1",
        "powershell 5",
        "windows powershell 5",
        "using windows powershell",
        "detected powershell 5"
    };

    private static readonly string[] RestartIndicators =
    {
        "restart required",
        "restart recommended",
        "restart tidywindow",
        "restart the app",
        "relaunch tidywindow",
        "re-open tidywindow",
        "reopen tidywindow"
    };

    public static HighFrictionScenario ResolveHighFrictionScenario(ActivityLogEntry entry)
    {
        if (entry.Level is not ActivityLogLevel.Error and not ActivityLogLevel.Warning)
        {
            return HighFrictionScenario.None;
        }

        var text = BuildNormalizedText(entry);
        if (text.Length == 0)
        {
            return HighFrictionScenario.None;
        }

        if (IsPowerShell7Problem(text))
        {
            return HighFrictionScenario.LegacyPowerShell;
        }

        if (IsRestartRequirement(text))
        {
            return HighFrictionScenario.AppRestartRequired;
        }

        return HighFrictionScenario.None;
    }

    public static bool ShouldSuppressNotification(ActivityLogEntry entry)
    {
        if (IsNavigationNoise(entry))
        {
            return true;
        }

        if (IsKnownProcessesMissingServiceWarning(entry))
        {
            return true;
        }

        if (IsThreatWatchPassiveScanEntry(entry))
        {
            return true;
        }

        return false;
    }

    public static PulseGuardNotificationKind ResolveKind(ActivityLogEntry entry)
    {
        return entry.Level switch
        {
            ActivityLogLevel.Success => PulseGuardNotificationKind.SuccessDigest,
            ActivityLogLevel.Warning => PulseGuardNotificationKind.Insight,
            ActivityLogLevel.Error => PulseGuardNotificationKind.ActionRequired,
            _ => PulseGuardNotificationKind.Insight
        };
    }

    public static string BuildNormalizedText(ActivityLogEntry entry)
    {
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(entry.Message))
        {
            builder.Append(entry.Message).Append(' ');
        }

        foreach (var detail in entry.Details)
        {
            if (!string.IsNullOrWhiteSpace(detail))
            {
                builder.Append(detail).Append(' ');
            }
        }

        return builder.ToString().Trim().ToLowerInvariant();
    }

    private static bool IsPowerShell7Problem(string text)
    {
        if (!ContainsPowerShellToken(text))
        {
            return false;
        }

        if (ContainsAny(text, LegacyPowerShellIndicators))
        {
            return true;
        }

        if (ContainsMissingPwsh(text))
        {
            return true;
        }

        if (ContainsRequiresSeven(text))
        {
            return true;
        }

        if (ContainsUpgradeToSeven(text))
        {
            return true;
        }

        return false;
    }

    private static bool ContainsPowerShellToken(string text)
    {
        return text.Contains("powershell", StringComparison.Ordinal)
            || text.Contains("pwsh", StringComparison.Ordinal);
    }

    private static bool ContainsMissingPwsh(string text)
    {
        if (!text.Contains("pwsh", StringComparison.Ordinal) && !text.Contains("powershell 7", StringComparison.Ordinal))
        {
            return false;
        }

        if (ContainsAny(text, MissingPowerShell7Indicators))
        {
            return true;
        }

        return text.Contains("install powershell 7", StringComparison.Ordinal)
            || text.Contains("install pwsh", StringComparison.Ordinal)
            || text.Contains("download powershell 7", StringComparison.Ordinal)
            || text.Contains("missing powershell 7", StringComparison.Ordinal)
            || text.Contains("powershell 7 is required", StringComparison.Ordinal);
    }

    private static bool ContainsRequiresSeven(string text)
    {
        if (!text.Contains("powershell", StringComparison.Ordinal) && !text.Contains("pwsh", StringComparison.Ordinal))
        {
            return false;
        }

        return text.Contains("requires powershell 7", StringComparison.Ordinal)
            || text.Contains("require powershell 7", StringComparison.Ordinal)
            || text.Contains("needs powershell 7", StringComparison.Ordinal)
            || text.Contains("needs pwsh", StringComparison.Ordinal)
            || text.Contains("minimum powershell 7", StringComparison.Ordinal)
            || text.Contains("requires pwsh", StringComparison.Ordinal)
            || text.Contains("requires version 7", StringComparison.Ordinal);
    }

    private static bool ContainsUpgradeToSeven(string text)
    {
        if (!text.Contains("powershell", StringComparison.Ordinal))
        {
            return false;
        }

        return text.Contains("upgrade to powershell 7", StringComparison.Ordinal)
            || text.Contains("upgrade powershell 7", StringComparison.Ordinal)
            || text.Contains("update to powershell 7", StringComparison.Ordinal)
            || text.Contains("update powershell 7", StringComparison.Ordinal)
            || text.Contains("upgrade pwsh", StringComparison.Ordinal)
            || text.Contains("update pwsh", StringComparison.Ordinal);
    }

    private static bool IsRestartRequirement(string text)
    {
        if (ContainsAny(text, RestartIndicators))
        {
            return true;
        }

        if (text.Contains("restart required", StringComparison.Ordinal)
            || text.Contains("please restart", StringComparison.Ordinal)
            || text.Contains("restart to continue", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static bool IsNavigationNoise(ActivityLogEntry entry)
    {
        if (string.Equals(entry.Source, "Navigation", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(entry.Message) && entry.Message.StartsWith("navigating to", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool IsKnownProcessesMissingServiceWarning(ActivityLogEntry entry)
    {
        if (entry.Level != ActivityLogLevel.Warning)
        {
            return false;
        }

        if (!string.Equals(entry.Source, "Known Processes", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (ContainsMissingServiceLanguage(entry.Message))
        {
            return true;
        }

        foreach (var detail in entry.Details)
        {
            if (ContainsMissingServiceLanguage(detail))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsMissingServiceLanguage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var phrase in KnownProcessMissingPhrases)
        {
            if (text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsThreatWatchPassiveScanEntry(ActivityLogEntry entry)
    {
        if (!string.Equals(entry.Source, "Threat Watch", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return entry.Level is ActivityLogLevel.Information or ActivityLogLevel.Success;
    }

    private static bool ContainsAny(string text, IEnumerable<string> phrases)
    {
        foreach (var phrase in phrases)
        {
            if (text.Contains(phrase, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
