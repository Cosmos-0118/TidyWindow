using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace TidyWindow.Next.Core;

/// <summary>What an installer file says about itself.</summary>
public sealed record MsiPackageInfo(string ProductCode, string PackageCode, string Version, string Name);

public sealed record RunResult(int ExitCode, string Output);

/// <summary>The jobs that need the elevated helper for a Windows-wide program, apart from changing the registry.</summary>
public interface IElevatedOps
{
    /// <summary>Repairs one product from its own installer file with <c>msiexec /fvomus</c> (<c>/fomus</c> when the file is the program's own saved copy), which puts a lost saved copy back. Refuses a file that is not that product's.</summary>
    RunResult Reinstall(string msiPath, MsiProduct product);
    /// <summary>Stops the Windows Installer service, ending it by force if it does not stop. Nothing else is ever stopped.</summary>
    bool StopInstallerService();
    /// <summary>
    /// Puts a bundle's cached setup program back where its list entry says it is, from a downloaded copy that is signed, names the program and is the very file the entry expects.
    /// A Windows-wide bundle's cache (C:\ProgramData\Package Cache) only administrators can write to, so this runs in the helper for those.
    /// The bundle's own <c>/repair</c> is not used: with its saved choices gone it installs the default parts (Python's documentation and test suite) as well.
    /// </summary>
    RunResult RestoreBundleCopy(string exePath, string programName, string targetPath);
    /// <summary>Takes back a setup program <see cref="RestoreBundleCopy"/> put in the package cache (or an update file <see cref="RestorePatchCopy"/> put in C:\Windows\Installer), and the setup program's folder when that is empty. True once the file is gone.</summary>
    bool RemoveCopy(string path);
    /// <summary>
    /// Puts an update's saved file back where Windows Installer registered it (a <c>.msp</c> directly in C:\Windows\Installer), from a copy that carries the very patch code it registered.
    /// With <paramref name="requireSignature"/> the copy also has to have a valid digital signature; a file the person chose themselves does not. Never replaces a usable file.
    /// </summary>
    RunResult RestorePatchCopy(string mspPath, string patchCode, string targetPath, bool requireSignature);
}

/// <summary>Finding and checking the installer file a broken program needs. Reading only; nothing here changes the PC.</summary>
public interface IInstallerOps : IElevatedOps
{
    /// <summary>Installer files that could be this program's, from its own source list and the places Windows, installers and TidyWindow itself keep copies.</summary>
    IReadOnlyList<string> CandidatePackages(MsiProduct product);
    MsiPackageInfo? ReadPackage(string path);
    /// <summary>The package code Windows Installer registered for the product, as a GUID, or null when it has none.</summary>
    string? RegisteredPackageCode(MsiProduct product);
    /// <summary>The installer files (.msi and .exe) in a folder the person pointed at, and in the folders directly under it.</summary>
    IReadOnlyList<string> FilesIn(string folder);
    /// <summary>Unpacks every package out of a program's own setup program (a WiX Burn bundle, with <c>/layout</c>) and returns the .msi files. Only a setup program with a valid signature that names the program is run.</summary>
    IReadOnlyList<string> ExpandBundle(string exePath, string folder, string programName);
    /// <summary>Unpacks a .zip (WinGet sometimes downloads an installer inside one) into <paramref name="folder"/> and returns the .msi and .msp files in it. Nothing in it is run.</summary>
    IReadOnlyList<string> ExpandArchive(string zipPath, string folder);
    /// <summary>The update code ({GUID}) an .msp file carries, or null when it is not a readable update.</summary>
    string? ReadPatchCode(string path);
    /// <summary>.msp files that could be this update's, from the places bundles, Windows, WinGet and TidyWindow keep copies. Every one is checked against the patch code before it is used.</summary>
    IReadOnlyList<string> CandidatePatches(string patchCode);
    /// <summary>The .msp files in a folder the person pointed at, and in the folders directly under it.</summary>
    IReadOnlyList<string> PatchesIn(string folder);
}

[SupportedOSPlatform("windows")]
public sealed partial class WindowsInstallerOps : IInstallerOps
{

    private const string InstallerKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer";

    /// <summary>Where Windows Installer keeps a product's source list, by context: the signed-in user's, the machine's, or an administrator's for this user (Managed).</summary>
    private static (RegistryKey Hive, string Path) ProductKey(MsiProduct product, string packed) => product.Context switch
    {
        "User" => (Registry.CurrentUser, $@"SOFTWARE\Microsoft\Installer\Products\{packed}"),
        "Managed" => (Registry.LocalMachine, $@"{InstallerKey}\Managed\{product.Sid}\Installer\Products\{packed}"),
        _ => (Registry.LocalMachine, $@"SOFTWARE\Classes\Installer\Products\{packed}")
    };

