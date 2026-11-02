using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TidyWindow.Next.Core;

/// <summary>Runs the few programs the removal steps need: Windows PowerShell for the app cmdlets, and DISM and the installers' own uninstallers.</summary>
internal static class DebloatProcess
{
    /// <summary>
    /// The app cmdlets need Windows PowerShell 5.1. PowerShell 7 loads them through a compatibility layer that fails with 0x80131539,
    /// so this never uses whatever "powershell" or "pwsh" is first on the path.
    /// </summary>
    public static string PowerShell { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>Where every program is started from: a folder only administrators can write to, so nothing a program picks up from its starting folder can be one of the person's own files.</summary>
    public static string System32 { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");

    /// <summary>
    /// The only folders Windows PowerShell may load modules from here. A process started through the permission prompt keeps the account's own environment,
    /// and Windows PowerShell looks in the account's Documents\WindowsPowerShell\Modules before anywhere else, so a module planted there by an ordinary program
    /// (an "Appx" module with its own Get-AppxPackage) would run with administrator rights the next time an app was removed. Only folders administrators control are searched.
    /// </summary>
    public static string SystemModulePath { get; } = string.Join(';',
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsPowerShell", "Modules"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "Modules"));

    /// <summary>How long the output pipes may stay open after the program itself has ended before whatever has arrived is taken as its output.</summary>
    public static readonly TimeSpan PipeGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Every script starts with this. Errors stop the script; progress and warnings go nowhere, because the console host prints warnings on the same output as the
    /// JSON the scripts return (a damaged package registration makes Get-AppxPackage warn), and one warning in the middle of it would make the whole list unreadable.
    /// </summary>
    internal const string Prelude = "$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';$WarningPreference='SilentlyContinue';[Console]::OutputEncoding=[Text.Encoding]::UTF8;";

    /// <param name="Output">What the program printed on its standard output.</param>
    /// <param name="Error">What it printed on its error output, with PowerShell's serialised error records turned back into the messages they carry.</param>
    public sealed record Result(int ExitCode, string Output, bool TimedOut, string Error = "")
    {
        /// <summary>What to show for a program that failed: its error output when it has any, else what it printed.</summary>
        public string Problem => Error.Length > 0 ? Error : Output;
    }

    internal static ProcessStartInfo StartInfo(string file, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = System32
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["PSModulePath"] = SystemModulePath;
        return info;
    }

    public static Result Run(string file, IEnumerable<string> arguments, TimeSpan timeout, TimeSpan? grace = null)
    {
        using var process = Process.Start(StartInfo(file, arguments)) ?? throw new InvalidOperationException($"{Path.GetFileName(file)} could not be started.");
        var output = new StringBuilder();
        var errors = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (errors) errors.AppendLine(e.Data); };
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        if (!process.WaitForExit(timeout))
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            return new(-1, "", true);
        }
        // The program has ended, but something it started (OneDrive starting itself again, an installer's helper) may still hold its output pipes.
        // Waiting for them to close would wait for that program instead, so the output gets a moment to finish and the rest is let go.
        var wait = grace ?? PipeGrace;
        using (var stop = new CancellationTokenSource(wait))
        {
            try { process.WaitForExitAsync(stop.Token).Wait(wait); }
            catch (AggregateException) { }
        }
        string text, error;
        lock (output) text = output.ToString().Trim();
        lock (errors) error = errors.ToString();
        return new(process.ExitCode, text, false, Decode(error));
    }

    /// <summary>PowerShell writes its error records to the error output as CLIXML when that output is a pipe; this turns them back into plain messages.</summary>
    private static string Decode(string error)
    {
        if (error.Trim().Length == 0) return "";
        var text = new StringBuilder();
        foreach (var message in new PowerShellOutputDecoder(CommandOutputKind.Error).Feed(error, final: true)) text.Append(message.Text);
        return text.ToString().Trim();
    }

    /// <summary>Runs a script in Windows PowerShell. Its text is passed encoded, so nothing in it is ever read as a command line.</summary>
    public static Result Script(string script, TimeSpan timeout)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Prelude + script));
        return Run(PowerShell, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded], timeout);
    }
}

/// <summary>
/// The Windows PowerShell scripts the removal steps run. Every cmdlet that comes from a module is named with its module (Appx\Get-AppxPackage), so the copy
/// Windows ships is the one that runs even if another module of the same name could be found. Every value written into a script has passed <see cref="DebloatGuard"/> first.
/// </summary>
internal static class DebloatScripts
{
    public static string Packages(bool elevated) =>
        (elevated ? "$p=Appx\\Get-AppxPackage -AllUsers;" : "$p=Appx\\Get-AppxPackage;") +
        "$r=@($p|ForEach-Object{[pscustomobject]@{n=$_.Name;f=$_.PackageFullName;y=$_.PackageFamilyName;r=[bool]$_.NonRemovable;w=[bool]$_.IsFramework;" +
        "l=\"$($_.InstallLocation)\";u=@($_.PackageUserInformation|Where-Object{\"$($_.InstallState)\" -eq 'Installed'}|ForEach-Object{\"$($_.UserSecurityID.SID)\"})}});" +
        "Microsoft.PowerShell.Utility\\ConvertTo-Json -InputObject $r -Compress -Depth 3";

