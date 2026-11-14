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
        [32] = "a file is in use by another program",
        [1602] = "the installation was cancelled",
        [1603] = "a fatal error during installation",
        [1605] = "Windows Installer no longer has this app registered",
        [1612] = "Windows Installer’s saved copy of the app’s installer is missing",
        [1614] = "Windows Installer already has this product marked as uninstalled",
        [1618] = "another installation is already in progress",
        [1638] = "another version of this app is already installed",
        [1714] = "an older update’s installer file is missing",
        [1925] = "administrator rights are required",
        [unchecked((int)0x80070005)] = "access was denied",
        [unchecked((int)0x80073D28)] = "administrator rights are required"
    };

    /// <summary>A short reason for a failed command, or null when the code is not recognized.</summary>
    public static string? Explain(CommandResult result, bool uninstall = false)
    {
        var known = WinGet.GetValueOrDefault(unchecked((uint)result.ExitCode));
        var inner = InnerCode().Match(PackageOutputParser.Clean(result.Output + "\n" + result.Error));
        if (inner.Success && TryParseCode(inner.Groups[1].Value, out var code))
        {
            var meaning = Installer.TryGetValue(code, out var text) ? text : null;
            var shown = inner.Groups[1].Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? inner.Groups[1].Value : code.ToString();
            var reason = $"The {(uninstall ? "uninstaller" : "installer")} started but reported error {shown}" + (meaning is null ? "." : $" ({meaning}).")
                + (meaning is null && uninstall ? " That is the app’s own uninstaller failing, not WinGet: it often needs its own window, or the app is still open." : "");
            return known is null ? reason : known + " " + reason;
        }
        return MultipleVersions(result)
            ? "More than one version of this app is installed, and WinGet won’t choose between them."
            : known;
    }

    /// <summary>
    /// WinGet refused to uninstall because several installed versions match and no <c>--version</c> or <c>--all-versions</c> was given. It says so before any uninstaller runs.
    /// The sentence is translated, the two switch names are not, so they are what is matched.
    /// </summary>
    public static bool MultipleVersions(CommandResult result)
    {
        if (result.ExitCode == 0) return false;
        var text = PackageOutputParser.Clean(result.Output + "\n" + result.Error);
        return text.Contains("--all-versions", StringComparison.OrdinalIgnoreCase) && text.Contains("--version", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Multiple versions of this package are installed", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The code the installer or uninstaller itself reported, as WinGet prints it, or null when the output does not say.</summary>
    public static int? InnerExitCode(CommandResult result) =>
        InnerCode().Match(PackageOutputParser.Clean(result.Output + "\n" + result.Error)) is { Success: true } match && TryParseCode(match.Groups[1].Value, out var code) ? code : null;

    /// <summary>The code Chocolatey prints when an installer it ran failed: <c>Exit code was '1618'</c>.</summary>
    public static int? ManagerExitCode(CommandResult result) =>
        ChocolateyCode().Match(PackageOutputParser.Clean(result.Output + "\n" + result.Error)) is { Success: true } match && int.TryParse(match.Groups[1].Value, out var code) ? code : null;

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
        var text = PackageOutputParser.Clean(result.Output + "\n" + result.Error);
        // WinGet can exit 0 after printing "Installer failed with exit code: 0x80073d28" (it does for some Store packages). Only that exact line is trusted
        // when the exit code says success, so a successful run that merely mentions administrators is never run a second time.
        if (result.ExitCode == 0) return InstallerFailedForAdministrator().IsMatch(text);
        // WinGet's own code for "this command needs administrator rights" does not depend on the language it speaks; the words below only cover what an installer printed in English.
        if (unchecked((uint)result.ExitCode) == 0x8A150019) return true;
        return AdministratorRequired().IsMatch(text);
    }

    [GeneratedRegex(@"(?:Installer|Uninstall) failed with exit code: 0x80073d28", RegexOptions.IgnoreCase)]
    private static partial Regex InstallerFailedForAdministrator();

    /// <summary>
    /// Runs a WinGet command. If the installer says it needs administrator rights, tells the person and runs it once more through the Windows permission prompt, with the program
    /// <paramref name="locator"/> resolves for an elevated run (see <see cref="ElevatedProgram"/>). A program that must not run that way is not run again; the reason stays with the result.
    /// </summary>
    public static async Task<CommandResult> RunAsync(ICommandRunner runner, CommandSpec command, string name, CancellationToken token, IExecutableLocator? locator = null)
    {
        var result = await runner.RunAsync(command, token).ConfigureAwait(false);
        if (command.Elevate || !NeedsAdministrator(result)) return result;
        var (file, problem) = locator?.Elevated(command.FileName) ?? ElevatedProgram.Resolve(command.FileName);
        if (problem is not null)
        {
            try { command.Progress?.Report(new(problem, CommandOutputKind.Notice)); } catch { /* A display failure must not hide the reason. */ }
            return result with { Error = (result.Error + "\n" + $"{name} needs administrator permission, which was not asked for: " + problem).Trim() };
        }
        try { command.Progress?.Report(new($"{name} needs administrator permission. Asking Windows for it…", CommandOutputKind.Notice)); }
        catch { /* A display failure must not stop the retry. */ }
        var elevated = await runner.RunAsync(command with { FileName = file, Elevate = true }, token).ConfigureAwait(false);
        // The elevated window's output cannot be captured, so keep what the first attempt said alongside the new exit code.
        return elevated with { Output = result.Output + "\n" + elevated.Output };
    }

    [GeneratedRegex(@"0x80073d28|administrator privileges (?:are )?required", RegexOptions.IgnoreCase)]
    private static partial Regex AdministratorRequired();

    // Installers report the code in decimal ("exit code: 1603") or, for Store packages, in hex ("exit code: 0x80073d28").
    // An uninstall prints "Uninstall failed with exit code: 1605", not "Installer failed".
    [GeneratedRegex(@"exit code was '(-?\d+)'", RegexOptions.IgnoreCase)]
    private static partial Regex ChocolateyCode();

    [GeneratedRegex(@"(?:installer|install|uninstall(?:er)?) failed with exit code:?\s*(0x[0-9a-f]+|-?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex InnerCode();
}
