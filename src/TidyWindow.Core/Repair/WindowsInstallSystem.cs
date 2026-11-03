using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TidyWindow.Next.Core;

/// <summary>The real PC: the installed-programs list in the registry, Windows Installer's own database, and the processes that run installs.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsInstallSystem : IInstallSystem
{
    private const string Installer = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer";
    private const string MachineSid = "S-1-5-18";
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
                            parent.Length > 0 || Array.Exists(UpdateTypes, x => string.Equals(x, Text(key, "ReleaseType"), StringComparison.OrdinalIgnoreCase)), Text(key, "BundleProviderKey")));
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
            var product = code.ToString();
            products.Add(new(product, context == MachineContext ? "Machine" : context == UserContext ? "User" : "Managed", sid.ToString(), Info(product, sid.ToString(), context, "ProductName") ?? "", Info(product, sid.ToString(), context, "LocalPackage")));
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

    public bool SavedCopyDamaged(MsiProduct product) =>
        !string.IsNullOrWhiteSpace(product.LocalPackage) && FileExists(product.LocalPackage) && PackageHealth.InstallerDamaged(product.LocalPackage, product.Code);

    /// <summary>Product keys Windows Installer has, that its own list of products does not include: the half of a record that an interrupted repair leaves.</summary>
    public IReadOnlyList<MsiProduct> Ghosts()
    {
        var known = new HashSet<string>(Products().Select(x => InstallRepair.Pack(x.Code)), StringComparer.OrdinalIgnoreCase);
        var me = CurrentSid();
        var found = new Dictionary<string, MsiProduct>(StringComparer.OrdinalIgnoreCase);

        void Add(string packed, string context, string sid, string name)
        {
            if (!PackedCode().IsMatch(packed) || known.Contains(packed) || found.ContainsKey(packed)) return;
            found[packed] = new(InstallRepair.Unpack(packed), context, sid, name, null);
        }

        // Only this account's own records and the machine's are looked at: another user's programs are not listed by Windows Installer without administrator rights,
        // so they would all look half-registered.
        var places = new List<(RegistryKey Hive, string Products, string? Features, string Context, string Sid)>
        {
            (Registry.LocalMachine, @"SOFTWARE\Classes\Installer\Products", @"SOFTWARE\Classes\Installer\Features", "Machine", ""),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Installer\Products", @"SOFTWARE\Microsoft\Installer\Features", "User", me),
            (Registry.CurrentUser, @"SOFTWARE\Classes\Installer\Products", @"SOFTWARE\Classes\Installer\Features", "User", me),
            (Registry.LocalMachine, $@"{Installer}\Managed\{me}\Installer\Products", $@"{Installer}\Managed\{me}\Installer\Features", "Managed", me)
        };
        foreach (var (hive, products, features, context, sid) in places)
        {
            try
            {
                using var list = hive.OpenSubKey(products);
                foreach (var name in list?.GetSubKeyNames() ?? [])
                {
                    using var key = list!.OpenSubKey(name);
                    Add(name, context, sid, key?.GetValue("ProductName") as string ?? "");
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
            // A features key outliving its product's key is what an undo that failed on the key's permissions leaves.
            try
            {
                using var list = features is null ? null : hive.OpenSubKey(features);
                foreach (var name in list?.GetSubKeyNames() ?? []) Add(name, context, sid, "");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }

        // The other half: the product's data survives where its registration is gone, so Windows Installer's own list never shows it.
        foreach (var (sid, context) in new[] { (MachineSid, "Machine"), (me, "User") })
        {
            if (sid.Length == 0) continue;
            try
            {
                using var list = Registry.LocalMachine.OpenSubKey($@"{Installer}\UserData\{sid}\Products");
                foreach (var name in list?.GetSubKeyNames() ?? [])
                {
                    // Only a key that holds the program's install properties was ever a program; some drivers leave a bare features list here that nothing reads.
                    using var key = list!.OpenSubKey($@"{name}\InstallProperties");
                    if (key is not null) Add(name, context, context == "Machine" ? "" : sid, key.GetValue("DisplayName") as string ?? "");
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
        return [.. found.Values];
    }

    [GeneratedRegex("^[0-9A-Fa-f]{32}$")]
    private static partial Regex PackedCode();

    // ---- The component lists, read once per scan ----

    // A machine has tens of thousands of component keys, so each account's list is read once and every check after that is a lookup.
    private sealed class ComponentLists
    {
        /// <summary>Each product's packed code, with the components that name it and the path each one points at.</summary>
        public readonly Dictionary<string, List<(string Component, string Path)>> ByProduct = new(StringComparer.OrdinalIgnoreCase);
    }

    private Dictionary<string, ComponentLists> components = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, bool> orphans = new(StringComparer.OrdinalIgnoreCase);

    public void BeginScan() { components = new(StringComparer.OrdinalIgnoreCase); orphans = new(StringComparer.OrdinalIgnoreCase); }

    private ComponentLists ComponentsOf(string sid)
    {
        if (components.TryGetValue(sid, out var cached)) return cached;
        var lists = new ComponentLists();
        try
        {
            using var list = Registry.LocalMachine.OpenSubKey($@"{Installer}\UserData\{sid}\Components");
            foreach (var component in list?.GetSubKeyNames() ?? [])
            {
                using var key = list!.OpenSubKey(component);
                foreach (var name in key?.GetValueNames() ?? [])
                {
                    if (name.Length != 32) continue;
                    if (!lists.ByProduct.TryGetValue(name, out var entries)) lists.ByProduct[name] = entries = [];
                    entries.Add((component, key!.GetValue(name) as string ?? ""));
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        components[sid] = lists;
        return lists;
    }

    public bool ProductFilesPresent(MsiProduct product)
    {
        // Windows Installer's own answer first: it is one call, against a registry walk.
        if (Info(product.Code, product.Sid, ContextOf(product.Context), "InstallLocation") is { Length: > 0 } location && Directory.Exists(location)) return true;
        var packed = InstallRepair.Pack(product.Code);
        var sid = string.Equals(product.Context, "Machine", StringComparison.OrdinalIgnoreCase) || product.Sid.Length == 0 ? MachineSid : product.Sid;
        foreach (var (_, path) in ComponentsOf(sid).ByProduct.GetValueOrDefault(packed) ?? [])
            // A component's key path is a file or folder, or a registry value ("01:\Software\Vendor\App\Value"). A program whose components all have registry key paths is still there when they are.
            if (path.Length > 3 && path[1] == ':' && path[2] == '\\' && (File.Exists(path) || Directory.Exists(path))) return true;
            else if (RegistryValuePresent(path)) return true;
        return false;
    }

    /// <summary>
    /// Whether the registry value a component names as its key path is there. The two characters before ":\" are hex: the low bits pick the hive
    /// (0 classes root, 1 current user, 2 local machine, 3 users) and 0x20 means the 64-bit view.
    /// </summary>
    internal static bool RegistryValuePresent(string path)
    {
        if (path.Length < 5 || path[2] != ':' || path[3] != '\\' || !int.TryParse(path[..2], System.Globalization.NumberStyles.AllowHexSpecifier, null, out var code)) return false;
        var hive = (code & 0x0F) switch { 0 => RegistryHive.ClassesRoot, 1 => RegistryHive.CurrentUser, 2 => RegistryHive.LocalMachine, 3 => RegistryHive.Users, _ => (RegistryHive?)null };
        if (hive is null) return false;
        var rest = path[4..];
        var cut = rest.LastIndexOf('\\');
        var key = cut < 0 ? rest : rest[..cut];
        var value = cut < 0 ? "" : rest[(cut + 1)..];
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive.Value, (code & 0x20) != 0 ? RegistryView.Registry64 : RegistryView.Registry32);
            using var opened = root.OpenSubKey(key);
            return opened is not null && (value.Length == 0 || opened.GetValue(value) is not null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException) { return false; }
    }

    private static int ContextOf(string context) => string.Equals(context, "Machine", StringComparison.OrdinalIgnoreCase) ? MachineContext : string.Equals(context, "User", StringComparison.OrdinalIgnoreCase) ? UserContext : ManagedContext;

    public bool BundleHasChildren(string bundleId)
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var list = hive.OpenSubKey(@"SOFTWARE\Classes\Installer\Dependencies");
                foreach (var name in list?.GetSubKeyNames() ?? [])
                {
                    if (name.Length != 38 || name[0] != '{') continue;
                    using var dependent = list!.OpenSubKey($@"{name}\Dependents\{bundleId}");
                    if (dependent is not null && ProductState(name) != InstallRepair.Unknown) return true;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
        return false;
    }

    private static string CurrentSid() => System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "";

    /// <summary>Upgrade-code links to a product that Windows Installer does not know and that has no record in any place this account can see.</summary>
    public IReadOnlyList<ValueRef> DanglingLinks()
    {
        var links = new List<ValueRef>();
        var me = CurrentSid();
        // Every place an upgrade code lists the products that carry it, for the machine, this account, and the programs an administrator installed for it.
        var roots = new List<(RegistryKey Hive, string Name, string Path)>
        {
            (Registry.LocalMachine, "HKLM", @"SOFTWARE\Classes\Installer\UpgradeCodes"),
            (Registry.LocalMachine, "HKLM", $@"{Installer}\UpgradeCodes"),
            (Registry.LocalMachine, "HKLM", $@"{Installer}\Managed\{me}\Installer\UpgradeCodes"),
            (Registry.CurrentUser, "HKCU", @"SOFTWARE\Microsoft\Installer\UpgradeCodes"),
            (Registry.CurrentUser, "HKCU", @"SOFTWARE\Classes\Installer\UpgradeCodes"),
            (Registry.CurrentUser, "HKCU", $@"{Installer}\UpgradeCodes")
        };
        foreach (var (hive, name, path) in roots)
        {
            try
            {
                using var list = hive.OpenSubKey(path);
                foreach (var upgrade in list?.GetSubKeyNames() ?? [])
                {
                    using var key = list!.OpenSubKey(upgrade);
                    foreach (var product in key?.GetValueNames() ?? [])
                        if (product.Length == 32 && IsOrphan(product)) links.Add(new($@"{name}\{path}\{upgrade}", product));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }

        // A product ripped out of the registry without its components leaves its code in them, which keeps their files and keys from ever being cleaned up.
        foreach (var sid in new[] { MachineSid, me }.Where(x => x.Length > 0))
            foreach (var (product, entries) in ComponentsOf(sid).ByProduct)
            {
                if (product.All(x => x == '0') || !IsOrphan(product)) continue;
                foreach (var (component, _) in entries) links.Add(new($@"HKLM\{Installer}\UserData\{sid}\Components\{component}", product));
            }
        return links;
    }

    /// <summary>A product code that Windows Installer does not know and that has no record in any place this account can see.</summary>
    private bool IsOrphan(string packed)
    {
        if (orphans.TryGetValue(packed, out var known)) return known;
        var orphan = ProductState(InstallRepair.Unpack(packed)) == InstallRepair.Unknown && !HasProductKey(packed);
        orphans[packed] = orphan;
        return orphan;
    }

    private static bool HasProductKey(string packed)
    {
        foreach (var (hive, path) in new[]
        {
            (Registry.LocalMachine, @"SOFTWARE\Classes\Installer\Products"), (Registry.CurrentUser, @"SOFTWARE\Microsoft\Installer\Products"), (Registry.CurrentUser, @"SOFTWARE\Classes\Installer\Products"),
            (Registry.LocalMachine, $@"{Installer}\UserData\{MachineSid}\Products"), (Registry.LocalMachine, $@"{Installer}\UserData\{CurrentSid()}\Products")
        })
        {
            try { using var key = hive.OpenSubKey($@"{path}\{packed}"); if (key is not null) return true; }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { return true; }
        }
        try
        {
            using var managed = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\Managed");
            foreach (var sid in managed?.GetSubKeyNames() ?? [])
            {
                using var key = managed!.OpenSubKey($@"{sid}\Installer\Products\{packed}");
                if (key is not null) return true;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { return true; }
        return false;
    }

    /// <summary>Updates whose saved .msp file is gone while a program still wears them (error 1612 and 1714 when it is repaired, updated or removed).</summary>
    public IReadOnlyList<DeadPatch> DeadPatches()
    {
        var found = new List<DeadPatch>();
        var me = CurrentSid();
        foreach (var sid in new[] { MachineSid, me }.Where(x => x.Length > 0))
        {
            var data = $@"{Installer}\UserData\{sid}";
            try
            {
                using var patches = Registry.LocalMachine.OpenSubKey($@"{data}\Patches");
                foreach (var patch in patches?.GetSubKeyNames() ?? [])
                {
                    if (!PackedCode().IsMatch(patch)) continue;
                    using var record = patches!.OpenSubKey(patch);
                    if (record?.GetValue("LocalPackage") is not string { Length: > 0 } file) continue;
                    // A patch file that is there but empty, cut short or another update's is as useless to Windows Installer as one that is gone.
                    var damaged = FileExists(file);
                    if (damaged && !PackageHealth.PatchDamaged(file, InstallRepair.Unpack(patch))) continue;

                    var keys = new List<string>(); var programs = new List<string>();
                    using (var products = Registry.LocalMachine.OpenSubKey($@"{data}\Products"))
                        foreach (var product in products?.GetSubKeyNames() ?? [])
                        {
                            using var worn = products!.OpenSubKey($@"{product}\Patches\{patch}");
                            if (worn is null) continue;
                            keys.Add($@"HKLM\{data}\Products\{product}\Patches\{patch}");
                            using var properties = products.OpenSubKey($@"{product}\InstallProperties");
                            programs.Add(properties?.GetValue("DisplayName") as string is { Length: > 0 } display ? display : InstallRepair.Unpack(product));
                        }
                    // A patch nothing wears is not what stops a program, so it is left alone.
                    if (keys.Count == 0) continue;
                    keys.Add($@"HKLM\{data}\Patches\{patch}");
                    var shared = sid == MachineSid
                        ? new[] { $@"HKLM\SOFTWARE\Classes\Installer\Patches\{patch}" }
                        : [$@"HKCU\SOFTWARE\Microsoft\Installer\Patches\{patch}", $@"HKCU\SOFTWARE\Classes\Installer\Patches\{patch}", $@"HKLM\{Installer}\Managed\{sid}\Installer\Patches\{patch}"];
                    keys.AddRange(shared.Where(KeyExists));
                    found.Add(new(patch, file, programs, keys, damaged));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
        return found;
    }

    private static bool KeyExists(string path)
    {
        try
        {
            using var key = (path.StartsWith("HKCU\\", StringComparison.Ordinal) ? Registry.CurrentUser : Registry.LocalMachine).OpenSubKey(path[(path.IndexOf('\\') + 1)..]);
            return key is not null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    public bool InstallRollbackExists() => KeyExists(InstallRepair.InstallRollbackKey);

    /// <summary>Starting or stopping, twice in a row a moment apart, with nothing installing.</summary>
    public bool InstallerServiceStuck()
    {
        static bool Pending(string? state) => state is "START_PENDING" or "STOP_PENDING" or "CONTINUE_PENDING" or "PAUSE_PENDING";
        if (!Pending(WindowsInstallerOps.ServiceState())) return false;
        Thread.Sleep(1500);
        return Pending(WindowsInstallerOps.ServiceState());
    }

    public IReadOnlyList<string> FilesIn(string folder)
    {
        try { return Directory.Exists(folder) ? Directory.EnumerateFiles(folder).ToList() : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    // ---- Installer files ----

    private static IEnumerable<string> Matching(string folder, string pattern)
    {
        try { return Directory.Exists(folder) ? Directory.EnumerateDirectories(folder, pattern).ToList() : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    private static string Windows => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public IReadOnlyList<string> InstallerLeftovers(string code, bool packageCache)
    {
        var found = new List<string>();
        found.AddRange(Matching(Path.Combine(Windows, "Installer"), code));
        found.AddRange(Matching(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Installer"), code));
        if (packageCache)
        {
            found.AddRange(Matching(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Package Cache"), code + "*"));
            found.AddRange(Matching(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Package Cache"), code + "*"));
        }
        // Only folders of the shapes a repair is allowed to set aside, and never a link to somewhere else.
        return found.Where(x => RepairFolders.Classify(x) == FolderKind.InstallerCache && (new DirectoryInfo(x).Attributes & FileAttributes.ReparsePoint) == 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<string> OrphanInstallerFolders()
    {
        // While an install runs, the folder it is filling is new and the product is not registered yet.
        if (InstallerBusy()) return [];
        var found = new List<string>();
        foreach (var root in new[] { Path.Combine(Windows, "Installer"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Installer") })
            foreach (var folder in Matching(root, "{*}"))
            {
                var name = Path.GetFileName(folder);
                if (RepairFolders.Classify(folder) != FolderKind.InstallerCache || !Guid.TryParse(name, out _)) continue;
                try
                {
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(folder) < TimeSpan.FromDays(2) || (new DirectoryInfo(folder).Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (ProductState(name.ToUpperInvariant()) != InstallRepair.Unknown || HasProductKey(InstallRepair.Pack(name))) continue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { continue; }
                found.Add(folder);
            }
        return found;
    }

    public string? RollbackFolder()
    {
        var path = Path.Combine((Environment.GetEnvironmentVariable("SystemDrive") ?? "C:") + @"\", "Config.Msi");
        return Directory.Exists(path) && RepairFolders.Classify(path) == FolderKind.RollbackFolder ? path : null;
    }

    // ---- Shortcuts ----

    private static readonly EnumerationOptions Everything = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 8, AttributesToSkip = 0 };

    public IReadOnlyList<ShortcutRoot> ShortcutRoots() => ShortcutPlaces.Roots();

    public IReadOnlyList<ShortcutFile> Shortcuts()
    {
        var paths = new List<string>();
        foreach (var root in ShortcutPlaces.Roots())
        {
            try
            {
                // Only the Start menu has program folders worth walking into; the others hold the shortcuts themselves.
                var options = root.Area == ShortcutArea.StartMenu ? Everything : new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
                paths.AddRange(Directory.EnumerateFiles(root.Path, "*", options).Where(x => x.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".url", StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return ShortcutReader.ReadAll(paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    public IReadOnlyList<ShortcutFolder> ShortcutFolders()
    {
        var found = new List<ShortcutFolder>();
        void Walk(string folder, int depth)
        {
            try
            {
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) return;
                var children = Directory.EnumerateDirectories(folder).ToList();
                found.Add(new(folder, Directory.EnumerateFiles(folder).ToList(), children));
                if (depth < 8) foreach (var child in children) Walk(child, depth + 1);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        foreach (var root in ShortcutPlaces.Roots().Where(x => x.Area == ShortcutArea.StartMenu)) Walk(root.Path, 0);
        return found;
    }

    public void ShellChanged(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            var pointer = Marshal.StringToHGlobalUni(path);
            try
            {
                var link = path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".url", StringComparison.OrdinalIgnoreCase);
                var here = File.Exists(path) || Directory.Exists(path);
                // SHCNE_DELETE / RMDIR for what is gone, UPDATEITEM / UPDATEDIR for what an undo put back.
                SHChangeNotify(here ? (link ? 0x2000 : 0x1000) : (link ? 0x4 : 0x10), 0x5, pointer, IntPtr.Zero);
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }
        // The Start menu and search read their lists again when the shell says associations changed.
        if (paths.Count > 0) SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int events, uint flags, IntPtr item1, IntPtr item2);

    public bool RemoveEmptyFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return true;
            if (Directory.EnumerateFileSystemEntries(path).Any()) return false;
            Directory.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
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

    private const int AllContexts = 7, MachineContext = 4, UserContext = 2, ManagedContext = 1, NoMoreItems = 259, MoreData = 234;

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern int MsiQueryProductState(string product);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiEnumProductsEx(string? product, string? userSid, int contexts, uint index, StringBuilder productCode, out int context, StringBuilder sid, ref uint sidLength);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiGetProductInfoEx(string product, string? userSid, int context, string property, StringBuilder value, ref uint length);
}
