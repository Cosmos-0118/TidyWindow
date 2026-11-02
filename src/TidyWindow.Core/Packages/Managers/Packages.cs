using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace TidyWindow.Next.Core;

public enum ManagerKind { Winget, Chocolatey, Scoop }
public enum ManagerHealth { Ready, Missing, Unavailable, ConnectionIssue, Restricted }
public enum ManagerProblem { None, NotInstalled, Executable, Configuration, Source, Network, Policy }

public sealed record ManagerDefinition(ManagerKind Kind, string Name, string Description, string SetupUrl)
{
    public static IReadOnlyList<ManagerDefinition> All { get; } =
    [
        new(ManagerKind.Winget, "WinGet", "The Windows package manager", "https://learn.microsoft.com/windows/package-manager/winget/"),
        new(ManagerKind.Chocolatey, "Chocolatey", "Community-powered Windows software", "https://chocolatey.org/install"),
        new(ManagerKind.Scoop, "Scoop", "Command-line tools, installed for you", "https://scoop.sh/")
    ];
}

public sealed record ManagerStatus(ManagerDefinition Definition, ManagerHealth Health, string Version, string? Executable, string Detail, ManagerProblem Problem = ManagerProblem.None);
public sealed record Package(ManagerKind Manager, string Id, string Name, string Version, string Source)
{
    public string Key => $"{Manager}:{Id}";
    public string ManagerName => ManagerDefinition.All.Single(x => x.Kind == Manager).Name;
}
public sealed record SearchResult(ManagerKind Manager, IReadOnlyList<Package> Packages, string? Error = null);
public enum CommandOutputKind { Output, Error, Notice, Information, Warning, Progress, Success }
public sealed record CommandOutput(string Text, CommandOutputKind Kind = CommandOutputKind.Output);
public sealed record CommandSpec(string FileName, IReadOnlyList<string> Arguments, TimeSpan Timeout, bool Elevate = false)
{
    public IProgress<CommandOutput>? Progress { get; init; }
    public bool MayShowInstaller { get; init; }
}
public sealed record CommandResult(int ExitCode, string Output, string Error);

public interface ICommandRunner
{
    Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default);
}

public interface IExecutableLocator
{
    string? Find(ManagerKind manager);
    string PowerShell { get; }
    /// <summary>The program to run when <paramref name="fileName"/> is to run with administrator rights, and the reason it must not be, if any: see <see cref="ElevatedProgram"/>.</summary>
    (string FileName, string? Problem) Elevated(string fileName) => ElevatedProgram.Resolve(fileName);
}

/// <summary>
/// Where a program has to be before TidyWindow runs it with administrator rights. The Windows permission prompt approves whatever file is at the path by then, so a program
/// in a folder that accounts without administrator rights can change (the person's own profile, the temporary folder, anything whose permissions let ordinary accounts write)
/// is refused: a program planted there by anything running as the person would otherwise run as an administrator on TidyWindow's say-so. WinGet's command is an App Execution
/// Alias in the profile that stands for the packaged program under Program Files\WindowsApps; that program is what is checked and run.
/// </summary>
public static class ElevatedProgram
{
    internal enum FolderAccess { Protected, Open, Unknown }

    /// <summary>The reason <paramref name="fileName"/> must not run with administrator rights, or null when it may.</summary>
    public static string? Problem(string fileName) => Resolve(fileName).Problem;

    /// <summary>The program to run with administrator rights (an alias replaced by the program it stands for) and the reason it must not be run that way, if any.</summary>
    public static (string FileName, string? Problem) Resolve(string fileName) =>
        Resolve(fileName, File.Exists, OperatingSystem.IsWindows() ? AppExecutionAlias.Target : (_ => null), folder => OperatingSystem.IsWindows() ? Access(folder) : FolderAccess.Unknown);

    /// <summary>The command made ready for an elevated run, or an exception saying why its program (or the <paramref name="script"/> that program would run) must not run with administrator rights.</summary>
    public static CommandSpec Checked(CommandSpec command, IExecutableLocator locator, string? script = null)
    {
        if (!command.Elevate) return command;
        var (file, problem) = locator.Elevated(command.FileName);
        if (problem is null && script is not null) problem = locator.Elevated(script).Problem;
        if (problem is not null) throw new InvalidOperationException(problem);
        return command with { FileName = file };
    }

    internal static (string FileName, string? Problem) Resolve(string fileName, Func<string, bool> fileExists, Func<string, string?> aliasTarget, Func<string, FolderAccess> access)
    {
        var name = Path.GetFileName(fileName) is { Length: > 0 } file ? file : fileName;
        var found = Locate(fileName, fileExists);
        if (found is null) return (fileName, $"{name} could not be found, so TidyWindow will not run it with administrator rights.");
        if (aliasTarget(found) is { } target)
        {
            if (!Path.IsPathFullyQualified(target) || !fileExists(target)) return (found, $"{name} stands for a packaged program that is not there ({target}), so TidyWindow will not run it with administrator rights.");
            found = Path.GetFullPath(target);
        }
        var folder = Path.GetDirectoryName(found) ?? found;
        if (UserPlaces().Any(place => IsUnder(folder, place))) return (found, Open(name, folder));
        return access(folder) switch
        {
            FolderAccess.Protected => (found, null),
            FolderAccess.Open => (found, Open(name, folder)),
            _ => (found, $"TidyWindow could not check who can change the folder {name} is in ({folder}), so it will not run it with administrator rights.")
        };
    }

