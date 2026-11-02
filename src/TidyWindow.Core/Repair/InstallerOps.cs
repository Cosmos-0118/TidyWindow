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
    /// <summary>
    /// Repairs one product from its own installer file with <c>msiexec /fvomus</c> (<c>/fomus</c> when the file is the program's own saved copy), which puts a lost saved copy back.
    /// Refuses a file that is not that product's; with administrator rights, also one that is neither signed by the program's publisher nor in a place only administrators can write to.
    /// Cancelling ends Windows Installer and answers 1602; the marker it may leave behind is the caller's to check.
    /// </summary>
    RunResult Reinstall(string msiPath, MsiProduct product, CancellationToken cancellationToken = default);
    /// <summary>Stops the Windows Installer service, ending it by force if it does not stop. Nothing else is ever stopped.</summary>
    bool StopInstallerService();
    /// <summary>
    /// Puts a bundle's cached setup program back where its list entry says it is, from a downloaded copy that is signed by the program's publisher, names the program and is the very file the entry expects.
    /// A Windows-wide bundle's cache (C:\ProgramData\Package Cache) only administrators can write to, so this runs in the helper for those.
    /// The bundle's own <c>/repair</c> is not used: with its saved choices gone it installs the default parts (Python's documentation and test suite) as well.
    /// </summary>
    RunResult RestoreBundleCopy(string exePath, string programName, string publisher, string targetPath);
    /// <summary>Takes back a setup program <see cref="RestoreBundleCopy"/> put in the package cache, and its folder when that is empty. True once the file is gone.</summary>
    bool RemoveCopy(string path);
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
    /// <summary>Unpacks every package out of a program's own setup program (a WiX Burn bundle, with <c>/layout</c>) and returns the .msi files. Only a setup program with a valid signature, from the program's publisher, that names the program is run.</summary>
    IReadOnlyList<string> ExpandBundle(string exePath, string folder, string programName, string publisher);
}