    /// <summary>
    /// Everyone first: removing it for one account first leaves Windows holding the files for itself, and its "all users" removal then has nothing to finish.
    /// Each account is tried afterwards, for a package that is still registered for some of them. Before that the package's own programs are closed:
    /// a running WidgetBoard.exe holds the packageManagement capability and installs Widgets back from C:\Windows\InboxApps, and a running program also holds
    /// its files open, which makes Windows refuse the removal.
    /// </summary>
    public static string RemovePackage(string fullName, IReadOnlyList<string> sids)
    {
        var script = new StringBuilder($"$e=@();Microsoft.PowerShell.Management\\Get-Process -ErrorAction SilentlyContinue|Where-Object{{$_.Path -like '*\\WindowsApps\\{fullName}\\*'}}|Microsoft.PowerShell.Management\\Stop-Process -Force -ErrorAction SilentlyContinue;");
        script.Append($"try{{Appx\\Remove-AppxPackage -Package '{fullName}' -AllUsers}}catch{{$e+=$_.Exception.Message}};");
        if (sids.Count > 0)
        {
            script.Append($"if(Appx\\Get-AppxPackage -AllUsers|Where-Object{{$_.PackageFullName -eq '{fullName}'}}){{");
            foreach (var sid in sids) script.Append($"try{{Appx\\Remove-AppxPackage -Package '{fullName}' -User '{sid}'}}catch{{$e+=$_.Exception.Message}};");
            script.Append('}');
        }
        script.Append("($e -join ' | ')");
        return script.ToString();
    }

    public static string RemoveProvisioned(string packageName) =>
        $"try{{Dism\\Remove-AppxProvisionedPackage -Online -AllUsers -PackageName '{packageName}'|Out-Null}}catch{{$_.Exception.Message}}";

    public const string ListOneDriveTasks = "Microsoft.PowerShell.Utility\\ConvertTo-Json -InputObject @(ScheduledTasks\\Get-ScheduledTask|Where-Object{$_.TaskName -like 'OneDrive*'}|ForEach-Object{$_.TaskName}) -Compress";

    public static string UnregisterTasks(IEnumerable<string> names) =>
        string.Concat(names.Select(x => $"try{{ScheduledTasks\\Unregister-ScheduledTask -TaskName '{x}' -Confirm:$false}}catch{{}};"));
}

/// <summary>
/// Whether OneDrive is installed. Two things that look like OneDrive are not evidence: Windows keeps OneDriveSetup.exe in System32 whether or not OneDrive is installed,
/// and OneDrive's own uninstaller leaves its folder behind (logs, and a shell extension that Explorer keeps locked until it restarts).
/// What counts is the program itself, in a version folder or beside it, or the uninstall entry OneDrive registers.
/// </summary>
public static class OneDriveDetection
{
    public static bool IsInstalled(IEnumerable<string> roots, Func<string, bool> fileExists, Func<string, bool> directoryExists, Func<bool> hasUninstallEntry)
    {
        if (hasUninstallEntry()) return true;
        foreach (var root in roots)
        {
            if (fileExists(Path.Combine(root, "OneDrive.exe"))) return true;
            // The program also sits in a folder named for its version, such as 26.189.0928.0009.
            if (directoryExists(root) && VersionFolders(root).Any(folder => fileExists(Path.Combine(folder, "OneDrive.exe")))) return true;
        }
        return false;
    }

    private static IEnumerable<string> VersionFolders(string root)
    {
        try { return Directory.EnumerateDirectories(root).Where(x => char.IsDigit(Path.GetFileName(x)[0])).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}

/// <summary>
/// Reads the browsers registered with Windows (Clients\StartMenuInternet). A registration only counts when the program it names is really there,
/// so a browser whose folder was deleted rather than uninstalled cannot stand in for one: removing Edge on its word would leave the PC with no browser.
/// </summary>
public static class BrowserDetection
{
    /// <summary>Edge and Internet Explorer do not count as another browser.</summary>
    public static bool IsOtherBrowser(string name) =>
        !name.StartsWith("Microsoft Edge", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("IEXPLORE", StringComparison.OrdinalIgnoreCase);

    /// <summary>The program a browser's open command runs: the quoted path, or the path up to its .exe when it is not quoted. Null when it is not a full path.</summary>
    public static string? ProgramOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var text = command.Trim();
        string path;
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            if (end < 0) return null;
            path = text[1..end];
        }
        else
        {
            var end = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            path = end < 0 ? text.Split(' ')[0] : text[..(end + 4)];
        }
        path = path.Trim();
        return Path.IsPathFullyQualified(path) && !path.Contains("..") ? path : null;
    }
}

/// <summary>An Edge that is installed: the setup program that uninstalls it, and the Windows Installer product code when it was installed from Microsoft's MSI.</summary>
public sealed record EdgeInstall(string Setup, string? MsiProductCode);

/// <summary>
/// Whether Edge is installed, found from what Edge itself leaves rather than from one registry entry. Edge installed by its own setup registers
/// "Uninstall\Microsoft Edge", but Edge installed from Microsoft's MSI (which is what WinGet installs) registers a Windows Installer product instead and no such entry,
/// so looking only there made Edge vanish from the list as soon as it was put back. Its updater records the uninstall command for both kinds, and the setup program
/// sits in the same place for both. What counts is a setup.exe that exists inside Edge's own folder, so an entry that outlives an uninstall is not evidence.
/// </summary>
public static partial class EdgeDetection
{
    /// <summary>The updater's id for Edge Stable.</summary>
    public const string StableId = "{56EB18F8-B008-4CBD-B6D2-8C97FE7E9062}";

    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}(-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}\}$")]
    private static partial Regex ProductCode();

    public static bool IsProductCode(string? text) => text is not null && ProductCode().IsMatch(text);

    /// <param name="registered">Uninstall commands that were found in the registry, most trusted first. Each may be quoted or not, and may carry arguments.</param>
    /// <param name="applicationFolder">Edge's own folder, such as C:\Program Files (x86)\Microsoft\Edge\Application. A setup program anywhere else is ignored.</param>
    /// <param name="versionFolders">The version folders inside <paramref name="applicationFolder"/>.</param>
    public static EdgeInstall? Find(IEnumerable<string> registered, string applicationFolder, string? msiProductCode, Func<string, bool> fileExists, Func<string, IEnumerable<string>> versionFolders)
    {
        var product = IsProductCode(msiProductCode) ? msiProductCode : null;
        foreach (var command in registered)
            if (SetupOf(command) is { } setup && IsInside(setup, applicationFolder) && fileExists(setup)) return new(setup, product);
        // No usable entry (or none at all): the setup program of the newest version folder.
        foreach (var folder in versionFolders(applicationFolder).Where(x => char.IsDigit(Path.GetFileName(x).FirstOrDefault())).OrderByDescending(x => Version.TryParse(Path.GetFileName(x), out var v) ? v : new Version()))
        {
            var setup = Path.Combine(folder, "Installer", "setup.exe");
            if (fileExists(setup)) return new(setup, product);
        }
        return null;
    }

    /// <summary>The setup.exe an uninstall command runs. Edge's updater stores the path without quotes even when it has spaces, so the path is cut after "setup.exe" instead of at the first space.</summary>
    public static string? SetupOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var end = command.IndexOf("setup.exe", StringComparison.OrdinalIgnoreCase);
        if (end < 0) return null;
        var path = command[..(end + "setup.exe".Length)].Trim().Trim('"');
        return Path.IsPathFullyQualified(path) && string.Equals(Path.GetFileName(path), "setup.exe", StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    private static bool IsInside(string path, string folder) => path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) && !path.Contains("..");
}