    private static string Open(string name, string folder) =>
        $"{name} is installed in a folder that any program on this PC could change ({folder}), so TidyWindow will not run it with administrator rights.";

    /// <summary>A bare name is found the way Windows finds it for a new process (the system folders, then PATH, which can hold the person's own folders); where it is found decides whether it may run.</summary>
    private static string? Locate(string fileName, Func<string, bool> fileExists)
    {
        if (fileName.Trim().Length == 0 || fileName.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
        try
        {
            if (Path.IsPathRooted(fileName)) return fileExists(fileName) ? Path.GetFullPath(fileName) : null;
            var folders = new List<string> { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows) };
            folders.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(x => x.Trim().Trim('"')));
            foreach (var folder in folders.Where(x => x.Length > 0))
            {
                var candidate = Path.GetFullPath(Path.Combine(folder, fileName));
                if (fileExists(candidate)) return candidate;
                if (!Path.HasExtension(fileName) && fileExists(candidate + ".exe")) return candidate + ".exe";
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        return null;
    }

    /// <summary>Folders every account on the PC gets to write to, whatever their permissions say today: the profiles, the temporary folder, the shared Public profile.</summary>
    private static IEnumerable<string> UserPlaces()
    {
        var profiles = OperatingSystem.IsWindows() ? ProfilesFolder() : Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? "";
        string[] places =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Path.GetTempPath(), profiles, profiles.Length > 0 ? Path.Combine(profiles, "Public") : ""
        ];
        return places.Where(x => x.Length > 0);
    }

    [SupportedOSPlatform("windows")]
    private static string ProfilesFolder()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (key?.GetValue("ProfilesDirectory") is string value && value.Length > 0) return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) { }
        return Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "Users");
    }

    /// <summary>
    /// Walks the folder and every folder above it, reading who may change what (the rules the Windows-wide PATH guard applies). Windows installs packaged apps under
    /// WindowsApps and lets ordinary accounts neither read its permissions nor touch anything in it, so that one folder is passed over; the package folder and Program Files are checked as usual.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static FolderAccess Access(string folder)
    {
        string full;
        try { full = Path.GetFullPath(folder); }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException) { return FolderAccess.Unknown; }
        var windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        var target = true;
        for (var directory = new DirectoryInfo(full); directory is not null; directory = directory.Parent, target = false)
        {
            DirectorySecurity security;
            try { security = directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
            {
                if (IsUnder(directory.FullName, windowsApps) && IsUnder(windowsApps, directory.FullName)) continue;
                return FolderAccess.Unknown;
            }
            if (MachinePathGuard.Opens(security, target)) return FolderAccess.Open;
        }
        return FolderAccess.Protected;
    }

    private static bool IsUnder(string path, string root)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(root);
        var full = Path.TrimEndingDirectorySeparator(path);
        return full.Equals(trimmed, StringComparison.OrdinalIgnoreCase) || full.StartsWith(trimmed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Reads where an App Execution Alias (the small reparse-point file Windows keeps in the profile for a packaged app's command, such as winget.exe) really points.</summary>
[SupportedOSPlatform("windows")]
internal static class AppExecutionAlias
{
    private const uint Tag = 0x8000001B, GetReparsePoint = 0x000900A8, OpenReparsePoint = 0x00200000, BackupSemantics = 0x02000000, OpenExisting = 3, ShareAll = 7;

    /// <summary>The packaged program the alias stands for, or null when the file is not an alias or cannot be read.</summary>
    public static string? Target(string path)
    {
        try
        {
            if (!File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) return null;
            using var handle = CreateFileW(path, 0, ShareAll, IntPtr.Zero, OpenExisting, OpenReparsePoint | BackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid) return null;
            var buffer = new byte[16 * 1024];
            if (!DeviceIoControl(handle, GetReparsePoint, IntPtr.Zero, 0, buffer, buffer.Length, out var size, IntPtr.Zero) || size < 12) return null;
            if (BitConverter.ToUInt32(buffer, 0) != Tag) return null;
            // After the tag and length: a version (3), then NUL-separated text: the package, the app, the program's path, the app's type.
            var length = Math.Min(BitConverter.ToUInt16(buffer, 4), size - 8);
            if (length < 6 || BitConverter.ToUInt32(buffer, 8) != 3) return null;
            var parts = Encoding.Unicode.GetString(buffer, 12, length - 4).Split('\0');
            return parts.Length >= 3 && parts[2].Length > 0 ? parts[2] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
}