/// <param name="vault">In the administrator helper: where it notes the setup programs it put in the package cache, and stages installer files before running them. The app's own instance has none.</param>
/// <param name="cacheRoots">The only folders a setup program is put into or taken out of: the Windows-wide Package Cache and the account's own. The app's instance takes this account's; the helper is told the account it was started for.</param>
/// <param name="elevated">Running with administrator rights: an installer file is then run only from a copy in a folder only administrators can write to, and only when it is trusted.</param>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsInstallerOps(RepairVault? vault = null, IReadOnlyList<string>? cacheRoots = null, bool elevated = false) : IInstallerOps
{
    private readonly IReadOnlyList<string> cacheRoots = cacheRoots ?? DefaultCacheRoots();

    /// <summary>The Windows-wide Package Cache and this account's own.</summary>
    public static IReadOnlyList<string> DefaultCacheRoots() =>
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Package Cache"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Package Cache")
    ];

    /// <summary>
    /// The Package Cache folders the helper may change for the account it was started for: the Windows-wide one and the one in that account's profile, found through
    /// Windows' own list of profiles (which only administrators can change), not through anything the account itself could point elsewhere.
    /// </summary>
    public static IReadOnlyList<string> CacheRootsFor(string userSid)
    {
        var roots = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Package Cache") };
        try
        {
            using var profile = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{userSid}");
            if (userSid.StartsWith("S-1-", StringComparison.Ordinal) && !userSid.Contains('\\') && profile?.GetValue("ProfileImagePath") is string { Length: > 0 } home && Path.IsPathFullyQualified(home))
                roots.Add(Path.Combine(home, "AppData", "Local", "Package Cache"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException) { }
        return roots;
    }

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

        foreach (var cache in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
            folders.AddRange(Safe(() => Directory.EnumerateDirectories(Path.Combine(cache, "Package Cache"), product.Code + "v*")));
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

    public IReadOnlyList<string> FilesIn(string folder)
    {
        var found = new List<string>();
        foreach (var directory in new[] { folder }.Concat(Safe(() => Directory.EnumerateDirectories(folder))))
            found.AddRange(Safe(() => Directory.EnumerateFiles(directory)).Where(x => Path.GetExtension(x).ToLowerInvariant() is ".msi" or ".exe"));
        return found.Take(400).ToList();
    }

    public IReadOnlyList<string> ExpandBundle(string exePath, string folder, string programName, string publisher)
    {
        // A setup program installs several parts with names of their own, so its details only have to carry the program's first word; its signer has to be the program's publisher.
        if (InstallerTrust.Problem(exePath, programName, publisher, wholeName: false) is not null) return [];
        Directory.CreateDirectory(folder);
        var result = RunSetup(exePath, TimeSpan.FromMinutes(10), false, "/layout", folder, "/quiet");
        return result.ExitCode == 0 ? Safe(() => Directory.EnumerateFiles(folder, "*.msi", SearchOption.AllDirectories)).ToList() : [];
    }

    public RunResult RestoreBundleCopy(string exePath, string programName, string publisher, string targetPath)
    {
        if (CachePath(targetPath) is not { } target) return new(87, "That is not a place Windows keeps a setup program.");
        // The whole name of the bundle has to be in the setup program's own details: this file will be run with administrator rights by Windows' own repair and uninstall of the bundle.
        if (InstallerTrust.Problem(exePath, programName, publisher, wholeName: true) is { } problem) return new(87, $"That setup program {problem}.");
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
            if (InstallerTrust.Problem(staged, programName, publisher, wholeName: true) is { } again) { File.Delete(staged); return new(87, $"That setup program {again}."); }
            // The helper notes what it put there before it is there, so a copy it did not make is never one it takes out.
            vault?.MarkStaged(target);
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
        if (CachePath(path) is not { } target) return false;
        // The helper takes out only the setup programs it put in; the app's own instance reaches only this account's cache.
        if (vault is not null && !vault.WasStaged(target)) return false;
        try
        {
            File.Delete(target);
            var folder = Path.GetDirectoryName(target);
            if (folder is not null && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            if (File.Exists(target)) return false;
            vault?.Unstage(target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// The full path of a setup program inside one of the Package Cache folders this instance may change, or null for anything else: a path that resolves elsewhere
    /// ("..\" is resolved first), a folder merely named "Package Cache" somewhere else, or a path that goes through a link to somewhere else.
    /// </summary>
    internal string? CachePath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(full).Length <= 4) return null;
            var root = cacheRoots.Select(x => Path.TrimEndingDirectorySeparator(Path.GetFullPath(x))).FirstOrDefault(x => full.StartsWith(x + "\\", StringComparison.OrdinalIgnoreCase));
            return root is not null && !CrossesLink(full, root) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Whether the file, or any folder from the cache root down to it, is a link (a junction, a symbolic link, a mount point) that would carry a change somewhere else.</summary>
    private static bool CrossesLink(string full, string root)
    {
        var file = new FileInfo(full);
        if (file.Exists && (file.LinkTarget is not null || (file.Attributes & FileAttributes.ReparsePoint) != 0)) return true;
        for (var folder = Path.GetDirectoryName(full); folder is not null && folder.Length >= root.Length; folder = Path.GetDirectoryName(folder))
        {
            var info = new DirectoryInfo(folder);
            if (info.Exists && (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)) return true;
            if (string.Equals(Path.TrimEndingDirectorySeparator(folder), root, StringComparison.OrdinalIgnoreCase)) break;
        }
        return false;
    }

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

    public RunResult Reinstall(string msiPath, MsiProduct product, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(Path.GetExtension(msiPath), ".msi", StringComparison.OrdinalIgnoreCase) || !File.Exists(msiPath)) return new(87, "That is not an installer file.");
        string full;
        try { full = Path.GetFullPath(msiPath); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return new(87, "That is not an installer file."); }
        // With administrator rights, a file is run only when nobody but an administrator could have put it where it is, or when it is signed by the program's publisher;
        // one in a folder the ordinary account can write to is copied into the helper's own folder and checked there before Windows Installer sees it (see RunStagedMsiExec).
        var guarded = elevated && !InstallerTrust.InProtectedPlace(full);
        if (guarded)
        {
            if (HasExternalCabinets(full)) return new(87, "That installer file keeps its files in separate cabinet files beside it, in a folder anyone could change, so it is not used with administrator rights.");
            if (InstallerTrust.SignatureProblem(full, product.Publisher) is { } problem) return new(87, $"That installer file {problem}, so it is not used with administrator rights.");
        }
        var info = ReadPackage(full);
        if (info is null || !string.Equals(info.ProductCode, product.Code, StringComparison.OrdinalIgnoreCase)) return new(87, "That installer file is for a different program.");
        // Windows Installer may have no package code for it; when it has one, the file has to be the very same build.
        if (RegisteredPackageCode(product) is { } registered && info.PackageCode.Length > 0 && !string.Equals(registered, info.PackageCode, StringComparison.OrdinalIgnoreCase))
            return new(87, "That installer file is a different build of the program.");

        ClearStaleRollbackFolders();

        // "v" runs from the original source and re-caches the package, which is what puts a lost saved copy back. The program's own saved copy is already the cache, and with "v"
        // Windows Installer insists on the original source (error 1706) that is usually gone, so a repair from the saved copy tries "/fomus" first.
        // If that fails and the cached MSI carries its own embedded cabinets (#cab), staging it under its registered PackageName lets "/fvomus" (or "/fvemus" for GAC packages) repair self-contained.
        var saved = !string.IsNullOrWhiteSpace(product.LocalPackage) && string.Equals(full, Path.GetFullPath(product.LocalPackage), StringComparison.OrdinalIgnoreCase);
        if (saved && !guarded)
        {
            var first = RunMsiExec("/fomus", full, cancellationToken);
            if (first.ExitCode is 0 or 3010 or 1641 or 1602 || !HasEmbeddedCabinets(full))
                return first;
        }

        var flags = NeedsEqualReinstall(full) ? "/fvemus" : "/fvomus";
        return RunStagedMsiExec(flags, full, product, guarded, cancellationToken);
    }

    /// <summary>Whether the MSI names cabinet files that sit beside it rather than inside it; what those hold is never checked by a signature on the MSI.</summary>
    internal static bool HasExternalCabinets(string path)
    {
        if (MsiOpenDatabase(path, IntPtr.Zero, out var database) != 0) return false;
        try { return ColumnValues(database, "SELECT `Cabinet` FROM `Media`").Any(x => x.Length > 0 && x[0] != '#'); }
        finally { MsiCloseHandle(database); }
    }

    private static RunResult RunMsiExec(string flags, string msiPath, CancellationToken cancellationToken, params string[] properties)
    {
        var log = Path.Combine(Path.GetTempPath(), "TidyWindow-repair-" + Guid.NewGuid().ToString("N")[..8] + ".log");
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { flags, msiPath, "/qn", "/norestart", "/l*v", log }) start.ArgumentList.Add(argument);
        foreach (var prop in properties) start.ArgumentList.Add(prop);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows Installer could not be started.");
        // Windows Installer does the work in its service and unwinds it when its client goes; ending the client and everything it started is how a stop is asked for.
        using var stop = cancellationToken.Register(() => End(process));
        if (!process.WaitForExit(TimeSpan.FromMinutes(20))) { End(process); return new(1460, "Windows Installer did not finish in 20 minutes.\nLog: " + log); }
        if (cancellationToken.IsCancellationRequested) return new(1602, "The repair was stopped before Windows Installer finished.\nLog: " + log);
        var result = new RunResult(process.ExitCode, "Log: " + log);
        if (result.ExitCode is not (0 or 3010 or 1641) && properties.Length == 0 && FailedOnRollbackFolder(log))
        {
            ClearStaleRollbackFolders();
            return RunMsiExec(flags, msiPath, cancellationToken, "DISABLEROLLBACK=1");
        }
        // msiexec's own exit code is the client's. When the install server finished and reconfigured the product without rolling anything back, the repair worked.
        if (result.ExitCode == 1603 && ServerFinishedCleanly(log) is { } code)
            return new(code, result.Output + "\nWindows Installer's exit code said 1603, but its log shows the repair finished and the program was reconfigured without error.");
        return result;
    }

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
    /// <param name="guarded">The file lies where the ordinary account can write: it is always staged, into the helper's own folder, and the staged copy is checked again before it runs.</param>
    private RunResult RunStagedMsiExec(string flags, string msiPath, MsiProduct product, bool guarded, CancellationToken cancellationToken)
    {
        var full = Path.GetFullPath(msiPath);
        var wanted = RegisteredPackageName(product);
        var installerCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Installer").TrimEnd('\\') + "\\";
        var inCache = full.StartsWith(installerCache, StringComparison.OrdinalIgnoreCase);
        var needsRename = wanted is not null && !string.Equals(Path.GetFileName(full), wanted, StringComparison.OrdinalIgnoreCase);
        if (!inCache && !needsRename && !guarded) return RunMsiExec(flags, full, cancellationToken);

        var stageDir = Path.Combine(guarded && vault is not null ? vault.Stage : Path.GetTempPath(), "TidyWindow-stage-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(stageDir);
            var staged = Path.Combine(stageDir, wanted ?? Path.GetFileName(full));
            File.Copy(full, staged, true);
            if (guarded)
            {
                // The copy now sits where only administrators can write, so what was checked is what runs.
                if (InstallerTrust.SignatureProblem(staged, product.Publisher) is { } problem) return new(87, $"That installer file {problem}, so it is not used with administrator rights.");
                if (ReadPackage(staged) is not { } copy || !string.Equals(copy.ProductCode, product.Code, StringComparison.OrdinalIgnoreCase)) return new(87, "That installer file is for a different program.");
            }
            else if (!inCache && Path.GetDirectoryName(full) is { Length: > 0 } sourceDir)
            {
                foreach (var cab in Safe(() => Directory.EnumerateFiles(sourceDir, "*.cab")))
                {
                    var destCab = Path.Combine(stageDir, Path.GetFileName(cab));
                    if (!CreateHardLink(destCab, cab, IntPtr.Zero)) File.Copy(cab, destCab, true);
                }
            }
            return RunMsiExec(flags, staged, cancellationToken);
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
                // A tree with a link in it is left for the person: deleting through it could reach somewhere else.
                if (HoldsLink(folder)) continue;
                File.SetAttributes(folder, FileAttributes.Normal);
                Directory.Delete(folder, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (!Directory.Exists(folder)) continue;

            UnlockRollbackFolder(folder);
            try
            {
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) { Directory.Delete(folder, false); continue; }
                if (HoldsLink(folder)) continue;
                Directory.Delete(folder, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Whether the folder, or anything under it, is a link to somewhere else. Nothing is followed to find out, and a folder that cannot be looked into counts as holding one.</summary>
    internal static bool HoldsLink(string folder)
    {
        try
        {
            var info = new DirectoryInfo(folder);
            if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0) return true;
            return info.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = 0 })
                .Any(x => x.LinkTarget is not null || (x.Attributes & FileAttributes.ReparsePoint) != 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return true; }
    }

    private static void End(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException) { }
    }

    internal static void UnlockRollbackFolder(string folder)
    {
        if (!Directory.Exists(folder)) return;
        // Anyone can make a folder at the root of the drive. Ownership and permissions are only ever changed below one that is a plain tree: a link anywhere
        // in it would carry the change to wherever the link points.
        if (HoldsLink(folder)) return;
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
