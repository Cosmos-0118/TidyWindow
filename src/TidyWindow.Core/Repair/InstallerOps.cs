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
    /// <summary>Repairs one product from its own installer file with <c>msiexec /fvomus</c>, which also puts the saved copy back. Refuses a file that is not that product's.</summary>
    RunResult Reinstall(string msiPath, MsiProduct product);
    /// <summary>Stops the Windows Installer service, ending it by force if it does not stop. Nothing else is ever stopped.</summary>
    bool StopInstallerService();
    /// <summary>
    /// Puts a bundle's cached setup program back where its list entry says it is, from a downloaded copy that is signed, names the program and is the very file the entry expects.
    /// A Windows-wide bundle's cache (C:\ProgramData\Package Cache) only administrators can write to, so this runs in the helper for those.
    /// The bundle's own <c>/repair</c> is not used: with its saved choices gone it installs the default parts (Python's documentation and test suite) as well.
    /// </summary>
    RunResult RestoreBundleCopy(string exePath, string programName, string targetPath);
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
    /// <summary>Unpacks every package out of a program's own setup program (a WiX Burn bundle, with <c>/layout</c>) and returns the .msi files. Only a setup program with a valid signature that names the program is run.</summary>
    IReadOnlyList<string> ExpandBundle(string exePath, string folder, string programName);
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
        if (PackageCachePath(path) is not { } target) return false;
        try
        {
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
        var log = Path.Combine(Path.GetTempPath(), "TidyWindow-repair-" + Guid.NewGuid().ToString("N")[..8] + ".log");
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "/fvomus", msiPath, "/qn", "/norestart", "/l*v", log }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows Installer could not be started.");
        if (!process.WaitForExit(TimeSpan.FromMinutes(20))) { try { process.Kill(); } catch (InvalidOperationException) { } return new(1460, "Windows Installer did not finish in 20 minutes.\nLog: " + log); }
        return new(process.ExitCode, "Log: " + log);
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
}