    public IReadOnlyList<string> CandidatePackages(MsiProduct product)
    {
        var found = new List<string>();
        var packed = InstallRepair.Pack(product.Code);
        var folders = new List<string>();
        var names = new List<string>();
        var (list, path) = ProductKey(product, packed);
        try
        {
            using var source = list.OpenSubKey(path + @"\SourceList");
            if (source is not null)
            {
                if (source.GetValue("PackageName") is string name && name.Length > 0) names.Add(name);
                // LastUsedSource reads like "n;1;C:\folder\"; the folder is what comes after the second semicolon.
                if (source.GetValue("LastUsedSource") is string last && last.Split(';', 3) is { Length: 3 } parts) folders.Add(parts[2]);
                foreach (var kind in new[] { "Net", "URL", "Media" })
                {
                    using var sub = source.OpenSubKey(kind);
                    foreach (var value in sub?.GetValueNames() ?? []) if (sub!.GetValue(value) is string folder && kind != "Media") folders.Add(folder);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }

        // The folder the program was installed from outlives its source list.
        try
        {
            var sid = product.Context == "Machine" || product.Sid.Length == 0 ? "S-1-5-18" : product.Sid;
            using var properties = Registry.LocalMachine.OpenSubKey($@"{InstallerKey}\UserData\{sid}\Products\{packed}\InstallProperties");
            if (properties?.GetValue("InstallSource") is string { Length: > 0 } installSource) folders.Add(installSource);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }

        foreach (var root in BurnCacheRoots())
            folders.AddRange(Safe(() => Directory.EnumerateDirectories(root, product.Code + "v*")));
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        // Patched products keep their original installer under the patch cache.
        var patchCache = Path.Combine(windows, "Installer", "$PatchCache$", "Managed");
        foreach (var owner in Safe(() => Directory.EnumerateDirectories(patchCache)))
            folders.AddRange(Safe(() => Directory.EnumerateDirectories(Path.Combine(owner, packed))));

        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var name in names) if (SafeExists(Path.Combine(folder, name))) found.Add(Path.Combine(folder, name));
            found.AddRange(Safe(() => Directory.EnumerateFiles(folder, "*.msi")));
        }

        // Copies already on this PC that the program's own records know nothing about: what TidyWindow's Cleanup set aside (kept for 14 days), and what WinGet and Scoop downloaded.
        // Only installer packages are taken from these; every one is checked against the program before it is used.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var root in new[] { Path.Combine(local, "TidyWindow.Next", "Undo"), Path.Combine(Path.GetTempPath(), "WinGet"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "cache") })
            found.AddRange(Packages(root));
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> Packages(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return [];
            return Directory.EnumerateFiles(root, "*.msi", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true }).Take(300).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    private const string BurnPolicyKey = @"SOFTWARE\Policies\WiX\Burn";

    /// <summary>The folders Burn (WiX bundles, Visual Studio, the .NET SDK) caches its packages in: the two standard ones, and the one a policy moved the cache to.</summary>
    internal static IReadOnlyList<string> BurnCacheRoots() => BurnCacheRoots(BurnPolicyRoot());

    internal static IReadOnlyList<string> BurnCacheRoots(string? policy)
    {
        var roots = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Package Cache"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Package Cache")
        };
        // Burn 3.9 and later put its cache folders straight under the policy's folder, and some builds under a "Package Cache" folder inside it.
        if (!string.IsNullOrWhiteSpace(policy))
        {
            var root = Environment.ExpandEnvironmentVariables(policy.Trim()).TrimEnd('\\');
            if (Path.IsPathFullyQualified(root))
            {
                roots.Add(root);
                roots.Add(Path.Combine(root, "Package Cache"));
            }
        }
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? BurnPolicyRoot()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(BurnPolicyKey);
            return key?.GetValue("PackageCache") as string;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return null; }
    }

    private static IEnumerable<string> Safe(Func<IEnumerable<string>> read)
    {
        try { return read().ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    private static bool SafeExists(string path) { try { return File.Exists(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; } }

    public string? RegisteredPackageCode(MsiProduct product)
    {
        var (hive, path) = ProductKey(product, InstallRepair.Pack(product.Code));
        try
        {
            using var key = hive.OpenSubKey(path);
            return key?.GetValue("PackageCode") is string { Length: 32 } code ? InstallRepair.Unpack(code) : null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException) { return null; }
    }

    /// <summary>The file name Windows Installer registered in the product's SourceList (such as <c>EpicInstaller-20.3.3.msi</c>), which SecureRepair checks when repairing from source.</summary>
    public string? RegisteredPackageName(MsiProduct product)
    {
        var (hive, path) = ProductKey(product, InstallRepair.Pack(product.Code));
        try
        {
            using var source = hive.OpenSubKey(path + @"\SourceList");
            if (source?.GetValue("PackageName") is string name && IsSafeMsiFileName(name)) return name;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException) { }
        return null;
    }

    private static bool IsSafeMsiFileName(string name) =>
        name.Length > 0
        && name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
        && string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal)
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    /// <summary>
    /// Whether an MSI database carries all of its cabinet streams inside itself (<c>#cab</c> in the <c>Media</c> table, present in <c>_Streams</c>).
    /// Windows Installer 5.0+ keeps embedded cabinets inside the cached <c>LocalPackage</c>, so staging <c>LocalPackage</c> under its registered <c>PackageName</c>
    /// satisfies source resolution without needing the deleted original download folder or a network fetch.
    /// </summary>
    internal static bool HasEmbeddedCabinets(string path)
    {
        if (MsiOpenDatabase(path, IntPtr.Zero, out var database) != 0) return false;
        try
        {
            var cabinets = ColumnValues(database, "SELECT `Cabinet` FROM `Media`").Where(x => x.Length > 0).ToList();
            if (cabinets.Count == 0 || cabinets.Any(x => x[0] != '#')) return false;
            var streams = ColumnValues(database, "SELECT `Name` FROM `_Streams`").ToHashSet(StringComparer.OrdinalIgnoreCase);
            return cabinets.All(x => streams.Contains(x[1..]));
        }
        finally { MsiCloseHandle(database); }
    }

    /// <summary>
    /// Whether repairing from source should use <c>e</c> (equal or older) rather than <c>o</c> (older only): products with .NET GAC assemblies (<c>MsiAssembly</c>)
    /// or an authored <c>REINSTALLMODE</c> containing <c>e</c>/<c>a</c> (such as IIS 10.0 Express) skip <c>AssemblyCopy</c> under <c>o</c> while still unpublishing the assembly.
    /// </summary>
    internal static bool NeedsEqualReinstall(string path)
    {
        if (MsiOpenDatabase(path, IntPtr.Zero, out var database) != 0) return false;
        try
        {
            if (Property(database, "REINSTALLMODE") is { } mode && (mode.Contains('a', StringComparison.OrdinalIgnoreCase) || mode.Contains('e', StringComparison.OrdinalIgnoreCase)))
                return true;
            return ColumnValues(database, "SELECT `File_` FROM `MsiAssembly`").Count > 0;
        }
        finally { MsiCloseHandle(database); }
    }

    private static List<string> ColumnValues(IntPtr database, string query)
    {
        var values = new List<string>();
        if (MsiDatabaseOpenView(database, query, out var view) != 0) return values;
        try
        {
            if (MsiViewExecute(view, IntPtr.Zero) != 0) return values;
            while (MsiViewFetch(view, out var record) == 0)
            {
                try
                {
                    var value = new StringBuilder(256); uint length = 256;
                    if (MsiRecordGetString(record, 1, value, ref length) == 0) values.Add(value.ToString());
                }
                finally { MsiCloseHandle(record); }
            }
        }
        finally { MsiCloseHandle(view); }
        return values;
    }

    public MsiPackageInfo? ReadPackage(string path)
    {
        if (MsiOpenDatabase(path, IntPtr.Zero, out var database) != 0) return null;
        try
        {
            var product = Property(database, "ProductCode");
            if (product is null) return null;
            var package = "";
            if (MsiGetSummaryInformation(database, null, 0, out var summary) == 0)
            {
                try
                {
                    var text = new StringBuilder(64); uint length = 64; uint type = 0; int number = 0; long time = 0;
                    if (MsiSummaryInfoGetProperty(summary, 9, ref type, ref number, ref time, text, ref length) == 0) package = text.ToString();
                }
                finally { MsiCloseHandle(summary); }
            }
            return new(product, package, Property(database, "ProductVersion") ?? "", Property(database, "ProductName") ?? "");
        }
        finally { MsiCloseHandle(database); }
    }

    private static string? Property(IntPtr database, string name)
    {
        if (MsiDatabaseOpenView(database, $"SELECT `Value` FROM `Property` WHERE `Property`='{name}'", out var view) != 0) return null;
        try
        {
            if (MsiViewExecute(view, IntPtr.Zero) != 0 || MsiViewFetch(view, out var record) != 0) return null;
            try
            {
                var value = new StringBuilder(256); uint length = 256;
                return MsiRecordGetString(record, 1, value, ref length) == 0 ? value.ToString() : null;
            }
            finally { MsiCloseHandle(record); }
        }
        finally { MsiCloseHandle(view); }
    }

    public string? ReadPatchCode(string path) => PackageHealth.ReadPatchCode(path);

    public IReadOnlyList<string> CandidatePatches(string patchCode)
    {
        var found = new List<string>();
        // A bundle keeps the updates it applied next to its other packages; Windows keeps every update's own saved file under a name of its own choosing.
        foreach (var root in BurnCacheRoots()) found.AddRange(Updates(root, 4));
        found.AddRange(Safe(() => Directory.EnumerateFiles(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Installer"), "*.msp")));
        foreach (var root in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWindow.Next", "Undo"), Path.Combine(Path.GetTempPath(), "WinGet"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "cache") })
            found.AddRange(Updates(root, 4));
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> Updates(string root, int depth)
    {
        try
        {
            if (!Directory.Exists(root)) return [];
            return Directory.EnumerateFiles(root, "*.msp", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = depth, IgnoreInaccessible = true }).Take(300).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    public IReadOnlyList<string> PatchesIn(string folder)
    {
        var found = new List<string>();
        foreach (var directory in new[] { folder }.Concat(Safe(() => Directory.EnumerateDirectories(folder))))
            found.AddRange(Safe(() => Directory.EnumerateFiles(directory, "*.msp")));
        return found.Take(400).ToList();
    }

    public IReadOnlyList<string> ExpandArchive(string zipPath, string folder)
    {
        try
        {
            using (var archive = System.IO.Compression.ZipFile.OpenRead(zipPath))
            {
                // A download is a few installers; an archive of thousands of files or many gigabytes is not one, and is not unpacked.
                if (archive.Entries.Count > 2000 || archive.Entries.Sum(x => x.Length) > 4L * 1024 * 1024 * 1024) return [];
            }
            Directory.CreateDirectory(folder);
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, folder);
            return Safe(() => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(x => Path.GetExtension(x).ToLowerInvariant() is ".msi" or ".msp")).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException) { return []; }
    }

    public RunResult RestorePatchCopy(string mspPath, string patchCode, string targetPath, bool requireSignature)
    {
        if (InstallerCachePatch(targetPath) is not { } target) return new(87, "That is not a place Windows keeps an update's saved file.");
        if (!string.Equals(Path.GetExtension(mspPath), ".msp", StringComparison.OrdinalIgnoreCase) || !File.Exists(mspPath)) return new(87, "That is not an update file.");
        if (!string.Equals(PackageHealth.ReadPatchCode(mspPath), patchCode, StringComparison.OrdinalIgnoreCase)) return new(87, "That update file is not the update Windows Installer registered.");
        // A usable file is never replaced: it is the one Windows Installer already trusts.
        if (File.Exists(target) && !PackageHealth.PatchDamaged(target, patchCode)) return new(183, "A usable file is already there.");
        var staged = target + ".tidywindow-new";
        try
        {
            File.Copy(mspPath, staged, true);
            // The copy sits in a folder only administrators can write to now, so nobody can swap it between these checks and the move below.
            if (!string.Equals(PackageHealth.ReadPatchCode(staged), patchCode, StringComparison.OrdinalIgnoreCase)) { DeleteStaged(staged); return new(87, "That update file is not the update Windows Installer registered."); }
            if (requireSignature && WindowsStartupSystem.Verify(staged).Trust != StartupTrust.Verified) { DeleteStaged(staged); return new(87, "That update file is not digitally signed, so it was not trusted."); }
            // Windows keeps its saved update files read-only, so a damaged one has to be made writable before it can be replaced, and the new one gets the same mark.
            if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
            File.Move(staged, target, true);
            File.SetAttributes(target, FileAttributes.ReadOnly | FileAttributes.Archive);
            return new(0, "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteStaged(staged);
            return new(5, ex.Message);
        }
    }

    /// <summary>Removes a half-placed copy. A copy of a read-only file is read-only too, and could not be deleted otherwise.</summary>
    private static void DeleteStaged(string staged)
    {
        try
        {
            if (!File.Exists(staged)) return;
            File.SetAttributes(staged, FileAttributes.Normal);
            File.Delete(staged);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The full path of an update file directly inside C:\Windows\Installer, or null for anything else. Resolving the path first keeps "..\" from walking out of it.</summary>
    internal static string? InstallerCachePatch(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Installer");
            return full.EndsWith(".msp", StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetDirectoryName(full), cache, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    public IReadOnlyList<string> FilesIn(string folder)
    {
        var found = new List<string>();
        foreach (var directory in new[] { folder }.Concat(Safe(() => Directory.EnumerateDirectories(folder))))
            found.AddRange(Safe(() => Directory.EnumerateFiles(directory)).Where(x => Path.GetExtension(x).ToLowerInvariant() is ".msi" or ".exe"));
        return found.Take(400).ToList();
    }

    public IReadOnlyList<string> ExpandBundle(string exePath, string folder, string programName)
    {
        if (!IsTrusted(exePath, programName)) return [];
        Directory.CreateDirectory(folder);
        var result = RunSetup(exePath, TimeSpan.FromMinutes(10), false, "/layout", folder, "/quiet");
        return result.ExitCode == 0 ? Safe(() => Directory.EnumerateFiles(folder, "*.msi", SearchOption.AllDirectories)).ToList() : [];
    }

    public RunResult RestoreBundleCopy(string exePath, string programName, string targetPath)
    {
        if (PackageCachePath(targetPath) is not { } target) return new(87, "That is not a place Windows keeps a setup program.");
        if (!IsTrusted(exePath, programName)) return new(87, "That setup program is not signed, or does not belong to this program.");
        string original;
        try { original = FileVersionInfo.GetVersionInfo(exePath).OriginalFilename ?? ""; }
        catch (IOException) { return new(2, "The downloaded setup program could not be read."); }
        // Bundles are often built with a generic name inside (Setup.exe) and downloaded under the name the entry expects, so either one is enough.
        var wanted = Path.GetFileName(target);
        if (!string.Equals(original, wanted, StringComparison.OrdinalIgnoreCase) && !string.Equals(Path.GetFileName(exePath), wanted, StringComparison.OrdinalIgnoreCase))
            return new(87, $"That setup program is {(original.Length > 0 ? original : Path.GetFileName(exePath))}, not {wanted}.");
        var staged = target + ".tidywindow-new";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(exePath, staged, true);
            // The copy now sits in a folder only administrators can write to (for a Windows-wide bundle), so nobody can swap it between this check and the move below.
            if (!IsTrusted(staged, programName)) { File.Delete(staged); return new(87, "That setup program is not signed, or does not belong to this program."); }
            File.Move(staged, target, true);
            return new(0, "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(staged); } catch (Exception inner) when (inner is IOException or UnauthorizedAccessException) { }
            return new(5, ex.Message);
        }
    }

    public bool RemoveCopy(string path)
    {
        if ((PackageCachePath(path) ?? InstallerCachePatch(path)) is not { } target) return false;
        try
        {
            // An update file Windows or TidyWindow saved is read-only.
            if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
            File.Delete(target);
            var folder = Path.GetDirectoryName(target);
            if (folder is not null && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            return !File.Exists(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The full path of a setup program inside a "Package Cache" folder, or null for anything else. Resolving the path first keeps "..\" from walking out of the cache.</summary>
    private static string? PackageCachePath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && full.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>A setup program is only run when Windows says its signature is valid and its own details name the program it is meant to repair.</summary>
    internal static bool IsTrusted(string exePath, string programName)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            var text = string.Join(' ', info.ProductName, info.FileDescription, info.InternalName, info.OriginalFilename, info.CompanyName, Path.GetFileName(exePath));
            if (!Names(text, programName)) return false;
            // The same in-process check Startup uses: Windows' own WinVerifyTrust, with no PowerShell started per file.
            return WindowsStartupSystem.Verify(exePath).Trust == StartupTrust.Verified;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException) { return false; }
    }

    private static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft", "windows", "corporation", "inc", "ltd", "llc", "the", "for", "and", "of", "app", "setup", "installer", "bit", "x64", "x86", "amd64", "arm64"
    };

    /// <summary>
    /// Whether a file's own details (product, description, company, file name) name the program. The words that tell programs apart have to be there:
    /// the program's first one, and its second as well when the name opens with a word every program from that maker or platform shares ("Microsoft", "Windows").
    /// A short word (R, HP, LG, EA) has to stand alone as a word, so it is not found inside any other.
    /// </summary>
    internal static bool Names(string details, string programName)
    {
        var words = Words().Matches(programName).Select(x => x.Value).ToList();
        var distinct = words.Where(x => !x.All(char.IsDigit) && !Filler.Contains(x)).ToList();
        if (distinct.Count == 0) return false;
        var whole = Words().Matches(details).Select(x => x.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var needed = words.Count > 0 && Filler.Contains(words[0]) ? Math.Min(2, distinct.Count) : 1;
        return distinct.Take(needed).All(word => word.Length < 3 ? whole.Contains(word) : details.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    [System.Text.RegularExpressions.GeneratedRegex("[A-Za-z0-9]+")]
    private static partial System.Text.RegularExpressions.Regex Words();

    private static RunResult RunSetup(string exePath, TimeSpan timeout, bool shell, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(exePath) { UseShellExecute = shell, CreateNoWindow = !shell, WorkingDirectory = Path.GetDirectoryName(exePath) ?? "" };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The setup program could not be started.");
            if (!process.WaitForExit(timeout)) { try { process.Kill(true); } catch (InvalidOperationException) { } return new(1460, "The setup program did not finish in time."); }
            return new(process.ExitCode, "");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return new(1223, ex.Message); }
    }

    public RunResult Reinstall(string msiPath, MsiProduct product)
    {
        if (!string.Equals(Path.GetExtension(msiPath), ".msi", StringComparison.OrdinalIgnoreCase) || !File.Exists(msiPath)) return new(87, "That is not an installer file.");
        var info = ReadPackage(msiPath);
        if (info is null || !string.Equals(info.ProductCode, product.Code, StringComparison.OrdinalIgnoreCase)) return new(87, "That installer file is for a different program.");
        // Windows Installer may have no package code for it; when it has one, the file has to be the very same build.
        if (RegisteredPackageCode(product) is { } registered && info.PackageCode.Length > 0 && !string.Equals(registered, info.PackageCode, StringComparison.OrdinalIgnoreCase))
            return new(87, "That installer file is a different build of the program.");

        ClearStaleRollbackFolders();

        // "v" runs from the original source and re-caches the package, which is what puts a lost saved copy back. The program's own saved copy is already the cache, and with "v"
        // Windows Installer insists on the original source (error 1706) that is usually gone, so a repair from the saved copy tries "/fomus" first.
        // If that fails and the cached MSI carries its own embedded cabinets (#cab), staging it under its registered PackageName lets "/fvomus" (or "/fvemus" for GAC packages) repair self-contained.
        var saved = !string.IsNullOrWhiteSpace(product.LocalPackage) && string.Equals(Path.GetFullPath(msiPath), Path.GetFullPath(product.LocalPackage), StringComparison.OrdinalIgnoreCase);
        if (saved)
        {
            var first = RunMsiExec("/fomus", msiPath, "/qn");
            if (first.ExitCode is 0 or 3010 or 1641 || !HasEmbeddedCabinets(msiPath))
                return first;
        }

        var flags = NeedsEqualReinstall(msiPath) ? "/fvemus" : "/fvomus";
        return RunStagedMsiExec(flags, msiPath, product);
    }

    private static RunResult RunMsiExec(string flags, string msiPath, string ui, params string[] properties)
    {
        var log = Path.Combine(Path.GetTempPath(), "TidyWindow-repair-" + Guid.NewGuid().ToString("N")[..8] + ".log");
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { flags, msiPath, ui, "/norestart", "/l*v", log }) start.ArgumentList.Add(argument);
        foreach (var prop in properties) start.ArgumentList.Add(prop);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows Installer could not be started.");
        if (!process.WaitForExit(TimeSpan.FromMinutes(20))) { try { process.Kill(); } catch (InvalidOperationException) { } return new(1460, "Windows Installer did not finish in 20 minutes.\nLog: " + log); }
        var result = new RunResult(process.ExitCode, "Log: " + log);
        if (result.ExitCode is not (0 or 3010 or 1641) && properties.Length == 0 && FailedOnRollbackFolder(log))
        {
            ClearStaleRollbackFolders();
            return RunMsiExec(flags, msiPath, ui, "DISABLEROLLBACK=1");
        }
        // msiexec's own exit code is the client's. When the install server finished and reconfigured the product without rolling anything back, the repair worked.
        if (result.ExitCode == 1603 && ServerFinishedCleanly(log) is { } code)
            return new(code, result.Output + "\nWindows Installer's exit code said 1603, but its log shows the repair finished and the program was reconfigured without error.");
        // A custom action (a program the package runs itself) can fail with no window and work with one: run it once more with the progress bar showing, which asks nothing.
        if (result.ExitCode == 1603 && ui == "/qn" && CustomActionFailed(log))
        {
            var again = RunMsiExec(flags, msiPath, "/qb", properties);
            return new(again.ExitCode, again.Output + "\nIt failed with 1603 when run without a window, so it was run again with Windows Installer's progress bar showing.");
        }
        return result;
    }

    /// <summary>Whether the log shows a step the package runs itself (a custom action) failing: error 1721 to 1724, or an action that "returned actual error code".</summary>
    internal static bool CustomActionFailed(string logPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(logPath);
            var text = bytes.Length > 1 && (bytes[1] == 0 || (bytes[0] == 0xFF && bytes[1] == 0xFE)) ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes);
            return CustomActionError().IsMatch(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?:Error |Note: 1: )172[1-4]\b|returned actual error code \d+")]
    private static partial System.Text.RegularExpressions.Regex CustomActionError();

    /// <summary>
    /// When the install server (<c>MSI (s)</c>) finished with 0 or 3010 and logged "reconfiguration success or error status: 0" with nothing rolled back, that code; otherwise null.
    /// The client (<c>MSI (c)</c>) can still report 1603 afterwards, for instance when the cached package was replaced and the old one is only deleted at the next restart.
    /// </summary>
    internal static int? ServerFinishedCleanly(string logPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(logPath);
            var text = bytes.Length > 1 && (bytes[1] == 0 || (bytes[0] == 0xFF && bytes[1] == 0xFE)) ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes);
            if (text.Contains("Error in rollback", StringComparison.Ordinal)) return null;
            if (!text.Contains("reconfiguration success or error status: 0", StringComparison.OrdinalIgnoreCase)) return null;
            var server = System.Text.RegularExpressions.Regex.Matches(text, @"MSI \(s\) [^\r\n]*MainEngineThread is returning (\d+)");
            if (server.Count == 0) return null;
            var last = int.Parse(server[^1].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            return last is 0 or 3010 ? last : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FormatException or OverflowException) { return null; }
    }

    internal static bool FailedOnRollbackFolder(string logPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(logPath);
            var text = bytes.Length > 1 && (bytes[1] == 0 || (bytes[0] == 0xFF && bytes[1] == 0xFE)) ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes);
            return text.Contains("Backup folder is created by user", StringComparison.Ordinal) && text.Contains("Error in rollback skipped", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>
    /// Runs <c>msiexec</c> with <c>v</c> against a copy staged under the product's registered <c>PackageName</c> when the file is in <c>C:\Windows\Installer</c>
    /// or has a different name on disk (such as a WinGet download), so Windows Installer's SecureRepair (<c>Error 1316</c>) and custom actions see the exact filename expected.
    /// </summary>
    private RunResult RunStagedMsiExec(string flags, string msiPath, MsiProduct product)
    {
        var full = Path.GetFullPath(msiPath);
        var wanted = RegisteredPackageName(product);
        var installerCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Installer").TrimEnd('\\') + "\\";
        var inCache = full.StartsWith(installerCache, StringComparison.OrdinalIgnoreCase);
        var needsRename = wanted is not null && !string.Equals(Path.GetFileName(full), wanted, StringComparison.OrdinalIgnoreCase);
        if (!inCache && !needsRename) return RunMsiExec(flags, full, "/qn");

        var stageDir = Path.Combine(Path.GetTempPath(), "TidyWindow-stage-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(stageDir);
            var staged = Path.Combine(stageDir, wanted ?? Path.GetFileName(full));
            File.Copy(full, staged, true);
            if (!inCache && Path.GetDirectoryName(full) is { Length: > 0 } sourceDir)
            {
                foreach (var cab in Safe(() => Directory.EnumerateFiles(sourceDir, "*.cab")))
                {
                    var destCab = Path.Combine(stageDir, Path.GetFileName(cab));
                    if (!CreateHardLink(destCab, cab, IntPtr.Zero)) File.Copy(cab, destCab, true);
                }
            }
            return RunMsiExec(flags, staged, "/qn");
        }
        finally
        {
            try { if (Directory.Exists(stageDir)) Directory.Delete(stageDir, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Removes an orphaned <c>C:\Config.Msi</c> (or <c>C:\Windows\Installer\Config.Msi</c>) left behind with locked permissions when no install is active.
    /// When <c>C:\Config.Msi</c> pre-exists with broken ACLs, <c>msi.dll</c> logs <c>Backup folder is created by user</c> / <c>Note: 1: 2265 ... -2147287035</c>
    /// and rolls back any repair that backs up files at the end of <c>InstallFinalize</c> with exit code 1603.
    /// </summary>
    internal static void ClearStaleRollbackFolders()
    {
        if (IsInstallerBusy()) return;
        try
        {
            using var inProgress = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\InProgress");
            if (inProgress is not null) return;
            // Saved rollback information that Windows Installer still lists is what an interrupted install would be undone from: the Repair scan never offers to remove it, and neither does this.
            using var rollback = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\Rollback");
            if (rollback is not null) return;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return; }

        var drive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\', '/') + @"\";
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var folder in new[] { Path.Combine(drive, "Config.Msi"), Path.Combine(windows, "Installer", "Config.Msi") })
        {
            if (!Directory.Exists(folder)) continue;
            try
            {
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(folder, false);
                    continue;
                }
                File.SetAttributes(folder, FileAttributes.Normal);
                Directory.Delete(folder, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (!Directory.Exists(folder)) continue;

            UnlockRollbackFolder(folder);
            try
            {
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) { Directory.Delete(folder, false); continue; }
                Directory.Delete(folder, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static void UnlockRollbackFolder(string folder)
    {
        if (!Directory.Exists(folder)) return;
        try { if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (OperatingSystem.IsWindows()) RegistryNative.EnablePrivileges();
        Run("takeown.exe", "/F", folder, "/A");
        Run("icacls.exe", folder, "/grant", "*S-1-5-32-544:F", "*S-1-5-18:F", "/C", "/Q");
        Run("takeown.exe", "/F", folder, "/A", "/R", "/D", "Y");
        Run("icacls.exe", folder, "/setowner", "*S-1-5-32-544", "/T", "/C", "/Q");
        Run("icacls.exe", folder, "/grant", "*S-1-5-32-544:F", "*S-1-5-18:F", "/T", "/C", "/Q");
        try
        {
            var info = new DirectoryInfo(folder);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return;
            info.Attributes = FileAttributes.Normal;
            foreach (var entry in info.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                try { entry.Attributes = FileAttributes.Normal; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool IsInstallerBusy()
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

    public bool StopInstallerService()
    {
        Run("sc.exe", "stop", "msiserver");
        for (var i = 0; i < 20; i++)
        {
            var state = ServiceState();
            if (state is "STOPPED") return true;
            Thread.Sleep(1000);
        }
        // A service stuck stopping is ended by force, only ever this one, only when it is its own process.
        Run("taskkill.exe", "/F", "/FI", "SERVICES eq msiserver");
        Thread.Sleep(1500);
        return ServiceState() is "STOPPED";
    }

    /// <summary>START_PENDING, STOP_PENDING, RUNNING, STOPPED, or null when it cannot be read.</summary>
    public static string? ServiceState()
    {
        var output = Run("sc.exe", "query", "msiserver");
        foreach (var state in new[] { "START_PENDING", "STOP_PENDING", "CONTINUE_PENDING", "PAUSE_PENDING", "RUNNING", "STOPPED" })
            if (output.Contains(state, StringComparison.Ordinal)) return state;
        return null;
    }

    private static string Run(string file, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, file)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return "";
            var text = process.StandardOutput.ReadToEnd();
            process.WaitForExit(30000);
            return text;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return ""; }
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiOpenDatabase(string path, IntPtr persist, out IntPtr database);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiDatabaseOpenView(IntPtr database, string query, out IntPtr view);

    [DllImport("msi.dll")]
    private static extern uint MsiViewExecute(IntPtr view, IntPtr record);

    [DllImport("msi.dll")]
    private static extern uint MsiViewFetch(IntPtr view, out IntPtr record);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiRecordGetString(IntPtr record, uint field, StringBuilder value, ref uint length);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiGetSummaryInformation(IntPtr database, string? path, uint updates, out IntPtr summary);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiSummaryInfoGetProperty(IntPtr summary, uint property, ref uint type, ref int number, ref long time, StringBuilder text, ref uint length);

    [DllImport("msi.dll")]
    private static extern uint MsiCloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
