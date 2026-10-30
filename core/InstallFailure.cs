using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

/// <summary>Turns package manager exit codes into a reason a person can act on, and recognizes the few that are not failures.</summary>
public static partial class InstallFailure
{
    // WinGet's documented return codes (winget-cli doc/windows/package-manager/winget/returnCodes.md).
    private static readonly Dictionary<uint, string> WinGet = new()
    {
        [0x8A150101] = "The app is open. Close it and try again.",
        [0x8A150102] = "Another installation is running. Try again when it finishes.",
        [0x8A150103] = "Some of the app’s files are in use. Close the app and try again.",
        [0x8A150104] = "A required dependency is missing from this computer.",
        [0x8A150105] = "The disk is out of space. Free some space and try again.",
        [0x8A150106] = "There isn’t enough memory. Close other apps and try again.",
        [0x8A150107] = "The installer needs an internet connection.",
        [0x8A15010A] = "The installation needs a restart before it can be retried.",
        [0x8A15010C] = "The installation was cancelled.",
        [0x8A15010D] = "A different version of this app is already installed.",
        [0x8A15010E] = "A newer version of this app is already installed.",
        [0x8A15010F] = "Your organization’s policy blocks this installation.",
        [0x8A150110] = "A dependency of this app could not be installed.",
        [0x8A150113] = "This app is not supported on this computer.",
        [0x8A15003A] = "Group Policy blocks this operation.",
        [0x8A15008E] = "WinGet can’t update this app because the newer version installs a different way. Update it from the app itself.",
        [0x8A150049] = "The MSI installer failed."
    };

    // Common Windows Installer / installer-process codes.
    private static readonly Dictionary<int, string> Installer = new()
    {
        [1] = "an unspecified error",
        [2] = "a missing file or bad option",
        [5] = "access was denied",
        [1223] = "the administrator prompt was declined",
        [1602] = "the installation was cancelled",
        [1603] = "a fatal error during installation",
        [1618] = "another installation is already in progress",
        [1638] = "another version of this app is already installed",
        [1925] = "administrator rights are required"
    };

    /// <summary>A short reason for a failed command, or null when the code is not recognized.</summary>
    public static string? Explain(CommandResult result)
    {
        var known = WinGet.GetValueOrDefault(unchecked((uint)result.ExitCode));
        var inner = InnerCode().Match(PackageOutputParser.Clean(result.Output + "\n" + result.Error));
        if (inner.Success && int.TryParse(inner.Groups[1].Value, out var code))
        {
            var meaning = Installer.TryGetValue(code, out var text) ? text : null;
            var reason = $"The installer started but reported error {code}" + (meaning is null ? "." : $" ({meaning}).");
            return known is null ? reason : known + " " + reason;
        }
        return known;
    }

    /// <summary>Codes that mean the app is present or will be after a restart, so they are not errors.</summary>
    public static ActionOutcome? NotAFailure(int exitCode) => unchecked((uint)exitCode) switch
    {
        0x8A15010D or 0x8A15010E => ActionOutcome.Completed,
        0x8A150109 or 0x8A15010B => ActionOutcome.RestartRequired,
        _ => null
    };

    /// <summary>WinGet lists the update but can never apply it, so retrying or re-listing it is pointless.</summary>
    public static bool UpdateCannotBeApplied(int exitCode) => unchecked((uint)exitCode) == 0x8A15008E;

    [GeneratedRegex(@"(?:installer|install) failed with exit code:?\s*(-?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex InnerCode();
}
