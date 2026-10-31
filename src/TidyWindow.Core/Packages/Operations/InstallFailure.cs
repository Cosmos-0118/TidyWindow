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
        [1925] = "administrator rights are required",
        [unchecked((int)0x80073D28)] = "administrator rights are required"
    };

    /// <summary>A short reason for a failed command, or null when the code is not recognized.</summary>
    public static string? Explain(CommandResult result)
    {
        var known = WinGet.GetValueOrDefault(unchecked((uint)result.ExitCode));
        var inner = InnerCode().Match(PackageOutputParser.Clean(result.Output + "\n" + result.Error));
        if (inner.Success && TryParseCode(inner.Groups[1].Value, out var code))
        {
            var meaning = Installer.TryGetValue(code, out var text) ? text : null;
            var shown = inner.Groups[1].Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? inner.Groups[1].Value : code.ToString();
            var reason = $"The installer started but reported error {shown}" +(meaning is null ? "." : $" ({meaning}).");
            return known is null ? reason : known + " " + reason;
        }
        return known;
    }

    private static bool TryParseCode(string value, out int code)
    {
        if (!value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return int.TryParse(value, out code);
        var parsed = uint.TryParse(value.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var bits);
        code = unchecked((int)bits);
        return parsed;
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

    /// <summary>
    /// The installer reported that it needs administrator rights (Store packages that register a service do this, and so do some MSI installers).
    /// WinGet runs unelevated, so it can only report this; asking Windows for permission and trying once more is up to the caller.
    /// </summary>
    public static bool NeedsAdministrator(CommandResult result)
    {
        if (result.ExitCode == 0) return false;
        return AdministratorRequired().IsMatch(PackageOutputParser.Clean(result.Output + "\n" + result.Error));
    }

    /// <summary>Runs a WinGet command. If the installer says it needs administrator rights, tells the person and runs it once more through the Windows permission prompt.</summary>
    public static async Task<CommandResult> RunAsync(ICommandRunner runner, CommandSpec command, string name, CancellationToken token)
    {
        var result = await runner.RunAsync(command, token).ConfigureAwait(false);
        if (command.Elevate || !NeedsAdministrator(result)) return result;
        try { command.Progress?.Report(new($"{name} needs administrator permission. Asking Windows for it…", CommandOutputKind.Notice)); }
        catch { /* A display failure must not stop the retry. */ }
        var elevated = await runner.RunAsync(command with { Elevate = true }, token).ConfigureAwait(false);
        // The elevated window's output cannot be captured, so keep what the first attempt said alongside the new exit code.
        return elevated with { Output = result.Output + "\n" + elevated.Output };
    }

    [GeneratedRegex(@"0x80073d28|administrator privileges (?:are )?required", RegexOptions.IgnoreCase)]
    private static partial Regex AdministratorRequired();

    // Installers report the code in decimal ("exit code: 1603") or, for Store packages, in hex ("exit code: 0x80073d28").
    [GeneratedRegex(@"(?:installer|install) failed with exit code:?\s*(0x[0-9a-f]+|-?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex InnerCode();
}
