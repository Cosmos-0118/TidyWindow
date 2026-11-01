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
    internal const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    internal const string StoreRoot = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";
    private const string RepositoryRoot = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
    private const string ServicesPath = @"SYSTEM\CurrentControlSet\Services";

    // Every Run-style key Windows reads at sign-in. Which user or machine copy is read, and where its on/off record lives, is decided by the source name.
    private static readonly (RegistryHive Hive, StartupScope Scope, string Source)[] RunLocations =
    [
        (RegistryHive.CurrentUser, StartupScope.User, StartupLocations.Run),
        (RegistryHive.LocalMachine, StartupScope.Machine, StartupLocations.Run),
        (RegistryHive.CurrentUser, StartupScope.User, StartupLocations.Run32),
        (RegistryHive.LocalMachine, StartupScope.Machine, StartupLocations.Run32),
        (RegistryHive.CurrentUser, StartupScope.User, StartupLocations.RunOnce),
        (RegistryHive.LocalMachine, StartupScope.Machine, StartupLocations.RunOnce),
        (RegistryHive.CurrentUser, StartupScope.User, StartupLocations.RunOnce32),
        (RegistryHive.LocalMachine, StartupScope.Machine, StartupLocations.RunOnce32),
        (RegistryHive.CurrentUser, StartupScope.User, StartupLocations.PolicyRun),
        (RegistryHive.LocalMachine, StartupScope.Machine, StartupLocations.PolicyRun)
    ];

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
        foreach (var (hive, scope, source) in RunLocations)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = root.OpenSubKey(StartupLocations.PathFor(source)!);
                if (key is null) continue;
                foreach (var name in key.GetValueNames())
                    if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string command && name.Length > 0)
                        values.Add(new(scope, source is StartupLocations.Run32 or StartupLocations.RunOnce32, name, command, source, key.GetValueKind(name) == RegistryValueKind.ExpandString));
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
                if (path.StartsWith(WindowsTaskFolder + @"\", StringComparison.OrdinalIgnoreCase)) { owned++; continue; }
                dynamic definition = task.Definition;
                bool logon = false, boot = false;
                foreach (var trigger in definition.Triggers)
                {
                    // A trigger that is switched off never fires, whatever it is for.
                    if (!(bool)trigger.Enabled) continue;
                    int kind = trigger.Type;
                    // Type 9 is "at log on"; type 11 is a session change, and connecting to the console or a remote session is how some apps ask to start at sign-in.
                    logon |= kind == 9 || (kind == 11 && (int)trigger.StateChange is 1 or 3);
                    boot |= kind == 8;
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
            if (name.Equals(WindowsTaskFolder, StringComparison.OrdinalIgnoreCase)) { owned += Count(child); continue; }
            Walk(child, found, ref owned);
        }
    }

    // Windows' own tasks. The rest of \Microsoft (Office, Visual Studio...) holds ordinary programs' tasks and is read like any other folder.
    internal const string WindowsTaskFolder = @"\Microsoft\Windows";

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
                    var image = Text(key, "ImagePath");
                    // Windows starts a service with a trigger when its event happens, even when the start type is Manual.
                    var triggered = key.GetSubKeyNames().Contains("TriggerInfo", StringComparer.OrdinalIgnoreCase);
                    var library = "";
                    if (image.Contains("svchost", StringComparison.OrdinalIgnoreCase))
                    {
                        using var parameters = key.OpenSubKey("Parameters");
                        library = parameters is null ? "" : Text(parameters, "ServiceDll");
                    }
                    services.Add(new(name, Text(key, "DisplayName"), Text(key, "Description"), image, type, start, key.GetValue("DelayedAutostart") as int? == 1, Text(key, "ObjectName"), depends, triggered, library));
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
            var registered = RegisteredPackages();
            foreach (var family in root.GetSubKeyNames())
            {
                using var package = root.OpenSubKey(family);
                if (package is null) continue;
                // Uninstalling a Store app leaves its startup task behind. When Windows knows the packages of this user and none belongs to the family, the app is gone.
                var current = registered.Count == 0 ? null : registered.Where(x => InFamily(x.Key, family)).OrderByDescending(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => ((string Key, string Root, string DisplayName)?)x).FirstOrDefault();
                if (registered.Count > 0 && current is null) continue;
                using var schema = package.OpenSubKey("Schemas");
                var full = current?.Key ?? schema?.GetValue("PackageFullName") as string ?? "";
                Manifest? manifest = null;
                foreach (var id in package.GetSubKeyNames())
                {
                    using var task = package.OpenSubKey(id);
                    if (task?.GetValue("State") is not int state) continue;
                    manifest ??= ReadManifest(full, current?.Root);
                    // Windows keeps the already-translated app name next to the package; the manifest may only hold a reference to a resource.
                    var name = ResolveName(full, current?.DisplayName ?? "") is { Length: > 0 } cached ? cached : manifest.DisplayName.Length > 0 ? manifest.DisplayName : manifest.TaskNames.GetValueOrDefault(id, "");
                    tasks.Add(new(family, id, state, full, name, manifest.Publisher, manifest.ExecutableFor(id)));
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        return tasks;
    }

    // The packages registered for this user: full name, folder and the name Windows shows. Empty when Windows' list cannot be read.
    private static List<(string Key, string Root, string DisplayName)> RegisteredPackages()
    {
        var list = new List<(string, string, string)>();
        try
        {
            using var repository = Registry.CurrentUser.OpenSubKey(RepositoryRoot);
            if (repository is null) return list;
            foreach (var name in repository.GetSubKeyNames())
            {
                using var key = repository.OpenSubKey(name);
                list.Add((name, key?.GetValue("PackageRootFolder") as string ?? "", key?.GetValue("DisplayName") as string ?? ""));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        return list;
    }

    // A package full name is "Name_Version_Architecture_ResourceId_PublisherId"; its family is "Name_PublisherId".
    private static bool InFamily(string fullName, string family)
    {
        var split = family.LastIndexOf('_');
        return split > 0 && fullName.StartsWith(family[..split] + "_", StringComparison.OrdinalIgnoreCase) && fullName.EndsWith(family[split..], StringComparison.OrdinalIgnoreCase);
    }

    private sealed record Manifest(string DisplayName, string Publisher, string Folder, IReadOnlyDictionary<string, string> Executables, string Main = "", IReadOnlyDictionary<string, string>? Names = null)
    {
        public IReadOnlyDictionary<string, string> TaskNames => Names ?? new Dictionary<string, string>();
        // A startup task that names no program belongs to the package's main app.
        public string ExecutableFor(string taskId) => Folder.Length > 0 && (Executables.TryGetValue(taskId, out var exe) ? exe : Main) is { Length: > 0 } found ? Path.Combine(Folder, found) : "";
    }

    private static Manifest ReadManifest(string packageFullName, string? packageRoot = null)
    {
        var none = new Manifest("", "", "", new Dictionary<string, string>());
        if (packageFullName.Length == 0 || packageFullName.IndexOfAny(['\\', '/']) >= 0) return none;
        try
        {
            var folder = !string.IsNullOrWhiteSpace(packageRoot) ? packageRoot : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps", packageFullName);
            var file = Path.Combine(folder, "AppxManifest.xml");
            if (!File.Exists(file)) return none;
            var document = XDocument.Load(file);
            var properties = document.Descendants().FirstOrDefault(x => x.Name.LocalName == "Properties");
            string Read(string name) => properties?.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value ?? "";
            string Resolve(string text) => ResolveName(packageFullName, text);
            var display = Resolve(Read("DisplayName"));
            var publisher = Resolve(Read("PublisherDisplayName"));
            var executables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var task in document.Descendants().Where(x => x.Name.LocalName == "StartupTask"))
            {
                if (task.Attribute("TaskId")?.Value is not { } id) continue;
                // The program is named on the task itself, or on the extension that holds it (uap5:Extension Executable="app\Tool.exe").
                if ((task.Attribute("Executable") ?? task.Parent?.Attribute("Executable"))?.Value is { } exe) executables[id] = exe;
                if (task.Attribute("DisplayName")?.Value is { Length: > 0 } taskName && Resolve(taskName) is { Length: > 0 } shown) names[id] = shown;
            }
            var main = document.Descendants().FirstOrDefault(x => x.Name.LocalName == "Application")?.Attribute("Executable")?.Value ?? "";
            return new(display, publisher, folder, executables, main, names);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) { return none; }
    }

    // "ms-resource:AppName" points into the package's own resources and "@{package?...}" is Windows' form of the same; both are looked up, and a name that cannot be read is left blank.
    private static string ResolveName(string packageFullName, string text)
    {
        text = text.Trim();
        if (text.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) && packageFullName.Length > 0) text = $"@{{{packageFullName}?{text}}}";
        if (text.Length == 0) return "";
        var found = LoadIndirect(text);
        return found.Length > 0 && found[0] != '@' && !found.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? found : "";
    }

    public IReadOnlyList<string> Subfolders(string folder)
    {
        try { return Directory.Exists(folder) ? Directory.GetDirectories(folder) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    /// <summary>Reads the text a resource reference such as <c>@%SystemRoot%\system32\x.dll,-101</c> or <c>@{package?ms-resource:Name}</c> points to; returns the text unchanged when it cannot.</summary>
    public string Localize(string text) => LoadIndirect(text);

    private static string LoadIndirect(string text)
    {
        if (text.Length == 0 || text[0] != '@') return text;
        var buffer = new StringBuilder(512);
        try { return SHLoadIndirectString(text, buffer, (uint)buffer.Capacity, IntPtr.Zero) == 0 && buffer.Length > 0 ? buffer.ToString() : text; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException) { return text; }
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHLoadIndirectString(string source, StringBuilder output, uint size, IntPtr reserved);

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

        // The same settings exist per user and need no administrator rights to change, so they are a favourite hiding place. Normally they are empty, and only shown when they are not.
        try
        {
            using var logon = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon");
            if (logon is not null)
            {
                if (Text(logon, "Shell") is { Length: > 0 } shell) settings.Add(new("User.Winlogon.Shell", "Your own desktop program (Shell)", shell, "", @"HKCU\...\Winlogon", StartupScope.User));
                if (Text(logon, "Userinit") is { Length: > 0 } userinit) settings.Add(new("User.Winlogon.Userinit", "Your own sign-in program (Userinit)", userinit, "", @"HKCU\...\Winlogon", StartupScope.User));
            }
            using var windows = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows");
            if (windows is not null)
            {
                if (Text(windows, "Load") is { Length: > 0 } load) settings.Add(new("User.Windows.Load", "Program started for you at sign-in (Load)", load, "", @"HKCU\...\Windows", StartupScope.User));
                if (Text(windows, "Run") is { Length: > 0 } run) settings.Add(new("User.Windows.Run", "Program started for you at sign-in (Run)", run, "", @"HKCU\...\Windows", StartupScope.User));
            }
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
            var (trust, signer) = Verify(path);
            return new(true, Clean(info.CompanyName), Clean(info.FileDescription), Clean(info.ProductName), trust, signer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return new(File.Exists(path), "", "", "", StartupTrust.Unknown); }
    }

    private static string Clean(string? text) => text?.Trim() ?? "";

    public IReadOnlyList<UninstallEntry> InstalledPrograms() => new WindowsInstallSystem().Entries();

    private readonly Dictionary<string, string> packagePublishers = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"\\WindowsApps\\([^\\]+)\\", RegexOptions.IgnoreCase)]
    private static partial Regex PackageFolder();

    public string PackagePublisher(string exePath)
    {
        if (PackageFolder().Match(exePath) is not { Success: true } match) return "";
        var package = match.Groups[1].Value;
        lock (packagePublishers)
        {
            if (!packagePublishers.TryGetValue(package, out var publisher)) packagePublishers[package] = publisher = ReadManifest(package).Publisher;
            return publisher;
        }
    }

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

    // ---- Authenticode: is the file signed, does the signature check out, and who signed it? ----

    internal static (StartupTrust Trust, string Signer) Verify(string path)
    {
        var file = IntPtr.Zero; var data = IntPtr.Zero; var name = IntPtr.Zero;
        var opened = false;
        try
        {
            name = Marshal.StringToHGlobalUni(path);
            var info = new WinTrustFileInfo { Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(), FilePath = name };
            file = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(info, file, false);
            // Keeping the verification state open lets the same check hand back the certificate that signed the file.
            var trust = new WinTrustData { Size = (uint)Marshal.SizeOf<WinTrustData>(), UiChoice = 2, RevocationChecks = 0, UnionChoice = 1, File = file, StateAction = StateVerify, ProviderFlags = 0x1000 | 0x10 };
            data = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(trust, data, false);
            var result = WinVerifyTrust(new IntPtr(-1), WintrustActionGenericVerifyV2, data);
            opened = true;
            return result switch
            {
                0 => (StartupTrust.Verified, SignerOf(data)),
                unchecked((int)0x800B0100) or unchecked((int)0x800B0001) or unchecked((int)0x800B0003) => (StartupTrust.Unsigned, ""),
                _ => (StartupTrust.Invalid, "")
            };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException) { return (StartupTrust.Unknown, ""); }
        finally
        {
            if (opened) CloseState(data);
            if (data != IntPtr.Zero) Marshal.FreeHGlobal(data);
            if (file != IntPtr.Zero) Marshal.FreeHGlobal(file);
            if (name != IntPtr.Zero) Marshal.FreeHGlobal(name);
        }
    }

    private static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const uint StateVerify = 1, StateClose = 2;

    /// <summary>The company on the certificate that signed the file, read from the open verification state. Empty if it cannot be read.</summary>
    private static string SignerOf(IntPtr data)
    {
        try
        {
            var state = Marshal.PtrToStructure<WinTrustData>(data).StateData;
            if (state == IntPtr.Zero) return "";
            var provider = WTHelperProvDataFromStateData(state);
            if (provider == IntPtr.Zero) return "";
            var signer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            if (signer == IntPtr.Zero) return "";
            var link = WTHelperGetProvCertFromChain(signer, 0);
            if (link == IntPtr.Zero) return "";
            // CRYPT_PROVIDER_CERT is a size field followed by the certificate context pointer.
            var context = Marshal.ReadIntPtr(link, IntPtr.Size);
            if (context == IntPtr.Zero) return "";
            using var certificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(context);
            return PublisherNames.FromSubject(certificate.Subject);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or ArgumentException or AccessViolationException or SEHException or EntryPointNotFoundException) { return ""; }
    }

    private static void CloseState(IntPtr data)
    {
        try
        {
            var trust = Marshal.PtrToStructure<WinTrustData>(data);
            trust.StateAction = StateClose;
            Marshal.StructureToPtr(trust, data, false);
            WinVerifyTrust(new IntPtr(-1), WintrustActionGenericVerifyV2, data);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException) { }
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint signer, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterIndex);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint certificate);

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

    public void SetTask(string path, bool enabled) => WithTask(path, task => task.Enabled = enabled);

    // Windows' own tasks are never touched; the rest of \Microsoft (Office, Visual Studio...) belongs to ordinary programs.
    private static void CheckTaskPath(string path)
    {
        if (path.Length < 2 || path[0] != '\\' || path.Length > 240 || path.Contains("..", StringComparison.Ordinal)
            || path.StartsWith(WindowsStartupSystem.WindowsTaskFolder + @"\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("That task cannot be changed.");
    }

    private static void WithTask(string path, Action<dynamic> change)
    {
        CheckTaskPath(path);
        try
        {
            dynamic service = Connect();
            var split = path.LastIndexOf('\\');
            dynamic task = service.GetFolder(split == 0 ? "\\" : path[..split]).GetTask(path[(split + 1)..]);
            change(task);
        }
        catch (COMException ex) when ((uint)ex.HResult == 0x80070005) { throw new UnauthorizedAccessException(ex.Message, ex); }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException ex) when (ex.Message.Contains("0x80070005", StringComparison.Ordinal) || ex.Message.Contains("denied", StringComparison.OrdinalIgnoreCase)) { throw new UnauthorizedAccessException(ex.Message, ex); }
    }

    private static dynamic Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler is not available.");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }

    public string RemoveTask(string path)
    {
        string xml = "";
        CheckTaskPath(path);
        try
        {
            dynamic service = Connect();
            var split = path.LastIndexOf('\\');
            dynamic folder = service.GetFolder(split == 0 ? "\\" : path[..split]);
            var name = path[(split + 1)..];
            dynamic task = folder.GetTask(name);
            xml = (string)task.Xml;
            if (xml.Length == 0) throw new InvalidOperationException("That task could not be saved, so it was left alone.");
            folder.DeleteTask(name, 0);
        }
        catch (COMException ex) when ((uint)ex.HResult == 0x80070005) { throw new UnauthorizedAccessException(ex.Message, ex); }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException ex) when (ex.Message.Contains("0x80070005", StringComparison.Ordinal) || ex.Message.Contains("denied", StringComparison.OrdinalIgnoreCase)) { throw new UnauthorizedAccessException(ex.Message, ex); }
        return xml;
    }

    public void RestoreTask(string path, string xml)
    {
        CheckTaskPath(path);
        if (xml.Length == 0 || xml.Length > 1 << 20) throw new InvalidOperationException("That task cannot be put back.");
        try
        {
            dynamic service = Connect();
            var split = path.LastIndexOf('\\');
            dynamic folder = service.GetFolder(split == 0 ? "\\" : path[..split]);
            // 6 = create or update; the task's own definition names who it runs as, so no account is passed.
            folder.RegisterTask(path[(split + 1)..], xml, 6, null, null, 0, null);
        }
        catch (COMException ex) when ((uint)ex.HResult == 0x80070005) { throw new UnauthorizedAccessException(ex.Message, ex); }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException ex) when (ex.Message.Contains("0x80070005", StringComparison.Ordinal) || ex.Message.Contains("denied", StringComparison.OrdinalIgnoreCase)) { throw new UnauthorizedAccessException(ex.Message, ex); }
    }

    // ---- Taking an item out of startup, with a copy kept by the caller so it can be put back ----

    private const int LargestShortcut = 4 << 20;

    private static RegistryKey Hive(StartupScope scope) => RegistryKey.OpenBaseKey(scope == StartupScope.User ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, RegistryView.Registry64);

    private static string RunKeyPath(string source, string name)
    {
        if (StartupLocations.PathFor(source) is not { } path || string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['\\', '/']) >= 0) throw new InvalidOperationException("That startup item cannot be changed.");
        return path;
    }

    private static void ForgetApproved(RegistryKey root, string subKey, string name)
    {
        using var key = root.OpenSubKey($@"{WindowsStartupSystem.ApprovedPath}\{subKey}", writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    public void RemoveRun(StartupScope scope, string source, string name)
    {
        var path = RunKeyPath(source, name);
        try
        {
            using var root = Hive(scope);
            using var key = root.OpenSubKey(path, writable: true);
            if (key?.GetValue(name) is null) throw new InvalidOperationException("That startup item was not found.");
            key.DeleteValue(name);
            // A leftover on/off record would otherwise decide the fate of a different program that later uses the same name.
            if (StartupLocations.ApprovedKey(source) is { } approved) ForgetApproved(root, approved, name);
        }
        catch (System.Security.SecurityException ex) { throw new UnauthorizedAccessException(ex.Message, ex); }
    }

    public void RestoreRun(StartupScope scope, string source, string name, string command, bool expandable)
    {
        var path = RunKeyPath(source, name);
        if (command.Length == 0 || command.Length > 8192) throw new InvalidOperationException("That startup item cannot be put back.");
        try
        {
            using var root = Hive(scope);
            using var key = root.CreateSubKey(path, writable: true);
            key.SetValue(name, command, expandable ? RegistryValueKind.ExpandString : RegistryValueKind.String);
        }
        catch (System.Security.SecurityException ex) { throw new UnauthorizedAccessException(ex.Message, ex); }
    }

    private static string FolderFile(StartupScope scope, string fileName)
    {
        var folder = Environment.GetFolderPath(scope == StartupScope.User ? Environment.SpecialFolder.Startup : Environment.SpecialFolder.CommonStartup);
        if (string.IsNullOrEmpty(folder) || fileName.Length == 0 || Path.GetFileName(fileName) != fileName || fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("That startup item cannot be changed.");
        return Path.Combine(folder, fileName);
    }

    public byte[] RemoveFolderItem(StartupScope scope, string fileName)
    {
        var file = FolderFile(scope, fileName);
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists) throw new InvalidOperationException("That startup item was not found.");
            if (info.Length > LargestShortcut) throw new InvalidOperationException("That file is too large to keep a copy of, so it was left alone.");
            var contents = File.ReadAllBytes(file);
            File.Delete(file);
            using var root = Hive(scope);
            ForgetApproved(root, "StartupFolder", fileName);
            return contents;
        }
        catch (System.Security.SecurityException ex) { throw new UnauthorizedAccessException(ex.Message, ex); }
    }

    public void RestoreFolderItem(StartupScope scope, string fileName, byte[] contents)
    {
        var file = FolderFile(scope, fileName);
        if (contents.Length == 0 || contents.Length > LargestShortcut) throw new InvalidOperationException("That startup item cannot be put back.");
        if (File.Exists(file)) return;
        File.WriteAllBytes(file, contents);
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
        // Windows itself writes this as a 64-bit number of Unix seconds (a 32-bit value is what an older TidyWindow left behind).
        if (!enabled) key.SetValue("LastDisabledTime", now().ToUnixTimeSeconds(), RegistryValueKind.QWord);
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