/// <summary>Reads packages and the state of OneDrive, Edge and Recall from this PC.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDebloatSystem(bool elevated) : IDebloatSystem
{
    private static readonly string[] KnownFolders = ["Desktop", "Personal", "My Pictures"];
    private static readonly string[] KnownFolderNames = ["Desktop", "Documents", "Pictures"];
    private const string UserShellFolders = @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";

    /// <summary>The account this process runs as. In the helper that is whoever approved the permission prompt, which need not be the account that asked.</summary>
    internal static string? CurrentSid { get; } = ReadCurrentSid();

    private static string? ReadCurrentSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value;
    }

    public int Build { get; } = Environment.OSVersion.Version.Build;

    public IReadOnlyList<AppxFact> Packages()
    {
        var result = DebloatProcess.Script(DebloatScripts.Packages(elevated), TimeSpan.FromSeconds(90));
        if (result.TimedOut || result.ExitCode != 0) throw new InvalidOperationException("Windows did not list the installed apps. " + Brief(result.Problem));
        var list = new List<AppxFact>();
        using var json = JsonDocument.Parse(result.Output.Length == 0 ? "[]" : result.Output);
        foreach (var row in json.RootElement.EnumerateArray())
        {
            var users = new List<string>();
            if (row.TryGetProperty("u", out var u))
            {
                if (u.ValueKind == JsonValueKind.Array) users.AddRange(u.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0));
                else if (u.ValueKind == JsonValueKind.String && u.GetString() is { Length: > 0 } one) users.Add(one);
            }
            list.Add(new(Text(row, "n"), Text(row, "f"), Text(row, "y"), Flag(row, "r"), Flag(row, "w"), users, Text(row, "l")));
        }
        // Without administrator rights Windows does not say who has a package; what it lists is the current user's. The store adds what is installed for other accounts.
        return elevated ? list : AppxStoreRecords.Merge(list.Select(x => x with { Users = [] }).ToList(), AppxRegistry.Read("Applications", AppxStoreRecords.Package));
    }

    /// <summary>
    /// What Windows installs for new accounts. This is read from the package store in the registry, which needs no administrator rights and no slow DISM call,
    /// and which still lists an app after "Uninstall" removed it for one account only. The apps from the Store are under Applications;
    /// InboxApplications holds only parts of Windows itself.
    /// </summary>
    public IReadOnlyList<ProvisionedFact> Provisioned() => AppxRegistry.Read("Applications", AppxStoreRecords.Staged);

    /// <param name="userSid">The account that asked. The helper runs as whoever approved the prompt, so OneDrive, which is often installed for one account only, is looked for in the profile of the account that asked.</param>
    public PlaybookFacts Playbooks(string? userSid = null)
    {
        var self = userSid is null || string.Equals(userSid, CurrentSid, StringComparison.OrdinalIgnoreCase);
        var profile = self ? null : UserHives.ProfileOf(userSid);
        var folders = elevated ? BackedUpFoldersOfEveryAccount() : BackedUpFoldersOfSignedInAccounts();
        return new(OneDriveInstalled(profile, profile is null ? null : userSid), folders, FindEdge() is not null, OtherBrowserInstalled(), RecallInstalled());
    }

    /// <summary>Accounts whose Desktop, Documents or Pictures live in OneDrive, read from the registry of every account that is signed in: the only ones readable without administrator rights.</summary>
    private static List<string> BackedUpFoldersOfSignedInAccounts()
    {
        var folders = new List<string>();
        foreach (var sid in Registry.Users.GetSubKeyNames().Where(DebloatCleanup.IsAccountSid))
        {
            try
            {
                using var key = Registry.Users.OpenSubKey(sid + "\\" + UserShellFolders);
                if (key is null) continue;
                foreach (var name in BackedUp(key)) if (!folders.Contains(name)) folders.Add(name);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return folders;
    }

    /// <summary>With administrator rights every account on the PC is checked, signed in or not, because the removal is for all of them. Each folder names its account.</summary>
    private static List<string> BackedUpFoldersOfEveryAccount()
    {
        var folders = new List<string>();
        UserHives.ForEach(account =>
        {
            using var key = account.Root.OpenSubKey(UserShellFolders);
            if (key is null) return;
            var who = Path.GetFileName(Path.TrimEndingDirectorySeparator(account.Profile));
            foreach (var name in BackedUp(key)) folders.Add($"{name} ({who})");
        });
        return folders;
    }

    private static IEnumerable<string> BackedUp(RegistryKey key)
    {
        for (var i = 0; i < KnownFolders.Length; i++)
            if (key.GetValue(KnownFolders[i], null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string path && path.Contains("OneDrive", StringComparison.OrdinalIgnoreCase)) yield return KnownFolderNames[i];
    }

    /// <param name="profile">The profile folder of the account that asked, when that is not the account this process runs as; null for this account's own profile.</param>
    /// <param name="userSid">That account's id, so its uninstall entry can be read from its registry while it is signed in.</param>
    internal static bool OneDriveInstalled(string? profile = null, string? userSid = null)
    {
        var programFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft OneDrive");
        var local = profile is null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "OneDrive")
            : Path.Combine(profile, "AppData", "Local", "Microsoft", "OneDrive");
        return OneDriveDetection.IsInstalled([programFiles, local], File.Exists, Directory.Exists, () => UninstallEntryExists(userSid));
    }

    private static bool UninstallEntryExists(string? userSid)
    {
        const string path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\OneDriveSetup.exe";
        try
        {
            // The account that asked: this process's own registry when it runs as that account, else that account's hive while it is signed in.
            using (var user = userSid is null ? Registry.CurrentUser.OpenSubKey(path) : Registry.Users.OpenSubKey(userSid + "\\" + path)) if (user is not null) return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        using (var machine = Registry.LocalMachine.OpenSubKey(path)) if (machine is not null) return true;
        using var machine32 = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\OneDriveSetup.exe");
        return machine32 is not null;
    }

    /// <summary>The installed Edge, or null when Edge is not installed. See <see cref="EdgeDetection"/> for why more than one place is read.</summary>
    internal static EdgeInstall? FindEdge()
    {
        var registered = new List<string>();
        var places = new[]
        {
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Microsoft Edge", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Microsoft Edge",
            @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\ClientState\" + EdgeDetection.StableId, @"SOFTWARE\Microsoft\EdgeUpdate\ClientState\" + EdgeDetection.StableId
        };
        foreach (var path in places)
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            if (key?.GetValue("UninstallString") is string text && text.Length > 0) registered.Add(text);
        }
        var application = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application");
        return EdgeDetection.Find(registered, application, EdgeMsiProductCode(), File.Exists, VersionFolders);
    }

    private static IEnumerable<string> VersionFolders(string folder)
    {
        try { return Directory.Exists(folder) ? Directory.EnumerateDirectories(folder).ToList() : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>The Windows Installer product code of Edge when it was installed from Microsoft's MSI, which registers itself under its code instead of the name "Microsoft Edge".</summary>
    private static string? EdgeMsiProductCode()
    {
        foreach (var path in new[] { @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            using var list = Registry.LocalMachine.OpenSubKey(path);
            if (list is null) continue;
            foreach (var name in list.GetSubKeyNames().Where(EdgeDetection.IsProductCode))
            {
                using var key = list.OpenSubKey(name);
                if (key?.GetValue("DisplayName") as string == "Microsoft Edge" && key.GetValue("Publisher") as string == "Microsoft Corporation" && key.GetValue("WindowsInstaller") is 1) return name;
            }
        }
        return null;
    }

    /// <summary>
    /// Another web browser is installed for everyone: registered with Windows for the whole PC, with its program really there. A registration made for one account
    /// only does not count for a removal that is for every account, and one that outlived its uninstall does not count at all. See <see cref="BrowserDetection"/>.
    /// </summary>
    internal static bool OtherBrowserInstalled()
    {
        foreach (var path in new[] { @"SOFTWARE\Clients\StartMenuInternet", @"SOFTWARE\WOW6432Node\Clients\StartMenuInternet" })
        {
            using var clients = Registry.LocalMachine.OpenSubKey(path);
            if (clients is null) continue;
            foreach (var name in clients.GetSubKeyNames())
            {
                if (!BrowserDetection.IsOtherBrowser(name)) continue;
                using var command = clients.OpenSubKey(name + @"\shell\open\command");
                if (BrowserDetection.ProgramOf(command?.GetValue(null) as string) is { } program && File.Exists(program)) return true;
            }
        }
        return false;
    }

    /// <summary>Recall is a Windows feature. It is there when its servicing package is installed (state 112).</summary>
    internal static bool RecallInstalled()
    {
        using var packages = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\Packages");
        if (packages is null) return false;
        foreach (var name in packages.GetSubKeyNames().Where(x => x.StartsWith("UserExperience-Recall-Package", StringComparison.OrdinalIgnoreCase)))
        {
            using var key = packages.OpenSubKey(name);
            if (key?.GetValue("CurrentState") is int state && state == 112) return true;
        }
        return false;
    }

    private static string Text(JsonElement row, string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static bool Flag(JsonElement row, string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static string Brief(string text) => text.Length <= 200 ? text : text[..200] + "…";
}

/// <summary>Reads one list of package records from the registry (see <see cref="AppxStoreRecords"/>).</summary>
[SupportedOSPlatform("windows")]
internal static class AppxRegistry
{
    private const string Root = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\";

    public static List<T> Read<T>(string list, Func<string, string, T?> record) where T : class
    {
        var found = new List<T>();
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(Root + list);
            if (key is null) return found;
            foreach (var name in key.GetSubKeyNames())
            {
                using var package = key.OpenSubKey(name);
                if (record(name, package?.GetValue("Path") as string ?? "") is { } fact) found.Add(fact);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return found;
    }
}

/// <summary>The part of the app registry that deals with removal. Only these four places are ever written, and only with names that passed the guard.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAppxStore : IAppxStore
{
    private const string Root = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore";

    private static RegistryKey Machine() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    public IReadOnlyList<string> UserSids()
    {
        using var hklm = Machine();
        using var key = hklm.OpenSubKey(Root);
        return key?.GetSubKeyNames().Where(DebloatGuard.IsSid).ToList() ?? [];
    }

    public bool DeleteInbox(string fullName)
    {
        Require(fullName);
        using var hklm = Machine();
        using var key = hklm.OpenSubKey(Root + @"\InboxApplications");
        if (key is null || !key.GetSubKeyNames().Contains(fullName, StringComparer.OrdinalIgnoreCase)) return false;
        using var writable = hklm.OpenSubKey(Root + @"\InboxApplications", writable: true);
        writable?.DeleteSubKeyTree(fullName, throwOnMissingSubKey: false);
        return true;
    }

    public bool CreateEndOfLife(string sid, string fullName)
    {
        Require(fullName); if (!DebloatGuard.IsSid(sid)) throw new ArgumentException("Not an account id.", nameof(sid));
        using var hklm = Machine();
        var path = $@"{Root}\EndOfLife\{sid}\{fullName}";
        using (var existing = hklm.OpenSubKey(path)) if (existing is not null) return false;
        using var created = hklm.CreateSubKey(path);
        return created is not null;
    }

    public void DeleteEndOfLife(string sid, string fullName)
    {
        Require(fullName); if (!DebloatGuard.IsSid(sid)) return;
        using var hklm = Machine();
        using var parent = hklm.OpenSubKey($@"{Root}\EndOfLife\{sid}", writable: true);
        parent?.DeleteSubKeyTree(fullName, throwOnMissingSubKey: false);
    }

    public bool HasDeprovisioned(string family)
    {
        Require(family);
        using var hklm = Machine();
        using var key = hklm.OpenSubKey($@"{Root}\Deprovisioned\{family}");
        return key is not null;
    }

    public void CreateDeprovisioned(string family)
    {
        Require(family);
        using var hklm = Machine();
        using var key = hklm.CreateSubKey($@"{Root}\Deprovisioned\{family}");
    }

    public void DeleteDeprovisioned(string family)
    {
        Require(family);
        using var hklm = Machine();
        using var parent = hklm.OpenSubKey($@"{Root}\Deprovisioned", writable: true);
        parent?.DeleteSubKeyTree(family, throwOnMissingSubKey: false);
    }

    private static void Require(string name) { if (!DebloatGuard.IsToken(name)) throw new ArgumentException("Not a package name.", nameof(name)); }
}

/// <summary>Removes packages with the Windows PowerShell app cmdlets and DISM.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAppxOps : IAppxOps
{
    private static string Dism => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "dism.exe");

    public string? ClearNonRemovable(string family) => SetNonRemovable(family, false);

    public string? RestoreNonRemovable(string family) => SetNonRemovable(family, true);

    private static string? SetNonRemovable(string family, bool locked)
    {
        if (!DebloatGuard.IsToken(family)) return "Not a package family.";
        var result = DebloatProcess.Run(Dism, ["/Online", "/Set-NonRemovableAppPolicy", $"/PackageFamily:{family}", locked ? "/NonRemovable:1" : "/NonRemovable:0", "/NoRestart"], TimeSpan.FromMinutes(2));
        var what = locked ? "lock the app again" : "unlock the app";
        return result.TimedOut ? $"Windows took too long to {what}." : result.ExitCode is 0 or 3010 ? null : Brief($"Windows would not {what}. " + result.Problem);
    }

    public string? RemovePackage(string fullName, IReadOnlyList<string> sids)
    {
        if (!DebloatGuard.IsToken(fullName) || sids.Any(x => !DebloatGuard.IsSid(x))) return "Not a package name.";
        var result = DebloatProcess.Script(DebloatScripts.RemovePackage(fullName, sids), TimeSpan.FromMinutes(3));
        if (result.TimedOut) return "Windows took too long to remove the app.";
        return result.Problem.Length == 0 ? null : Brief(result.Problem);
    }

    public string? RemoveProvisioned(string packageName)
    {
        if (!DebloatGuard.IsToken(packageName)) return "Not a package name.";
        var result = DebloatProcess.Script(DebloatScripts.RemoveProvisioned(packageName), TimeSpan.FromMinutes(3));
        if (result.TimedOut) return "Windows took too long to remove the app.";
        return result.Problem.Length == 0 ? null : Brief(result.Problem);
    }

    private static string Brief(string text) => text.Length <= 220 ? text : text[..220] + "…";
}

/// <summary>One account on this PC with its registry open for writing. <paramref name="Classes"/> is its file-type and class settings (HKCU\Software\Classes), when they could be opened.</summary>
internal sealed record UserAccount(string Sid, string Profile, RegistryKey Root, RegistryKey? Classes);

/// <summary>
/// Visits every account that has a profile on this PC, not only the one that approved the permission prompt. An account that is signed in already has its
/// registry loaded; for the others it is loaded for the length of the visit and unloaded again. An account that cannot be opened is skipped.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class UserHives
{
    private const string ProfileList = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    public static void ForEach(Action<UserAccount> visit)
    {
        var reg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "reg.exe");
        var signedIn = Registry.Users.GetSubKeyNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mounted = 0;
        foreach (var (sid, profile) in Profiles())
        {
            try
            {
                if (signedIn.Contains(sid)) Visit(sid, profile, sid, signedIn.Contains(sid + "_Classes") ? sid + "_Classes" : null, visit);
                else
                {
                    var mount = "TidyWindowUser" + ++mounted;
                    var classes = mount + "_Classes";
                    var hive = Path.Combine(profile, "NTUSER.DAT");
                    if (!File.Exists(hive) || Load(reg, mount, hive) is false) continue;
                    var classesFile = Path.Combine(profile, "AppData", "Local", "Microsoft", "Windows", "UsrClass.dat");
                    var classesLoaded = File.Exists(classesFile) && Load(reg, classes, classesFile);
                    try { Visit(sid, profile, mount, classesLoaded ? classes : null, visit); }
                    finally
                    {
                        // A handle left open keeps the file locked, and reg refuses to unload it.
                        GC.Collect(); GC.WaitForPendingFinalizers();
                        if (classesLoaded) Unload(reg, classes);
                        Unload(reg, mount);
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or Win32Exception) { }
        }
    }

    private static void Visit(string sid, string profile, string root, string? classes, Action<UserAccount> visit)
    {
        using var user = Registry.Users.OpenSubKey(root, writable: true);
        using var classKey = classes is null ? null : Registry.Users.OpenSubKey(classes, writable: true);
        if (user is not null) visit(new(sid, profile, user, classKey));
    }

    /// <summary>The profile folder Windows keeps for an account, from Windows' own list of profiles; null for anything that is not a person's account with a profile here.</summary>
    public static string? ProfileOf(string? sid)
    {
        if (!DebloatCleanup.IsAccountSid(sid)) return null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ProfileList + "\\" + sid);
            return key?.GetValue("ProfileImagePath") is string path && Environment.ExpandEnvironmentVariables(path) is { Length: > 0 } folder && Directory.Exists(folder) ? folder : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    /// <summary>The profile new accounts are made from, where Windows says it is (normally C:\Users\Default); null when it cannot be found.</summary>
    public static string? DefaultProfile()
    {
        try
        {
            using var list = Registry.LocalMachine.OpenSubKey(ProfileList);
            if (list?.GetValue("Default") is string path && Environment.ExpandEnvironmentVariables(path) is { Length: > 0 } folder && Directory.Exists(folder)) return folder;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        var beside = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "..", "Default"));
        return Directory.Exists(beside) ? beside : null;
    }

    private static IEnumerable<(string Sid, string Profile)> Profiles()
    {
        using var list = Registry.LocalMachine.OpenSubKey(ProfileList);
        if (list is null) yield break;
        foreach (var sid in list.GetSubKeyNames().Where(DebloatCleanup.IsAccountSid))
        {
            using var key = list.OpenSubKey(sid);
            if (key?.GetValue("ProfileImagePath") is string path && Directory.Exists(Environment.ExpandEnvironmentVariables(path))) yield return (sid, Environment.ExpandEnvironmentVariables(path));
        }
    }

    private static bool Load(string reg, string mount, string file) => DebloatProcess.Run(reg, ["load", @"HKU\" + mount, file], TimeSpan.FromSeconds(20)).ExitCode == 0;
    private static void Unload(string reg, string mount) => DebloatProcess.Run(reg, ["unload", @"HKU\" + mount], TimeSpan.FromSeconds(20));
}

/// <summary>
/// Removes the things that are not ordinary apps. Each one is done the way its owner's own uninstaller expects, never by deleting program folders,
/// and none of them deletes a file of yours. A removal only counts, and only has its clean-up applied, once the thing is really gone.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPlaybooks : IDebloatPlaybooks
{
    private const string EdgeStableId = EdgeDetection.StableId;
    private const string EdgeUpdatePolicy = @"SOFTWARE\Policies\Microsoft\EdgeUpdate";

    public DebloatOutcome Remove(DebloatKind kind, string? userSid) => kind switch
    {
        DebloatKind.OneDrive => RemoveOneDrive(userSid),
        DebloatKind.Edge => RemoveEdge(),
        DebloatKind.Recall => SetRecall(enable: false),
        _ => new(false, "That is not a component.")
    };

    public DebloatOutcome Restore(DebloatKind kind) => kind switch
    {
        DebloatKind.OneDrive => new(true),  // The installer comes from WinGet.
        DebloatKind.Edge => AllowEdge(),
        DebloatKind.Recall => SetRecall(enable: true),
        _ => new(false, "That is not a component.")
    };

    /// <summary>
    /// The programs that may uninstall OneDrive: the copy of its installer that Windows ships, and the one inside a per-machine install under Program Files.
    /// Never one from a profile folder: this runs with administrator rights, and anything under a profile could have been put there by any program the account runs.
    /// </summary>
    internal static IEnumerable<string> OneDriveSetups(string windows, string programFiles, Func<string, IEnumerable<string>> setupsIn)
    {
        yield return Path.Combine(windows, "SysWOW64", "OneDriveSetup.exe");
        yield return Path.Combine(windows, "System32", "OneDriveSetup.exe");
        foreach (var setup in setupsIn(Path.Combine(programFiles, "Microsoft OneDrive")).OrderByDescending(x => x)) yield return setup;
    }

    private static IEnumerable<string> SetupsIn(string root)
    {
        try
        {
            return Directory.Exists(root)
                ? Directory.EnumerateFiles(root, "OneDriveSetup.exe", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    // OneDrive: stop it, run its own uninstaller, and stop Windows adding it to new accounts. Files in the OneDrive folder are left exactly where they are.
    private static DebloatOutcome RemoveOneDrive(string? userSid)
    {
        foreach (var name in new[] { "OneDrive", "OneDriveStandaloneUpdater", "FileCoAuth" })
            foreach (var process in Process.GetProcessesByName(name))
            {
                try { process.Kill(); process.WaitForExit(5000); }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
                finally { process.Dispose(); }
            }
        var self = userSid is null || string.Equals(userSid, WindowsDebloatSystem.CurrentSid, StringComparison.OrdinalIgnoreCase);
        var profile = self ? null : UserHives.ProfileOf(userSid);
        var setup = OneDriveSetups(Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), SetupsIn).FirstOrDefault(File.Exists);
        return RemoveOneDriveCore(() => WindowsDebloatSystem.OneDriveInstalled(profile, profile is null ? null : userSid), setup,
            path => DebloatProcess.Run(path, ["/uninstall"], TimeSpan.FromMinutes(3)), ForgetOneDrive, sameAccount: self || profile is null);
    }

    /// <summary>
    /// The steps of the OneDrive removal with the machine behind delegates, so the order can be checked without a PC: the uninstaller runs, OneDrive must then be gone
    /// from the profile of the account that asked, and only then are its startup entries, side-bar entry and tasks taken away. An uninstall that did not work leaves everything as it was.
    /// </summary>
    internal static DebloatOutcome RemoveOneDriveCore(Func<bool> installed, string? setup, Func<string, DebloatProcess.Result> uninstall, Action forget, bool sameAccount)
    {
        if (setup is null) return new(false, "OneDrive's own uninstaller was not found.");
        var result = uninstall(setup);
        if (result.TimedOut) return new(false, "OneDrive took too long to uninstall.");
        if (installed())
        {
            var code = result.ExitCode == 0 ? "" : $" Its uninstaller reported error code {result.ExitCode}.";
            return new(false, sameAccount
                ? "OneDrive is still on this PC." + code
                : "OneDrive is still on this PC. It is installed for that account only, and the administrator helper is running as a different account, so it cannot uninstall it there. Uninstall OneDrive from Settings > Apps while signed in to that account." + code);
        }
        forget();
        return new(true);
    }

    /// <summary>
    /// Do not start OneDrive again at sign-in, for any account on this PC or for accounts made later. The helper may be running as another administrator
    /// than the people it is cleaning for, so the current user's registry is not enough: every account is visited, and what is not signed in is loaded for a moment.
    /// </summary>
    private static void ForgetOneDrive()
    {
        UserHives.ForEach(account =>
        {
            ForgetRunValues(account.Root, DebloatCleanup.IsOneDriveRunValue);
            if (account.Classes is { } classes) HideOneDriveInSideBar(classes);
        });
        ForgetOneDriveInNewAccounts();
        RemoveOneDriveTasks();
    }

    /// <summary>Takes the startup entries that match, and the "allowed to start" marks Task Manager keeps for them, out of one account's registry.</summary>
    private static void ForgetRunValues(RegistryKey account, Func<string, bool> matches)
    {
        foreach (var path in new[] { DebloatCleanup.RunKey, DebloatCleanup.ApprovedKey })
        {
            using var key = account.OpenSubKey(path, writable: true);
            if (key is null) continue;
            foreach (var name in key.GetValueNames().Where(matches).ToList()) key.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    // OneDrive's uninstaller leaves its folder in File Explorer's side bar. Windows hides it when the entry says it is not pinned (32-bit and 64-bit views alike).
    private static void HideOneDriveInSideBar(RegistryKey classes)
    {
        foreach (var path in new[] { @"CLSID\" + DebloatCleanup.OneDriveNavigationPane, @"Wow6432Node\CLSID\" + DebloatCleanup.OneDriveNavigationPane })
        {
            using var key = classes.CreateSubKey(path, writable: true);
            key?.SetValue("System.IsPinnedToNameSpaceTree", 0, RegistryValueKind.DWord);
        }
    }

    // The installer registers an update task and a reporting task for each account that signed in; without OneDrive they only fail every day.
    private static void RemoveOneDriveTasks()
    {
        var listed = DebloatProcess.Script(DebloatScripts.ListOneDriveTasks, TimeSpan.FromSeconds(60));
        if (listed.TimedOut || listed.ExitCode != 0 || listed.Output.Length == 0) return;
        List<string> names;
        try
        {
            using var json = JsonDocument.Parse(listed.Output);
            names = json.RootElement.ValueKind == JsonValueKind.Array
                ? json.RootElement.EnumerateArray().Select(x => x.GetString() ?? "").Where(DebloatCleanup.IsOneDriveTask).ToList()
                : json.RootElement.ValueKind == JsonValueKind.String && DebloatCleanup.IsOneDriveTask(json.RootElement.GetString() ?? "") ? [json.RootElement.GetString()!] : [];
        }
        catch (JsonException) { return; }
        if (names.Count == 0) return;
        DebloatProcess.Script(DebloatScripts.UnregisterTasks(names), TimeSpan.FromSeconds(60));
    }

    // New accounts are made from the Default profile, where Windows says it is: not next to whatever profile this process happens to run under.
    private static void ForgetOneDriveInNewAccounts()
    {
        if (UserHives.DefaultProfile() is not { } profile) return;
        var hive = Path.Combine(profile, "NTUSER.DAT");
        if (!File.Exists(hive)) return;
        const string mount = "TidyWindowDefaultUser";
        var reg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "reg.exe");
        if (DebloatProcess.Run(reg, ["load", @"HKU\" + mount, hive], TimeSpan.FromSeconds(20)).ExitCode != 0) return;
        try
        {
            using var run = Registry.Users.OpenSubKey(mount + @"\Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            run?.DeleteValue("OneDriveSetup", throwOnMissingValue: false);
        }
        finally
        {
            // A handle left open keeps the file locked, and reg refuses to unload it.
            GC.Collect(); GC.WaitForPendingFinalizers();
            DebloatProcess.Run(reg, ["unload", @"HKU\" + mount], TimeSpan.FromSeconds(20));
        }
    }

    // Edge: Windows blocks its removal in some regions, and Edge's own uninstaller refuses to run unless it is told this is allowed and a
    // placeholder for the old Edge exists. Those two are set for the length of the removal only. WebView2, which apps depend on, is a separate product and is not touched.
    private static DebloatOutcome RemoveEdge() =>
        RemoveEdgeCore(WindowsDebloatSystem.OtherBrowserInstalled, WindowsDebloatSystem.FindEdge,
            product => DebloatProcess.Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "msiexec.exe"), ["/x", product, "/qn", "/norestart"], TimeSpan.FromMinutes(5)),
            RunEdgeSetup, ForgetEdge);

    /// <summary>
    /// The steps of the Edge removal with the machine behind delegates. Edge installed from Microsoft's MSI (what putting it back with WinGet gives) belongs to Windows Installer,
    /// which is the way to remove it; if that leaves Edge in place, its own setup program is tried next, as for any other install. Edge is then looked for again:
    /// only an Edge that is really gone has its shortcuts and startup entries taken away and the updater told not to bring it back. An Edge that stayed keeps all of them,
    /// and the result says what its uninstallers reported.
    /// </summary>
    internal static DebloatOutcome RemoveEdgeCore(Func<bool> otherBrowser, Func<EdgeInstall?> find, Func<string, DebloatProcess.Result> uninstallMsi, Func<string, DebloatProcess.Result> runSetup, Action forget)
    {
        if (!otherBrowser()) return new(false, DebloatScan.OnlyBrowserLock);
        var install = find();
        if (install is null) return new(true);
        var problems = new List<string>();
        if (install.MsiProductCode is { } product)
        {
            var msi = uninstallMsi(product);
            if (msi.TimedOut) problems.Add("Windows Installer took too long.");
            // 1605: Windows Installer no longer knows the product, which is as good as removed.
            else if (msi.ExitCode is not (0 or 1605 or 3010)) problems.Add($"Windows Installer reported error code {msi.ExitCode}.");
            install = find();
        }
        if (install is not null)
        {
            var setup = runSetup(install.Setup);
            if (setup.TimedOut) problems.Add("Edge's uninstaller took too long.");
            else if (setup.ExitCode != 0) problems.Add($"Edge's uninstaller reported error code {setup.ExitCode}." + (setup.Problem.Length > 0 ? " " + Brief(setup.Problem) : ""));
        }
        if (find() is not null) return new(false, "Microsoft Edge is still on this PC." + (problems.Count > 0 ? " " + string.Join(" ", problems) : ""));
        forget();
        return new(true);
    }

    private static DebloatProcess.Result RunEdgeSetup(string setup)
    {
        var stubDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemApps", "Microsoft.MicrosoftEdge_8wekyb3d8bbwe");
        var stub = Path.Combine(stubDirectory, "MicrosoftEdge.exe");
        var madeStub = false;
        try
        {
            using (var update = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdateDev")) update.SetValue("AllowUninstall", 1, RegistryValueKind.DWord);
            if (!File.Exists(stub))
            {
                try { Directory.CreateDirectory(stubDirectory); File.WriteAllBytes(stub, []); madeStub = true; }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
            }
            return DebloatProcess.Run(setup, ["--uninstall", "--system-level", "--verbose-logging", "--force-uninstall"], TimeSpan.FromMinutes(5));
        }
        finally
        {
            if (madeStub) { try { File.Delete(stub); Directory.Delete(stubDirectory); } catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { } }
            using var update = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdateDev", writable: true);
            update?.DeleteValue("AllowUninstall", throwOnMissingValue: false);
        }
    }

    private static void ForgetEdge()
    {
        // Tell the updater not to install Edge again. The WebView2 runtime has its own product id and is not affected.
        using (var policy = Registry.LocalMachine.CreateSubKey(EdgeUpdatePolicy)) policy.SetValue("Install" + EdgeStableId, 0, RegistryValueKind.DWord);

        // Shortcuts shared by everyone, then those of each account: its desktop, Start menu and taskbar pin, and Edge's startup entries.
        var links = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Microsoft Edge.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Microsoft Edge.lnk")
        };
        UserHives.ForEach(account =>
        {
            ForgetRunValues(account.Root, DebloatCleanup.IsEdgeRunValue);
            links.AddRange(DebloatCleanup.EdgeShortcuts(account.Profile));
        });
        foreach (var path in new[] { DebloatCleanup.RunKey, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run" })
        {
            using var run = Registry.LocalMachine.OpenSubKey(path, writable: true);
            if (run is not null) foreach (var name in run.GetValueNames().Where(DebloatCleanup.IsEdgeRunValue).ToList()) run.DeleteValue(name, throwOnMissingValue: false);
        }
        foreach (var link in links)
        {
            try { File.Delete(link); } catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        }
    }

    private static DebloatOutcome AllowEdge()
    {
        using var policy = Registry.LocalMachine.OpenSubKey(EdgeUpdatePolicy, writable: true);
        policy?.DeleteValue("Install" + EdgeStableId, throwOnMissingValue: false);
        return new(true);
    }

    // Recall is switched off as a Windows feature, not deleted, so that it can be switched back on. DISM answers 3010 when the change only completes after a restart, either way.
    private static DebloatOutcome SetRecall(bool enable)
    {
        var dism = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "dism.exe");
        var result = DebloatProcess.Run(dism, ["/Online", enable ? "/Enable-Feature" : "/Disable-Feature", "/FeatureName:Recall", "/NoRestart"], TimeSpan.FromMinutes(5));
        if (result.TimedOut) return new(false, "Windows took too long to change Recall.");
        if (result.ExitCode == 3010) return new(true, null, "Restart Windows to finish.");
        // 0x800f080c: this Windows has no Recall feature, so there is nothing to switch.
        if (result.ExitCode is 0 || result.ExitCode == unchecked((int)0x800F080C)) return new(true);
        return new(false, "Windows would not change Recall. " + Brief(result.Problem));
    }

    private static string Brief(string text) => text.Length <= 200 ? text : text[..200] + "…";
}
