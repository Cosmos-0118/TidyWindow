using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

namespace TidyWindow.Next.Core;

/// <summary>The real PC: the registry, the Startup folders, Task Scheduler, the service list and the Store apps' startup tasks.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsStartupSystem : IStartupSystem
{
    private const string RunPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string Wow64RunPath = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    internal const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    internal const string StoreRoot = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";
    private const string ServicesPath = @"SYSTEM\CurrentControlSet\Services";

    public StartupPlaces Places { get; } = CreatePlaces();

    private static StartupPlaces CreatePlaces()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var data = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return new(windows, profile, [Path.GetTempPath(), Path.Combine(windows, "Temp"), Path.Combine(profile, "Downloads"), Path.Combine(Path.GetDirectoryName(profile) ?? profile, "Public"), Path.Combine(data, "Temp")]);
    }

    public IReadOnlyList<RunValue> RunValues()
    {
        var values = new List<RunValue>();
        foreach (var (hive, scope, path, wow) in new[]
        {
            (RegistryHive.CurrentUser, StartupScope.User, RunPath, false),
            (RegistryHive.LocalMachine, StartupScope.Machine, RunPath, false),
            (RegistryHive.LocalMachine, StartupScope.Machine, Wow64RunPath, true)
        })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = root.OpenSubKey(path);
                if (key is null) continue;
                foreach (var name in key.GetValueNames())
                    if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string command && name.Length > 0)
                        values.Add(new(scope, wow, name, command));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
        return values;
    }

    public byte[]? Approved(StartupScope scope, string subKey, string name)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(scope == StartupScope.User ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey($@"{ApprovedPath}\{subKey}");
            return key?.GetValue(name) as byte[];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return null; }
    }

    public IReadOnlyList<FolderItem> FolderItems()
    {
        var items = new List<FolderItem>();
        foreach (var (scope, folder) in new[] { (StartupScope.User, Environment.GetFolderPath(Environment.SpecialFolder.Startup)), (StartupScope.Machine, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)) })
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    var name = Path.GetFileName(file);
                    if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    var (target, arguments) = Path.GetExtension(file).Equals(".lnk", StringComparison.OrdinalIgnoreCase) ? Shortcut(file) : (file, "");
                    items.Add(new(scope, name, file, target, arguments));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        }
        return items;
    }

    private static (string Target, string Arguments) Shortcut(string file)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return ("", "");
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic link = shell.CreateShortcut(file);
            return ((string)link.TargetPath ?? "", (string)link.Arguments ?? "");
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { return ("", ""); }
    }

    public (IReadOnlyList<ScheduledStartup> Tasks, int WindowsOwned) Tasks()
    {
        var found = new List<ScheduledStartup>();
        var owned = 0;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) return (found, 0);
            dynamic service = Activator.CreateInstance(type)!;
            service.Connect();
            Walk(service.GetFolder("\\"), found, ref owned);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        return (found, owned);
    }

    private static void Walk(dynamic folder, List<ScheduledStartup> found, ref int owned)
    {
        foreach (var task in folder.GetTasks(1))
        {
            try
            {
                string path = task.Path;
                if (path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)) { owned++; continue; }
                dynamic definition = task.Definition;
                bool logon = false, boot = false;
                foreach (var trigger in definition.Triggers)
                {
                    int kind = trigger.Type;
                    logon |= kind == 9; boot |= kind == 8;
                }
                if (!logon && !boot) continue;
                string program = "", arguments = "";
                foreach (var action in definition.Actions)
                {
                    if ((int)action.Type != 0) continue;
                    program = (string)action.Path ?? ""; arguments = (string)action.Arguments ?? "";
                    break;
                }
                string runAs = (string)definition.Principal.UserId ?? (string)definition.Principal.GroupId ?? "";
                found.Add(new(path, (string)task.Name, (string)definition.RegistrationInfo.Description ?? "", (string)definition.RegistrationInfo.Author ?? "", (bool)task.Enabled, program, arguments, logon, boot, runAs));
            }
            catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException) { }
        }
        foreach (var child in folder.GetFolders(0))
        {
            string name = child.Path;
            if (name.Equals(@"\Microsoft", StringComparison.OrdinalIgnoreCase)) { owned += Count(child); continue; }
            Walk(child, found, ref owned);
        }
    }

    private static int Count(dynamic folder)
    {
        var count = 0;
        try
        {
            count += (int)folder.GetTasks(1).Count;
            foreach (var child in folder.GetFolders(0)) count += Count(child);
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        return count;
    }

    public IReadOnlyList<ServiceFacts> Services()
    {
        var services = new List<ServiceFacts>();
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var list = root.OpenSubKey(ServicesPath);
            if (list is null) return services;
            foreach (var name in list.GetSubKeyNames())
            {
                try
                {
                    using var key = list.OpenSubKey(name);
                    if (key is null || key.GetValue("Type") is not int type || (type & 0x30) == 0) continue;
                    var start = key.GetValue("Start") as int? ?? 3;
                    var depends = key.GetValue("DependOnService") as string[] ?? [];
                    services.Add(new(name, Text(key, "DisplayName"), Text(key, "Description"), Text(key, "ImagePath"), type, start, key.GetValue("DelayedAutostart") as int? == 1, Text(key, "ObjectName"), depends));
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        return services;
    }

    private static string Text(RegistryKey key, string name) => key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";

    public IReadOnlyList<StoreStartup> StoreTasks()
    {
        var tasks = new List<StoreStartup>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(StoreRoot);
            if (root is null) return tasks;
            foreach (var family in root.GetSubKeyNames())
            {
                using var package = root.OpenSubKey(family);
                if (package is null) continue;
                using var schema = package.OpenSubKey("Schemas");
                var full = schema?.GetValue("PackageFullName") as string ?? "";
                Manifest? manifest = null;
                foreach (var id in package.GetSubKeyNames())
                {
                    using var task = package.OpenSubKey(id);
                    if (task?.GetValue("State") is not int state) continue;
                    manifest ??= ReadManifest(full);
                    tasks.Add(new(family, id, state, full, manifest.DisplayName, manifest.Publisher, manifest.ExecutableFor(id)));
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        return tasks;
    }

    private sealed record Manifest(string DisplayName, string Publisher, string Folder, IReadOnlyDictionary<string, string> Executables, string Main = "")
    {
        // A startup task that names no program belongs to the package's main app.
        public string ExecutableFor(string taskId) => Folder.Length > 0 && (Executables.TryGetValue(taskId, out var exe) ? exe : Main) is { Length: > 0 } found ? Path.Combine(Folder, found) : "";
    }

    private static Manifest ReadManifest(string packageFullName)
    {
        var none = new Manifest("", "", "", new Dictionary<string, string>());
        if (packageFullName.Length == 0 || packageFullName.IndexOfAny(['\\', '/']) >= 0) return none;
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps", packageFullName);
            var file = Path.Combine(folder, "AppxManifest.xml");
            if (!File.Exists(file)) return none;
            var document = XDocument.Load(file);
            var properties = document.Descendants().FirstOrDefault(x => x.Name.LocalName == "Properties");
            string Read(string name) => properties?.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value ?? "";
            var display = Read("DisplayName");
            var publisher = Read("PublisherDisplayName");
            var executables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var task in document.Descendants().Where(x => x.Name.LocalName == "StartupTask"))
                // The program is named on the task itself, or on the extension that holds it (uap5:Extension Executable="app\Tool.exe").
                if (task.Attribute("TaskId")?.Value is { } id && (task.Attribute("Executable") ?? task.Parent?.Attribute("Executable"))?.Value is { } exe) executables[id] = exe;
            if (display.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) display = "";
            if (publisher.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) publisher = "";
            var main = document.Descendants().FirstOrDefault(x => x.Name.LocalName == "Application")?.Attribute("Executable")?.Value ?? "";
            return new(display, publisher, folder, executables, main);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) { return none; }
    }

    public IReadOnlyList<WindowsSetting> WindowsSettings()
    {
        var settings = new List<WindowsSetting>();
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var logon = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon");
            if (logon is not null)
            {
                settings.Add(new("Winlogon.Shell", "Windows desktop (Shell)", Text(logon, "Shell"), "explorer.exe", @"HKLM\...\Winlogon"));
                settings.Add(new("Winlogon.Userinit", "Sign-in program (Userinit)", Text(logon, "Userinit"), Path.Combine(Environment.SystemDirectory, "userinit.exe") + ",", @"HKLM\...\Winlogon"));
                if (Text(logon, "Taskman") is { Length: > 0 } taskman) settings.Add(new("Winlogon.Taskman", "Replacement Task Manager (Taskman)", taskman, "", @"HKLM\...\Winlogon"));
            }
            using var windows = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows");
            if (windows is not null && Text(windows, "AppInit_DLLs") is { Length: > 0 } appInit)
                settings.Add(new("Windows.AppInit", "Libraries loaded into every program (AppInit_DLLs)", appInit, "", @"HKLM\...\Windows"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        return settings;
    }

    public FileFacts Describe(string path)
    {
        try
        {
            if (!File.Exists(path)) return new(false, "", "", "", StartupTrust.Unknown);
            var info = FileVersionInfo.GetVersionInfo(path);
            return new(true, Clean(info.CompanyName), Clean(info.FileDescription), Clean(info.ProductName), Trust(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return new(File.Exists(path), "", "", "", StartupTrust.Unknown); }
    }

    private static string Clean(string? text) => text?.Trim() ?? "";

    public string Expand(string text) => Environment.ExpandEnvironmentVariables(text);
    public bool FileExists(string path) { try { return File.Exists(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; } }

    public string? FindOnPath(string name)
    {
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Append(Environment.SystemDirectory);
        foreach (var folder in folders)
        {
            try
            {
                var candidate = Path.Combine(folder.Trim().Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    public ISet<string> RunningImages()
    {
        var images = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var handle = OpenProcess(0x1000, false, process.Id);
                    if (handle == IntPtr.Zero) continue;
                    try
                    {
                        var buffer = new StringBuilder(1024);
                        var size = buffer.Capacity;
                        if (QueryFullProcessImageName(handle, 0, buffer, ref size)) images.Add(buffer.ToString());
                    }
                    finally { CloseHandle(handle); }
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            }
        }
        return images;
    }

    // ---- Authenticode: is the file signed, and does the signature check out? ----

    private static StartupTrust Trust(string path)
    {
        var file = IntPtr.Zero; var data = IntPtr.Zero; var name = IntPtr.Zero;
        try
        {
            name = Marshal.StringToHGlobalUni(path);
            var info = new WinTrustFileInfo { Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(), FilePath = name };
            file = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(info, file, false);
            var trust = new WinTrustData { Size = (uint)Marshal.SizeOf<WinTrustData>(), UiChoice = 2, RevocationChecks = 0, UnionChoice = 1, File = file, ProviderFlags = 0x1000 | 0x10 };
            data = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(trust, data, false);
            var result = WinVerifyTrust(new IntPtr(-1), WintrustActionGenericVerifyV2, data);
            return result switch
            {
                0 => StartupTrust.Verified,
                unchecked((int)0x800B0100) or unchecked((int)0x800B0001) or unchecked((int)0x800B0003) => StartupTrust.Unsigned,
                _ => StartupTrust.Invalid
            };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException) { return StartupTrust.Unknown; }
        finally
        {
            if (data != IntPtr.Zero) Marshal.FreeHGlobal(data);
            if (file != IntPtr.Zero) Marshal.FreeHGlobal(file);
            if (name != IntPtr.Zero) Marshal.FreeHGlobal(name);
        }
    }

    private static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo { public uint Size; public IntPtr FilePath; public IntPtr FileHandle; public IntPtr KnownSubject; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size; public IntPtr PolicyCallbackData; public IntPtr SipClientData; public uint UiChoice; public uint RevocationChecks; public uint UnionChoice;
        public IntPtr File; public uint StateAction; public IntPtr StateData; public IntPtr UrlReference; public uint ProviderFlags; public uint UiContext; public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, [MarshalAs(UnmanagedType.LPStruct)] Guid action, IntPtr data);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>The only writes the startup manager makes: Explorer's on/off record, a task's Enabled flag, a service's start type, and a Store app's startup state.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsStartupWriter(Func<DateTimeOffset>? clock = null) : IStartupWriter
{
    private static readonly string[] ApprovedKeys = ["Run", "Run32", "StartupFolder"];
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.Now);

    public void SetApproved(StartupScope scope, string subKey, string name, bool enabled)
    {
        if (Array.IndexOf(ApprovedKeys, subKey) < 0 || string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['\\', '/']) >= 0) throw new InvalidOperationException("That startup item cannot be changed.");
        try
        {
            using var root = RegistryKey.OpenBaseKey(scope == StartupScope.User ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.CreateSubKey($@"{WindowsStartupSystem.ApprovedPath}\{subKey}", writable: true);
            key.SetValue(name, StartupApproved.Make(enabled, now()), RegistryValueKind.Binary);
        }
        catch (System.Security.SecurityException ex) { throw new UnauthorizedAccessException(ex.Message, ex); }
    }

    public void SetTask(string path, bool enabled)
    {
        if (path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase) || path.Length < 2 || path[0] != '\\') throw new InvalidOperationException("That task cannot be changed.");
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler is not available.");
            dynamic service = Activator.CreateInstance(type)!;
            service.Connect();
            var split = path.LastIndexOf('\\');
            dynamic task = service.GetFolder(split == 0 ? "\\" : path[..split]).GetTask(path[(split + 1)..]);
            task.Enabled = enabled;
        }
        catch (COMException ex) when ((uint)ex.HResult == 0x80070005) { throw new UnauthorizedAccessException(ex.Message, ex); }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException ex) when (ex.Message.Contains("0x80070005", StringComparison.Ordinal) || ex.Message.Contains("denied", StringComparison.OrdinalIgnoreCase)) { throw new UnauthorizedAccessException(ex.Message, ex); }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.\- ]{1,256}$")]
    private static partial Regex ServiceName();

    public void SetService(string name, int start, bool delayed)
    {
        if (!ServiceName().IsMatch(name) || start is not (2 or 3)) throw new InvalidOperationException("That service cannot be changed.");
        var manager = OpenSCManager(null, null, 0x0001);
        if (manager == IntPtr.Zero) throw Failure();
        try
        {
            var service = OpenService(manager, name, 0x0001 | 0x0002);
            if (service == IntPtr.Zero) throw Failure();
            try
            {
                if (!ChangeServiceConfig(service, 0xFFFFFFFF, (uint)start, 0xFFFFFFFF, null, null, IntPtr.Zero, null, null, null, null)) throw Failure();
                var info = new DelayedAutoStart { Delayed = delayed && start == 2 };
                if (!ChangeServiceConfig2(service, 3, ref info)) throw Failure();
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    private static Exception Failure()
    {
        var code = Marshal.GetLastWin32Error();
        return code == 5 ? new UnauthorizedAccessException("Administrator permission is needed to change a service.") : new Win32Exception(code);
    }

    public void SetStoreTask(string family, string taskId, bool enabled)
    {
        if (family.Length == 0 || taskId.Length == 0 || family.IndexOfAny(['\\', '/']) >= 0 || taskId.IndexOfAny(['\\', '/']) >= 0) throw new InvalidOperationException("That app's startup task cannot be changed.");
        using var key = Registry.CurrentUser.OpenSubKey($@"{WindowsStartupSystem.StoreRoot}\{family}\{taskId}", writable: true) ?? throw new InvalidOperationException("That app's startup task was not found.");
        if (key.GetValue("State") is not int) throw new InvalidOperationException("That app's startup task was not found.");
        key.SetValue("State", enabled ? 2 : 1, RegistryValueKind.DWord);
        if (!enabled) key.SetValue("LastDisabledTime", (int)now().ToUnixTimeSeconds(), RegistryValueKind.DWord);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DelayedAutoStart { [MarshalAs(UnmanagedType.Bool)] public bool Delayed; }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr manager, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig(IntPtr service, uint type, uint start, uint errorControl, string? path, string? group, IntPtr tag, string? dependencies, string? account, string? password, string? displayName);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2(IntPtr service, uint level, ref DelayedAutoStart info);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
