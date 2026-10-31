using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
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

    public sealed record Result(int ExitCode, string Output, bool TimedOut);

    public static Result Run(string file, IEnumerable<string> arguments, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{Path.GetFileName(file)} could not be started.");
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        if (!process.WaitForExit(timeout))
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            return new(-1, "", true);
        }
        process.WaitForExit();
        lock (output) return new(process.ExitCode, output.ToString().Trim(), false);
    }

    /// <summary>Runs a script in Windows PowerShell. Its text is passed encoded, so nothing in it is ever read as a command line.</summary>
    public static Result Script(string script, TimeSpan timeout)
    {
        var full = "$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';[Console]::OutputEncoding=[Text.Encoding]::UTF8;" + script;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        return Run(PowerShell, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded], timeout);
    }
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

/// <summary>Reads packages and the state of OneDrive, Edge and Recall from this PC.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDebloatSystem(bool elevated) : IDebloatSystem
{
    private static readonly string[] KnownFolders = ["Desktop", "Personal", "My Pictures"];
    private static readonly string[] KnownFolderNames = ["Desktop", "Documents", "Pictures"];

    public int Build { get; } = Environment.OSVersion.Version.Build;

    public IReadOnlyList<AppxFact> Packages()
    {
        var script = (elevated ? "$p=Get-AppxPackage -AllUsers;" : "$p=Get-AppxPackage;") +
            "$r=@($p|ForEach-Object{[pscustomobject]@{n=$_.Name;f=$_.PackageFullName;y=$_.PackageFamilyName;r=[bool]$_.NonRemovable;w=[bool]$_.IsFramework;" +
            "l=\"$($_.InstallLocation)\";u=@($_.PackageUserInformation|Where-Object{\"$($_.InstallState)\" -eq 'Installed'}|ForEach-Object{\"$($_.UserSecurityID.SID)\"})}});" +
            "ConvertTo-Json -InputObject $r -Compress -Depth 3";
        var result = DebloatProcess.Script(script, TimeSpan.FromSeconds(90));
        if (result.TimedOut || result.ExitCode != 0) throw new InvalidOperationException("Windows did not list the installed apps. " + Brief(result.Output));
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

    public PlaybookFacts Playbooks()
    {
        var folders = new List<string>();
        foreach (var sid in Registry.Users.GetSubKeyNames().Where(x => x.StartsWith("S-1-5-21", StringComparison.Ordinal) && !x.EndsWith("_Classes", StringComparison.Ordinal)))
        {
            try
            {
                using var key = Registry.Users.OpenSubKey(sid + @"\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
                if (key is null) continue;
                for (var i = 0; i < KnownFolders.Length; i++)
                    if (key.GetValue(KnownFolders[i], null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string path && path.Contains("OneDrive", StringComparison.OrdinalIgnoreCase) && !folders.Contains(KnownFolderNames[i]))
                        folders.Add(KnownFolderNames[i]);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return new(OneDriveInstalled(), folders, EdgeUninstaller() is not null, OtherBrowserInstalled(), RecallInstalled());
    }

    internal static bool OneDriveInstalled()
    {
        var programFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft OneDrive");
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "OneDrive");
        return OneDriveDetection.IsInstalled([programFiles, local], File.Exists, Directory.Exists, UninstallEntryExists);
    }

    private static bool UninstallEntryExists()
    {
        const string path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\OneDriveSetup.exe";
        using (var user = Registry.CurrentUser.OpenSubKey(path)) if (user is not null) return true;
        using (var machine = Registry.LocalMachine.OpenSubKey(path)) if (machine is not null) return true;
        using var machine32 = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\OneDriveSetup.exe");
        return machine32 is not null;
    }

    /// <summary>The uninstall command Edge registered, or null when Edge is not installed.</summary>
    internal static string? EdgeUninstaller()
    {
        foreach (var path in new[] { @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Microsoft Edge", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Microsoft Edge" })
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            if (key?.GetValue("UninstallString") is string text && text.Length > 0) return text;
        }
        return null;
    }

    /// <summary>Another web browser is registered with Windows. Edge and Internet Explorer do not count.</summary>
    internal static bool OtherBrowserInstalled()
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            foreach (var path in new[] { @"SOFTWARE\Clients\StartMenuInternet", @"SOFTWARE\WOW6432Node\Clients\StartMenuInternet" })
            {
                using var key = hive.OpenSubKey(path);
                if (key?.GetSubKeyNames().Any(x => !x.StartsWith("Microsoft Edge", StringComparison.OrdinalIgnoreCase) && !x.StartsWith("IEXPLORE", StringComparison.OrdinalIgnoreCase)) == true) return true;
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
    public string? ClearNonRemovable(string family)
    {
        if (!DebloatGuard.IsToken(family)) return "Not a package family.";
        var dism = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "dism.exe");
        var result = DebloatProcess.Run(dism, ["/Online", "/Set-NonRemovableAppPolicy", $"/PackageFamily:{family}", "/NonRemovable:0", "/NoRestart"], TimeSpan.FromMinutes(2));
        return result.TimedOut ? "Windows took too long to unlock the app." : result.ExitCode is 0 or 3010 ? null : Brief("Windows would not unlock the app. " + result.Output);
    }

    public string? RemovePackage(string fullName, IReadOnlyList<string> sids)
    {
        if (!DebloatGuard.IsToken(fullName) || sids.Any(x => !DebloatGuard.IsSid(x))) return "Not a package name.";
        // Everyone first: removing it for one account first leaves Windows holding the files for itself, and its "all users" removal then has nothing to finish.
        // Each account is tried afterwards, for a package that is still registered for some of them.
        var script = new StringBuilder($"$e=@();try{{Remove-AppxPackage -Package '{fullName}' -AllUsers}}catch{{$e+=$_.Exception.Message}};");
        if (sids.Count > 0)
        {
            script.Append($"if(Get-AppxPackage -AllUsers|Where-Object{{$_.PackageFullName -eq '{fullName}'}}){{");
            foreach (var sid in sids) script.Append($"try{{Remove-AppxPackage -Package '{fullName}' -User '{sid}'}}catch{{$e+=$_.Exception.Message}};");
            script.Append('}');
        }
        script.Append("($e -join ' | ')");
        var result = DebloatProcess.Script(script.ToString(), TimeSpan.FromMinutes(3));
        if (result.TimedOut) return "Windows took too long to remove the app.";
        return result.Output.Length == 0 ? null : Brief(result.Output);
    }

    public string? RemoveProvisioned(string packageName)
    {
        if (!DebloatGuard.IsToken(packageName)) return "Not a package name.";
        var result = DebloatProcess.Script($"try{{Remove-AppxProvisionedPackage -Online -AllUsers -PackageName '{packageName}'|Out-Null}}catch{{$_.Exception.Message}}", TimeSpan.FromMinutes(3));
        if (result.TimedOut) return "Windows took too long to remove the app.";
        return result.Output.Length == 0 ? null : Brief(result.Output);
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
/// and none of them deletes a file of yours.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPlaybooks : IDebloatPlaybooks
{
    private const string EdgeStableId = "{56EB18F8-B008-4CBD-B6D2-8C97FE7E9062}";
    private const string EdgeUpdatePolicy = @"SOFTWARE\Policies\Microsoft\EdgeUpdate";

    public string? Remove(DebloatKind kind) => kind switch
    {
        DebloatKind.OneDrive => RemoveOneDrive(),
        DebloatKind.Edge => RemoveEdge(),
        DebloatKind.Recall => SetRecall(enable: false),
        _ => "That is not a component."
    };

    public string? Restore(DebloatKind kind) => kind switch
    {
        DebloatKind.OneDrive => null,  // The installer comes from WinGet.
        DebloatKind.Edge => AllowEdge(),
        DebloatKind.Recall => SetRecall(enable: true),
        _ => "That is not a component."
    };

    internal static IEnumerable<string> OneDriveSetups()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        yield return Path.Combine(windows, "SysWOW64", "OneDriveSetup.exe");
        yield return Path.Combine(windows, "System32", "OneDriveSetup.exe");
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var root in new[] { Path.Combine(local, "Microsoft", "OneDrive"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft OneDrive") })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var setup in Directory.EnumerateFiles(root, "OneDriveSetup.exe", SearchOption.AllDirectories).OrderByDescending(x => x)) yield return setup;
        }
    }

    // OneDrive: stop it, run its own uninstaller, and stop Windows adding it to new accounts. Files in the OneDrive folder are left exactly where they are.
    private static string? RemoveOneDrive()
    {
        foreach (var name in new[] { "OneDrive", "OneDriveStandaloneUpdater", "FileCoAuth" })
            foreach (var process in Process.GetProcessesByName(name))
            {
                try { process.Kill(); process.WaitForExit(5000); }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
                finally { process.Dispose(); }
            }
        var setup = OneDriveSetups().FirstOrDefault(File.Exists);
        if (setup is null) return "OneDrive's own uninstaller was not found.";
        var result = DebloatProcess.Run(setup, ["/uninstall"], TimeSpan.FromMinutes(3));
        if (result.TimedOut) return "OneDrive took too long to uninstall.";

        // Do not start it again at sign-in, for any account on this PC or for accounts made later. The helper may be running as another administrator
        // than the people it is cleaning for, so the current user's registry is not enough: every account is visited, and what is not signed in is loaded for a moment.
        UserHives.ForEach(account =>
        {
            ForgetRunValues(account.Root, DebloatCleanup.IsOneDriveRunValue);
            if (account.Classes is { } classes) HideOneDriveInSideBar(classes);
        });
        ForgetOneDriveInNewAccounts();
        RemoveOneDriveTasks();
        return null;
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
        var listed = DebloatProcess.Script("ConvertTo-Json -InputObject @(Get-ScheduledTask|Where-Object{$_.TaskName -like 'OneDrive*'}|ForEach-Object{$_.TaskName}) -Compress", TimeSpan.FromSeconds(60));
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
        DebloatProcess.Script(string.Concat(names.Select(x => $"try{{Unregister-ScheduledTask -TaskName '{x}' -Confirm:$false}}catch{{}};")), TimeSpan.FromSeconds(60));
    }

    private static void ForgetOneDriveInNewAccounts()
    {
        var hive = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "..", "Default", "NTUSER.DAT");
        hive = Path.GetFullPath(hive);
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
    private static string? RemoveEdge()
    {
        if (!WindowsDebloatSystem.OtherBrowserInstalled()) return DebloatScan.OnlyBrowserLock;
        var command = WindowsDebloatSystem.EdgeUninstaller();
        if (command is null) return null;
        var setup = command.Trim().StartsWith('"') ? command.Trim().Split('"')[1] : command.Trim().Split(' ')[0];
        if (!File.Exists(setup) || !Path.GetFileName(setup).Equals("setup.exe", StringComparison.OrdinalIgnoreCase)) return "Edge's own uninstaller was not found.";

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
            DebloatProcess.Run(setup, ["--uninstall", "--system-level", "--verbose-logging", "--force-uninstall"], TimeSpan.FromMinutes(5));
        }
        finally
        {
            if (madeStub) { try { File.Delete(stub); Directory.Delete(stubDirectory); } catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { } }
            using var update = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdateDev", writable: true);
            update?.DeleteValue("AllowUninstall", throwOnMissingValue: false);
        }

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
        return null;
    }

    private static string? AllowEdge()
    {
        using var policy = Registry.LocalMachine.OpenSubKey(EdgeUpdatePolicy, writable: true);
        policy?.DeleteValue("Install" + EdgeStableId, throwOnMissingValue: false);
        return null;
    }

    // Recall is switched off as a Windows feature, not deleted, so that it can be switched back on.
    private static string? SetRecall(bool enable)
    {
        var dism = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "dism.exe");
        var result = DebloatProcess.Run(dism, ["/Online", enable ? "/Enable-Feature" : "/Disable-Feature", "/FeatureName:Recall", "/NoRestart"], TimeSpan.FromMinutes(5));
        if (result.TimedOut) return "Windows took too long to change Recall.";
        if (result.ExitCode is 0 or 3010) return null;
        // 0x800f080c: this Windows has no Recall feature, so there is nothing to switch.
        if (result.ExitCode == unchecked((int)0x800F080C)) return null;
        return "Windows would not change Recall. " + (result.Output.Length <= 200 ? result.Output : result.Output[..200] + "…");
    }
}
