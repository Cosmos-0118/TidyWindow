using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace TidyWindow.Next.Core;

/// <summary>The real PC: the installed-programs list in the registry, Windows Installer's own database, and the processes that run installs.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsInstallSystem : IInstallSystem
{
    private const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Wow64Uninstall = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    private static readonly string[] UpdateTypes = ["Update", "Hotfix", "Security Update", "Update Rollup", "Service Pack"];

    public IReadOnlyList<UninstallEntry> Entries()
    {
        var entries = new List<UninstallEntry>();
        foreach (var (hive, prefix) in new[] { (RegistryHive.LocalMachine, "HKLM"), (RegistryHive.CurrentUser, "HKCU") })
        foreach (var path in new[] { Uninstall, Wow64Uninstall })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var list = root.OpenSubKey(path);
                if (list is null) continue;
                foreach (var name in list.GetSubKeyNames())
                {
                    try
                    {
                        using var key = list.OpenSubKey(name);
                        if (key is null) continue;
                        var parent = Text(key, "ParentKeyName");
                        entries.Add(new($@"{prefix}\{path}\{name}", Text(key, "DisplayName"), Text(key, "DisplayVersion"), Text(key, "Publisher"),
                            Text(key, "UninstallString"), Text(key, "QuietUninstallString"), Text(key, "InstallLocation"), Text(key, "DisplayIcon"),
                            Flag(key, "WindowsInstaller"), Flag(key, "SystemComponent"), Flag(key, "NoRemove"),
                            parent.Length > 0 || Array.Exists(UpdateTypes, x => string.Equals(x, Text(key, "ReleaseType"), StringComparison.OrdinalIgnoreCase))));
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
        return entries;
    }

    private static string Text(RegistryKey key, string name) => key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
    private static bool Flag(RegistryKey key, string name)
    {
        try { return Convert.ToInt32(key.GetValue(name) ?? 0) == 1; }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return false; }
    }

    public IReadOnlyList<MsiProduct> Products()
    {
        var products = new List<MsiProduct>();
        var code = new StringBuilder(40);
        var sid = new StringBuilder(256);
        for (uint index = 0; ; index++)
        {
            uint sidLength = (uint)sid.Capacity;
            code.Clear(); sid.Clear();
            var result = MsiEnumProductsEx(null, null, AllContexts, index, code, out var context, sid, ref sidLength);
            if (result == NoMoreItems) break;
            if (result != 0) continue;
            // Products managed by an administrator's policy are left alone; they are not what a stalled install leaves behind.
            if (context is not (MachineContext or UserContext)) continue;
            var product = code.ToString();
            products.Add(new(product, context == MachineContext ? "Machine" : "User", sid.ToString(), Info(product, sid.ToString(), context, "ProductName") ?? "", Info(product, sid.ToString(), context, "LocalPackage")));
        }
        return products;
    }

    private static string? Info(string product, string sid, int context, string property)
    {
        uint length = 1024;
        var value = new StringBuilder((int)length);
        var result = MsiGetProductInfoEx(product, context == MachineContext ? null : sid, context, property, value, ref length);
        if (result == MoreData) { value = new StringBuilder((int)length + 1); length++; result = MsiGetProductInfoEx(product, context == MachineContext ? null : sid, context, property, value, ref length); }
        return result == 0 ? value.ToString() : null;
    }

    public int ProductState(string code) => MsiQueryProductState(code);
    public bool FileExists(string path) { try { return File.Exists(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; } }
    public bool FolderExists(string path) { try { return Directory.Exists(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; } }

    public bool DriveAvailable(string path)
    {
        try { return Path.GetPathRoot(path) is { Length: > 0 } root && Directory.Exists(root); }
        catch (Exception ex) when (ex is ArgumentException or IOException) { return true; }
    }

    public string? FindOnPath(string fileName)
    {
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { var full = Path.Combine(Environment.ExpandEnvironmentVariables(folder.Trim().Trim('"')), fileName); if (File.Exists(full)) return full; }
            catch (Exception ex) when (ex is ArgumentException or IOException) { }
        }
        return null;
    }

    public string Expand(string text) => Environment.ExpandEnvironmentVariables(text);

    public bool InstallLockExists()
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\InProgress");
        return key is not null;
    }

    /// <summary>Windows Installer holds this named mutex for as long as it is installing something.</summary>
    public bool InstallerBusy()
    {
        try
        {
            if (!Mutex.TryOpenExisting(@"Global\_MSIExecute", out var mutex)) return false;
            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException) { return true; }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or IOException) { return false; }
    }

    /// <summary>Installers started in the signed-in user's session. The copy in session 0 is the Windows Installer service, which is never listed.</summary>
    public IReadOnlyList<RunningInstaller> Installers()
    {
        var found = new List<RunningInstaller>();
        foreach (var process in Process.GetProcessesByName("msiexec"))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId == 0) continue;
                    DateTimeOffset? started = null;
                    try { started = process.StartTime; } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                    found.Add(new(process.Id, "Windows Installer (msiexec)", started));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return found;
    }

    public IReadOnlyList<string> PendingRestart()
    {
        var reasons = new List<string>();
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using (var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending")) if (key is not null) reasons.Add("Windows servicing");
        using (var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")) if (key is not null) reasons.Add("Windows Update");
        using (var key = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager"))
            if (key?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 }) reasons.Add("files waiting to be replaced");
        return reasons;
    }

    public bool EndProcess(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            // The id could have been reused since the scan, so only a Windows Installer process is ever ended.
            if (!string.Equals(process.ProcessName, "msiexec", StringComparison.OrdinalIgnoreCase)) return false;
            process.Kill();
            return process.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    private const int AllContexts = 7, MachineContext = 4, UserContext = 2, NoMoreItems = 259, MoreData = 234;

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern int MsiQueryProductState(string product);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiEnumProductsEx(string? product, string? userSid, int contexts, uint index, StringBuilder productCode, out int context, StringBuilder sid, ref uint sidLength);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiGetProductInfoEx(string product, string? userSid, int context, string property, StringBuilder value, ref uint length);
}
