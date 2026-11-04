using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TidyWindow.Next.Core;

/// <summary>
/// Which programs may run with administrator rights. An elevated command runs whatever file its path names, as an administrator, so the file must
/// live where accounts without administrator rights cannot replace it: not in a profile or a temporary folder, and not in a folder whose
/// permissions let ordinary accounts write (judged the same way as a folder added to the Windows-wide PATH). The rules live here so that installs,
/// repairs and recovery all apply the same ones; the locator and the health check use them, and so can anything else that sets <see cref="CommandSpec.Elevate"/>.
/// </summary>
public static partial class ElevatedProgram
{
    [GeneratedRegex(@"^Microsoft\.DesktopAppInstaller_(\d+(?:\.\d+){1,3})_[A-Za-z0-9]+__8wekyb3d8bbwe$", RegexOptions.IgnoreCase)]
    private static partial Regex AppInstallerPackage();

    /// <summary>Windows PowerShell, which every PC has and only administrators can change.</summary>
    public static string WindowsPowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>The PowerShell host for runs with administrator rights: PowerShell 7 as its installer puts it under Program Files, else Windows PowerShell. Never one from the PATH, a Store alias or a Scoop shim, which an account can replace.</summary>
    public static string PowerShell
    {
        get
        {
            var pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
            return File.Exists(pwsh) ? pwsh : WindowsPowerShell;
        }
    }

    /// <summary>True when <paramref name="path"/> names a file that may run with administrator rights.</summary>
    public static bool IsProtectedLocation(string path) => ProtectedPathProblem(path) is null;

    /// <summary>Why <paramref name="fileName"/> must not run with administrator rights, in plain words, or null when it may.</summary>
    public static string? ProtectedPathProblem(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !Path.IsPathFullyQualified(fileName) || fileName.StartsWith(@"\\", StringComparison.Ordinal))
            return $"\"{fileName}\" is not a full path to a file on this PC, so it cannot run with administrator rights.";
        string full;
        try { full = Path.GetFullPath(fileName); }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException) { return $"\"{fileName}\" is not a valid path, so it cannot run with administrator rights."; }
        if (!OperatingSystem.IsWindows()) return null;
        if (!File.Exists(full)) return $"{full} does not exist.";
        // This judges the file at exactly this path, so unlike Resolve it does not follow an alias to the program it stands for: the path itself is what would be run.
        if (IsLink(full)) return $"{full} is a link, and a link can be pointed at any program, so it cannot run with administrator rights.";
        // The rule for what may run with administrator rights is the one in Resolve, so a folder is judged the same way whichever way the question comes.
        return Resolve(full).Problem;
    }

    /// <summary>
    /// WinGet as it is installed for this account: winget.exe inside the App Installer package under Program Files\WindowsApps, which only Windows
    /// writes. The alias under the profile is a link that any program of this account can replace, so it is never the one to elevate. Null when
    /// App Installer is not registered for this account or its folder cannot be trusted.
    /// </summary>
    public static string? Winget() => OperatingSystem.IsWindows() ? WingetCore() : null;

    [SupportedOSPlatform("windows")]
    private static string? WingetCore()
    {
        var apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        string? best = null;
        Version? bestVersion = null;
        try
        {
            using var packages = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            if (packages is null) return null;
            foreach (var name in packages.GetSubKeyNames())
            {
                var match = AppInstallerPackage().Match(name);
                if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var version)) continue;
                using var package = packages.OpenSubKey(name);
                if (package?.GetValue("PackageRootFolder") is not string folder || folder.Length == 0 || !Path.IsPathFullyQualified(folder)) continue;
                var executable = Path.Combine(Path.GetFullPath(folder), "winget.exe");
                if (!IsWithin(executable, apps) || !File.Exists(executable) || (bestVersion is not null && version <= bestVersion)) continue;
                best = executable; bestVersion = version;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) { return null; }
        return best is not null && ProtectedPathProblem(best) is null ? best : null;
    }
}

/// <param name="variables">Reads an environment variable from a scope, or from this process when the scope is null. Replaceable in tests.</param>
/// <param name="fileExists">Whether a file exists. Replaceable in tests.</param>
public sealed class ExecutableLocator(Func<string, EnvironmentVariableTarget?, string?>? variables = null, Func<string, bool>? fileExists = null) : IExecutableLocator
{
    private readonly Func<string, EnvironmentVariableTarget?, string?> variables = variables ?? ((name, scope) => scope is { } target ? Environment.GetEnvironmentVariable(name, target) : Environment.GetEnvironmentVariable(name));
    private readonly Func<string, bool> fileExists = fileExists ?? File.Exists;

    /// <summary>The host for Scoop and for the recovery scripts. Global Scoop operations run it with administrator rights, so it is always one of the protected hosts; see <see cref="ElevatedProgram.PowerShell"/>.</summary>
    public string PowerShell => ElevatedProgram.PowerShell;

    public string? Find(ManagerKind manager)
    {
        var name = manager switch { ManagerKind.Winget => "winget.exe", ManagerKind.Chocolatey => "choco.exe", _ => "scoop.ps1" };
        var location = new ManagerLocations(variables).Get(manager);
        IEnumerable<string> directories = manager switch
        {
            // WinGet's own alias comes before the PATH. A run with administrator rights does not use either; it resolves WinGet through ElevatedProgram.Winget.
            ManagerKind.Winget => new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps") }.Concat(PathEntries(null, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine)),
            // Chocolatey always runs with administrator rights, so the PC-wide installation comes first and an account's own PATH last.
            ManagerKind.Chocolatey => new[] { Path.Combine(location.Root, "bin"), location.Root }.Concat(PathEntries(EnvironmentVariableTarget.Machine)).Concat(PathEntries(null, EnvironmentVariableTarget.User)),
            _ => PathEntries(null, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine).Append(Path.Combine(location.Root, "shims"))
        };
        return directories.Select(x => Path.Combine(x, name))
            .Append(Path.Combine(location.Root, manager == ManagerKind.Scoop ? "apps/scoop/current/bin/scoop.ps1" : name))
            .FirstOrDefault(fileExists);
    }

    private IEnumerable<string> PathEntries(params EnvironmentVariableTarget?[] scopes) => scopes.SelectMany(scope => (variables("PATH", scope) ?? "").Split(Path.PathSeparator))
        .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Environment.ExpandEnvironmentVariables(x.Trim().Trim('"')));
}
