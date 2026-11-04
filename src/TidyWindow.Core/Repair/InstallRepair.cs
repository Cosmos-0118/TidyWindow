using System.Text;
using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

public enum RepairKind { BrokenEntry, InstallerRecord, InstallLock, RunningInstaller, PendingRestart, BundleRecord, DanglingLinks, InstallerService, MissingPatch, BrokenShortcut, EmptyFolder, InstallerFiles }
public enum RepairLevel { Recommended, Review, Info }

/// <summary>One program as Windows lists it in "Installed apps", read from the registry.</summary>
public sealed record UninstallEntry(string Key, string Name, string Version, string Publisher, string UninstallString, string QuietUninstallString,
    string InstallLocation, string DisplayIcon, bool WindowsInstaller, bool SystemComponent, bool NoRemove, bool IsUpdate, string BundleProvider = "", string InstallSource = "")
{
    public string KeyName => Key[(Key.LastIndexOf('\\') + 1)..];

    /// <summary>
    /// Installed from the Visual Studio Installer's package cache, which is where everything Visual Studio sets up comes from. Most of it repairs like any other program (IIS Express, Web Deploy, LocalDB),
    /// but a few packages (the .NET SDK) refuse to run on their own, which only the package's launch conditions say: <see cref="IInstallSystem.RefusesWithoutVisualStudio"/> decides, this only says where to look.
    /// </summary>
    public bool FromVisualStudioCache => InstallSource.Replace('/', '\\').Contains(@"\Microsoft\VisualStudio\Packages\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Visual Studio puts " from Visual Studio" in the name of the packages that refuse to run without it: enough on its own when the saved installer cannot be read.</summary>
    public bool NamedFromVisualStudio => Name.Contains(" from Visual Studio", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A product Windows Installer knows about. <see cref="Context"/> is Machine, User or Managed (installed for one user by an administrator). <see cref="Publisher"/> is who its record says made it, when it says.</summary>
public sealed record MsiProduct(string Code, string Context, string Sid, string Name, string? LocalPackage, string Publisher = "");

public sealed record RunningInstaller(int Id, string Name, DateTimeOffset? Started);

/// <summary>An update (patch) a program wears whose saved .msp file is gone, or is there but empty or not a patch (<see cref="Damaged"/>). <see cref="Keys"/> are the registrations of that patch to remove: the program's own links to it, and the patch's shared records when nothing else wears it.</summary>
public sealed record DeadPatch(string Patch, string File, IReadOnlyList<string> Programs, IReadOnlyList<string> Keys, bool Damaged = false);

/// <summary>Something wrong that a repair can fix, with exactly the registry keys that fixing it removes.</summary>
public sealed record RepairFinding(string Id, RepairKind Kind, string Title, string Detail, string Evidence, RepairLevel Level, IReadOnlyList<string> Keys, int? ProcessId = null,
    FootprintRequest? Footprint = null, IReadOnlyList<ValueRef>? Links = null, MsiProduct? Product = null, bool Alive = false, bool ServiceStuck = false, UninstallEntry? Entry = null,
    IReadOnlyList<string>? Shortcuts = null, bool MachineShortcuts = false, IReadOnlyList<string>? Folders = null, bool MachineFolders = false, DeadPatch? Patch = null)
{
    // A product's record always reaches Windows-wide keys, even a per-user one, because Windows Installer keeps its bookkeeping under UserData.
    public bool NeedsAdmin => Footprint is not null || ServiceStuck || MachineShortcuts || MachineFolders || Keys.Any(InstallRepair.IsMachine) || (Links?.Any(x => InstallRepair.IsMachine(x.Path)) ?? false);
    public bool CanFix => Keys.Count > 0 || ProcessId is not null || Footprint is not null || Links is { Count: > 0 } || ServiceStuck || Shortcuts is { Count: > 0 } || Folders is { Count: > 0 };
}

public sealed record RepairScan(IReadOnlyList<UninstallEntry> Entries, IReadOnlyList<MsiProduct> Products, IReadOnlyList<RepairFinding> Findings);

/// <summary>Everything the scan needs to know about the PC, so the checks can be tested without touching a real one.</summary>
public interface IInstallSystem
{
    IReadOnlyList<UninstallEntry> Entries();
    IReadOnlyList<MsiProduct> Products();
    /// <summary>Windows Installer's own answer for a product code; -1 means it has never heard of it.</summary>
    int ProductState(string code);
    bool FileExists(string path);
    bool FolderExists(string path);
    bool DriveAvailable(string path);
    string? FindOnPath(string fileName);
    string Expand(string text);
    bool InstallLockExists();
    bool InstallerBusy();
    IReadOnlyList<RunningInstaller> Installers();
    IReadOnlyList<string> PendingRestart();
    bool EndProcess(int id);
    /// <summary>Programs Windows Installer holds only part of a record for, which it cannot repair, remove or reinstall.</summary>
    IReadOnlyList<MsiProduct> Ghosts() => [];
    /// <summary>Links under upgrade codes that name a program Windows Installer no longer has.</summary>
    IReadOnlyList<ValueRef> DanglingLinks() => [];
    /// <summary>Whether any file or folder Windows Installer recorded for the product is still on the PC: a program with no install folder in its list entry can still be installed.</summary>
    bool ProductFilesPresent(MsiProduct product) => false;
    /// <summary>A Burn bundle (by its own id) that still has installed Windows Installer products listing it as their dependent: its program is still on the PC.</summary>
    bool BundleHasChildren(string bundleId) => false;
    /// <summary>The Windows Installer service is stuck starting or stopping, with nothing installing.</summary>
    bool InstallerServiceStuck() => false;
    /// <summary>Called once at the start of every scan, so anything read from the registry for the last scan is read again.</summary>
    void BeginScan() { }
    /// <summary>Updates whose saved patch file is gone (or is there but empty or not a patch) while a program still wears them: what repairing, updating or removing that program trips over (error 1612 or 1714).</summary>
    IReadOnlyList<DeadPatch> DeadPatches() => [];
    /// <summary>Whether the file Windows Installer saved for a program is there but unusable: empty, cut short, not an installer file at all, or another program's. A file that merely cannot be read right now is not called damaged.</summary>
    bool SavedCopyDamaged(MsiProduct product) => false;
    /// <summary>Whether the program's own saved installer says it can only be changed through the Visual Studio Installer (a launch condition refuses every other run).</summary>
    bool RefusesWithoutVisualStudio(MsiProduct product) => false;
    /// <summary>Windows Installer's saved rollback information, left behind by an install that was cut short.</summary>
    bool InstallRollbackExists() => false;
    /// <summary>Removes a folder only if nothing is in it; returns whether it is gone.</summary>
    bool RemoveEmptyFolder(string path) => true;
    /// <summary>The files directly in a folder, or none when it cannot be read.</summary>
    IReadOnlyList<string> FilesIn(string folder) => [];
    /// <summary>The folders Windows lists shortcuts from: the Start menu, the desktops, the taskbar's pins and the Send to menu.</summary>
    IReadOnlyList<ShortcutRoot> ShortcutRoots() => [];
    /// <summary>Every .lnk and .url file in those folders (the Start menu's subfolders included), read without resolving them.</summary>
    IReadOnlyList<ShortcutFile> Shortcuts() => [];
    /// <summary>Every folder under a Start menu, with what is directly in it.</summary>
    IReadOnlyList<ShortcutFolder> ShortcutFolders() => [];
    /// <summary>Tells Windows these shortcuts or folders changed, so the Start menu, search and Explorer drop them without a restart.</summary>
    void ShellChanged(IReadOnlyList<string> paths) { }
    /// <summary>
    /// The folders Windows Installer and Burn keep for a product code that are still on disk: the icon folders (C:\Windows\Installer, %APPDATA%\Microsoft\Installer),
    /// and with <paramref name="packageCache"/> the Package Cache folders named after it.
    /// </summary>
    IReadOnlyList<string> InstallerLeftovers(string code, bool packageCache) => [];
    /// <summary>Icon folders under C:\Windows\Installer (and the per-user one) whose product Windows Installer has no record of anywhere, and that have not changed for two days.</summary>
    IReadOnlyList<string> OrphanInstallerFolders() => [];
    /// <summary>The hidden C:\Config.Msi where an install that was cut short left its rollback files; null when it is not there.</summary>
    string? RollbackFolder() => null;
}

/// <summary>Whatever changes registry keys: the signed-in user's own, or the elevated helper. Every change hands back a full copy so it can be undone.</summary>
public interface IKeyEditor
{
    bool IsAllowed(string path) => true;
    bool Exists(string path);
    KeyBackup? Snapshot(string path);
    KeyBackup? Delete(string path);
    KeyBackup? DeleteValue(string path, string name);
    KeyBackup? SetText(string path, string name, string value);
    void Restore(KeyBackup backup);
    FootprintPlan Footprint(FootprintRequest request);
}

public static partial class InstallRepair
{
    public const int Unknown = -1;

    public static bool IsMachine(string path) => path.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase);

    public const string InstallLockKey = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\InProgress";
    public const string InstallRollbackKey = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\Rollback";
    /// <summary>INSTALLSTATE_DEFAULT: Windows Installer says the program is installed on this PC.</summary>
    private const int InstalledState = 5;
    private static readonly string[] UpdateTypes = ["Update", "Hotfix", "Security Update", "Update Rollup", "Service Pack"];

    /// <param name="shortcuts">Also look through the Start menu, desktops, taskbar and Send to menu for shortcuts that lead nowhere; a caller that only wants the install records leaves it out.</param>
    public static RepairScan Scan(IInstallSystem system, bool shortcuts = true)
    {
        system.BeginScan();
        var entries = system.Entries();
        var products = system.Products();
        var findings = new List<RepairFinding>();
        findings.AddRange(BrokenEntries(system, entries));
        findings.AddRange(BrokenRecords(system, products, entries));
        findings.AddRange(Ghosts(system, products));
        findings.AddRange(DeadPatches(system));
        findings.AddRange(DanglingLinks(system));
        findings.AddRange(StuckInstalls(system));
        if (shortcuts)
        {
            findings.AddRange(StaleShortcutFindings(system));
            findings.AddRange(OrphanedInstallerFindings(system));
        }
        return new(entries, products, findings);
    }

    // ---- Programs that Windows still lists but that cannot be removed or reinstalled ----

    private static IEnumerable<RepairFinding> BrokenEntries(IInstallSystem system, IReadOnlyList<UninstallEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || entry.IsUpdate) continue;
            var code = ProductCode(entry);
            // A hidden entry is checked only when it is a Windows Installer product: Burn's child packages hide themselves, and one Windows Installer has forgotten still blocks a reinstall.
            if (entry.SystemComponent && code is null) continue;
            if (code is not null)
            {
                if (system.ProductState(code) != Unknown) continue;
                yield return new($"entry:{entry.Key}", RepairKind.BrokenEntry, entry.Name,
                    "Windows Installer has no record of this program any more, so removing or reinstalling it fails. Only the list entry is left over.",
                    entry.Key, RepairLevel.Recommended, [entry.Key]);
                continue;
            }

            var command = string.IsNullOrWhiteSpace(entry.UninstallString) ? entry.QuietUninstallString : entry.UninstallString;
            string reason;
            string? innoData = null;
            if (string.IsNullOrWhiteSpace(command))
            {
                if (entry.NoRemove) continue;
                reason = "It has no uninstaller.";
            }
            else
            {
                var found = Launched(system, command);
                innoData = MissingInnoData(system, command);
                if (innoData is null)
                {
                    if (found.Count == 0 || found.Any(x => Exists(system, x) || IsUri(x))) continue;
                    // A program on a drive that is not plugged in is not broken, only out of reach.
                    if (found.Any(x => !system.DriveAvailable(system.Expand(x)))) continue;
                    reason = "Its uninstaller is missing.";
                }
                else reason = $"Its uninstaller needs {Path.GetFileName(innoData)}, which is missing.";
            }

            // An uninstaller that is there is proof enough that the program is.
            var alive = innoData is not null || StillThere(system, entry) || (entry.BundleProvider.Length > 0 && system.BundleHasChildren(entry.KeyName));
            // Only an entry that says where the program lives, with every one of those places gone, is called safe to remove.
            var named = entry.InstallLocation.Trim().Length > 0 || entry.DisplayIcon.Trim().Length > 0;
            var cached = CacheMarker().IsMatch(command ?? "");
            var (detail, level) =
                innoData is not null ? ($"{reason} Inno Setup writes that file when the program is installed and it cannot be rebuilt, so Windows cannot uninstall the program. Installing the same version over it puts it back, which TidyWindow can do with WinGet when downloading is allowed.", RepairLevel.Review)
                : alive ? ($"{reason} The program's files are still on this PC, so check that you no longer need it before removing the entry.", RepairLevel.Review)
                : cached ? ($"{reason} Windows keeps it in a cache that has been cleared. Running the program's installer again restores it; remove the entry only if you do not want the program.", RepairLevel.Review)
                : !named ? ($"{reason} It does not say where the program lives, so TidyWindow cannot tell whether it is still installed.", RepairLevel.Review)
                : ($"{reason} The program's files are gone too, so this entry only blocks reinstalling.", RepairLevel.Recommended);
            // A Burn bundle (Python, Visual C++ and many more) also keeps dependency records that name it, and they go with it.
            yield return entry.BundleProvider.Length > 0
                ? new($"entry:{entry.Key}", RepairKind.BundleRecord, entry.Name, detail, entry.Key, level, [entry.Key], Footprint: new("bundle", Keys: [entry.Key]), Alive: alive, Entry: entry)
                : new($"entry:{entry.Key}", RepairKind.BrokenEntry, entry.Name, detail, entry.Key, level, [entry.Key], Alive: alive, Entry: entry);
        }
    }

    public static string? ProductCode(UninstallEntry entry)
    {
        var name = entry.KeyName;
        var msiexec = MsiExec().IsMatch(entry.UninstallString) || MsiExec().IsMatch(entry.QuietUninstallString);
        if (ProductGuid().IsMatch(name) && (entry.WindowsInstaller || msiexec)) return name.ToUpperInvariant();
        foreach (var text in new[] { entry.UninstallString, entry.QuietUninstallString })
            if (MsiExec().IsMatch(text) && MsiCode().Match(text) is { Success: true } match) return match.Groups[1].Value.ToUpperInvariant();
        return null;
    }

    /// <summary>Whether the command a list entry runs to uninstall points at something that is there.</summary>
    public static bool HasUninstaller(IInstallSystem system, UninstallEntry entry)
    {
        var command = string.IsNullOrWhiteSpace(entry.UninstallString) ? entry.QuietUninstallString : entry.UninstallString;
        if (string.IsNullOrWhiteSpace(command)) return false;
        var found = Launched(system, command);
        return found.Count > 0 && found.Any(x => Exists(system, x) || IsUri(x)) && MissingInnoData(system, command) is null;
    }

    /// <summary>
    /// Inno Setup's uninstaller (unins000.exe) only runs with the unins000.dat beside it. The .dat is written when the program is installed and is not in the setup program,
    /// so a program whose .dat is gone cannot be uninstalled. Returns the missing .dat, or null when the command is not an Inno uninstaller or has its data.
    /// </summary>
    internal static string? MissingInnoData(IInstallSystem system, string command)
    {
        foreach (var candidate in Launched(system, command))
        {
            var path = system.Expand(candidate.Trim().Trim('"'));
            if (!InnoUninstaller().IsMatch(Path.GetFileName(path)) || !system.FileExists(path)) continue;
            var data = Path.ChangeExtension(path, ".dat");
            return system.FileExists(data) ? null : data;
        }
        return null;
    }

    private static readonly string[] Hosts = ["cmd", "powershell", "pwsh", "wscript", "cscript", "mshta", "rundll32"];

    /// <summary>
    /// What an uninstall command has to find to work. Where it only hands a script or library to a system host (cmd, PowerShell, rundll32), that script or library is what counts:
    /// the host itself is always there, so a deleted script would otherwise look healthy.
    /// </summary>
    private static List<string> Launched(IInstallSystem system, string command)
    {
        var found = Candidates(command).ToList();
        if (found.Count == 0 || !Hosts.Contains(Path.GetFileNameWithoutExtension(system.Expand(found[0].Trim().Trim('"'))), StringComparer.OrdinalIgnoreCase)) return found;
        var wrapped = IconCommand.Targets(command, system.Expand).Select(x => x.Path).Where(x => x.IndexOfAny(['\\', '/']) >= 0).ToList();
        return wrapped.Count > 0 ? wrapped : found;
    }

    private static bool Exists(IInstallSystem system, string candidate)
    {
        var path = system.Expand(candidate.Trim().Trim('"'));
        if (path.Length == 0) return false;
        if (path.IndexOfAny(['\\', '/']) < 0)
            return system.FindOnPath(path) is not null || (!Path.HasExtension(path) && system.FindOnPath(path + ".exe") is not null);
        return system.FileExists(path) || (!Path.HasExtension(path) && system.FileExists(path + ".exe"));
    }

    private static bool IsUri(string candidate) => candidate.Contains(':') && !candidate.Contains('\\') && !candidate.Contains('/');

    /// <summary>The files an uninstall command might be running: quoted text, text up to an executable extension, or each leading run of words.</summary>
    public static IEnumerable<string> Candidates(string command)
    {
        var text = command.Trim();
        if (text.Length == 0) yield break;
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            if (end > 1) yield return text[1..end];
            yield break;
        }
        if (ExecutablePrefix().Match(text) is { Success: true } match)
        {
            yield return match.Groups[1].Value;
            // A prefix that swallowed a switch ("uninst /app=C:\Vendor\app.exe") may have taken an argument for the program's name.
            if (!SwitchInside().IsMatch(match.Groups[1].Value)) yield break;
        }
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var count = words.Length; count >= 1; count--) yield return string.Join(' ', words.Take(count));
    }

    private static bool StillThere(IInstallSystem system, UninstallEntry entry)
    {
        var folder = system.Expand(entry.InstallLocation.Trim().Trim('"'));
        if (folder.Length > 0 && system.FolderExists(folder)) return true;
        var icon = entry.DisplayIcon.Trim();
        if (icon.Length == 0) return false;
        icon = icon.StartsWith('"') && icon.IndexOf('"', 1) is > 0 and var end ? icon[1..end] : icon;
        var comma = icon.LastIndexOf(',');
        if (comma > 0 && int.TryParse(icon[(comma + 1)..], out _)) icon = icon[..comma];
        return system.FileExists(system.Expand(icon.Trim().Trim('"')));
    }

    // ---- Windows Installer records whose saved installer file is gone ----

    private static IEnumerable<RepairFinding> BrokenRecords(IInstallSystem system, IReadOnlyList<MsiProduct> products, IReadOnlyList<UninstallEntry> entries)
    {
        foreach (var product in products)
        {
            // A record with no saved copy named at all is as broken as one naming a file that is gone, but only when Windows Installer says the program is installed:
            // a program that is only advertised has no saved copy yet.
            var unnamed = string.IsNullOrWhiteSpace(product.LocalPackage);
            var damaged = false;
            if (unnamed ? system.ProductState(product.Code) != InstalledState : system.FileExists(product.LocalPackage!) && !(damaged = system.SavedCopyDamaged(product))) continue;
            var arp = entries.Where(x => string.Equals(x.KeyName, product.Code, StringComparison.OrdinalIgnoreCase)).ToList();
            var alive = arp.Any(x => StillThere(system, x)) || system.ProductFilesPresent(product);
            var name = string.IsNullOrWhiteSpace(product.Name) ? arp.FirstOrDefault()?.Name ?? product.Code : product.Name;
            var keys = RecordKeys(product).Concat(arp.Select(x => x.Key)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var lost = unnamed ? "Windows Installer has no saved copy recorded that it needs to repair or uninstall this program"
                : damaged ? "Windows Installer's saved copy of this program's installer is damaged (empty, cut short or not its installer), so it cannot repair or uninstall the program"
                : "Windows Installer lost the saved copy it needs to repair or uninstall this program";
            yield return new($"record:{product.Code}", RepairKind.InstallerRecord, name,
                alive ? $"{lost}, and the program is still on this PC. TidyWindow first tries to restore the copy from the program's own installer files; if it cannot, it removes the record so the program can be installed again."
                      : $"{lost}, and the program is gone. Its leftover record blocks installing it again.",
                product.Code, alive ? RepairLevel.Review : RepairLevel.Recommended, keys,
                Footprint: new("product", product.Code, product.Context, product.Sid, arp.Select(x => x.Key).ToList()), Product: product, Alive: alive);
        }
    }

    /// <summary>Programs whose record Windows Installer holds only half of, as an interrupted repair or a half-finished undo leaves them.</summary>
    private static IEnumerable<RepairFinding> Ghosts(IInstallSystem system, IReadOnlyList<MsiProduct> products)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ghost in system.Ghosts())
        {
            if (products.Any(x => string.Equals(x.Code, ghost.Code, StringComparison.OrdinalIgnoreCase)) || !seen.Add(ghost.Code)) continue;
            yield return new($"ghost:{ghost.Code}", RepairKind.InstallerRecord, string.IsNullOrWhiteSpace(ghost.Name) ? ghost.Code : ghost.Name,
                "Windows Installer holds part of this program's record but not the rest, so it can neither repair, remove nor reinstall it. This is what an install or repair that was cut short leaves behind. Removing the rest of the record lets the program be installed again.",
                ghost.Code, RepairLevel.Review, RecordKeys(ghost), Footprint: new("product", ghost.Code, ghost.Context, ghost.Sid), Product: ghost);
        }
    }

    /// <summary>Updates whose saved file is gone: the program still lists them, so repairing, updating or removing it asks for a file that no longer exists.</summary>
    private static IEnumerable<RepairFinding> DeadPatches(IInstallSystem system)
    {
        foreach (var patch in system.DeadPatches())
        {
            var programs = patch.Programs.Count == 0 ? "a program" : string.Join(", ", patch.Programs.Take(3)) + (patch.Programs.Count > 3 ? $" and {patch.Programs.Count - 3} more" : "");
            yield return new($"patch:{patch.Patch}", RepairKind.MissingPatch, $"{(patch.Damaged ? "Damaged" : "Missing")} update file for {(patch.Programs.Count == 0 ? "a program" : patch.Programs[0])}",
                $"{programs} wears an update whose saved file ({Path.GetFileName(patch.File)}) is {(patch.Damaged ? "damaged" : "gone")}, so repairing, updating or removing it can fail with error 1612 or 1714. TidyWindow first looks for that update's own .msp file and puts it back; only when none is found does it remove the update's registration, which lets Windows Installer carry on (the changes the update made to the program's files stay).",
                patch.File, RepairLevel.Review, patch.Keys, Patch: patch);
        }
    }

    private static IEnumerable<RepairFinding> DanglingLinks(IInstallSystem system)
    {
        var links = system.DanglingLinks();
        if (links.Count == 0) yield break;
        yield return new("links", RepairKind.DanglingLinks, "Leftover links to removed programs",
            "Windows Installer still has links to older versions of programs that are no longer installed. A new install of the same program can follow one and ask for an installer file that no longer exists.",
            links.Count == 1 ? "1 link" : $"{links.Count} links", RepairLevel.Recommended, [], Links: links);
    }

    /// <summary>The registry keys Windows Installer keeps for one product.</summary>
    public static IReadOnlyList<string> RecordKeys(MsiProduct product)
    {
        var packed = Pack(product.Code);
        var data = $@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\{(product.Context == "Machine" ? "S-1-5-18" : product.Sid)}\Products\{packed}";
        if (product.Context == "Managed")
        {
            var managed = $@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\Managed\{product.Sid}\Installer";
            return [data, $@"{managed}\Products\{packed}", $@"{managed}\Features\{packed}"];
        }
        return product.Context == "Machine"
            ? [data, $@"HKLM\SOFTWARE\Classes\Installer\Products\{packed}", $@"HKLM\SOFTWARE\Classes\Installer\Features\{packed}"]
            : [data, $@"HKCU\SOFTWARE\Microsoft\Installer\Products\{packed}", $@"HKCU\SOFTWARE\Microsoft\Installer\Features\{packed}"];
    }

    /// <summary>What it takes to remove one program from the list by hand: its entry, plus Windows Installer's record when it has one.</summary>
    public static IReadOnlyList<string> RemovalKeys(UninstallEntry entry, IReadOnlyList<MsiProduct> products)
    {
        var keys = new List<string> { entry.Key };
        if (ProductCode(entry) is { } code)
            foreach (var product in products.Where(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase)))
                keys.AddRange(RecordKeys(product));
        return keys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Turns {AABBCCDD-EEFF-GGHH-IIJJ-KKLLMMNNOOPP} into the 32 characters Windows Installer uses as a key name.</summary>
    public static string Pack(string code)
    {
        var hex = code.Trim('{', '}').Replace("-", "");
        if (hex.Length != 32) throw new ArgumentException("That is not a product code.", nameof(code));
        var packed = new StringBuilder(32);
        packed.Append(Reverse(hex[..8])).Append(Reverse(hex[8..12])).Append(Reverse(hex[12..16]));
        for (var i = 16; i < 32; i += 2) packed.Append(hex[i + 1]).Append(hex[i]);
        return packed.ToString().ToUpperInvariant();
    }

    /// <summary>The reverse of <see cref="Pack"/>: the product code a 32 character key name stands for.</summary>
    public static string Unpack(string packed)
    {
        if (packed.Length != 32) throw new ArgumentException("That is not a packed code.", nameof(packed));
        var code = Pack("{" + packed.Insert(20, "-").Insert(16, "-").Insert(12, "-").Insert(8, "-") + "}");
        return "{" + code.Insert(20, "-").Insert(16, "-").Insert(12, "-").Insert(8, "-") + "}";
    }

    private static string Reverse(string text) { var chars = text.ToCharArray(); Array.Reverse(chars); return new string(chars); }

    // ---- An interrupted install that is still in the way ----

    private static IEnumerable<RepairFinding> StuckInstalls(IInstallSystem system)
    {
        var installers = system.Installers();
        var lockExists = system.InstallLockExists();
        if (lockExists && !system.InstallerBusy() && installers.Count == 0)
        {
            // The rollback information an interrupted install saved goes with its marker; on its own it is left alone.
            var rollback = system.InstallRollbackExists();
            yield return new("lock", RepairKind.InstallLock, "Windows thinks an install is still running",
                "An install was interrupted and left its marker behind. Every new install fails with “another installation is in progress” until the marker is removed."
                + (rollback ? " Its saved rollback information is removed with it." : ""),
                InstallLockKey, RepairLevel.Recommended, rollback ? [InstallLockKey, InstallRollbackKey] : [InstallLockKey]);
        }
        else if (!lockExists && !system.InstallerBusy() && installers.Count == 0 && !system.InstallRollbackExists() && system.RollbackFolder() is { } rollbackFolder)
        {
            yield return new("rollback-folder", RepairKind.InstallLock, "Leftover rollback folder from an interrupted install",
                "Windows Installer's temporary Config.Msi folder is still on the drive while no install is running. When its permissions are locked, repairs and installs roll back at the end with error 1603 until it is set aside.",
                rollbackFolder, RepairLevel.Recommended, [], Folders: [rollbackFolder], MachineFolders: RepairFolders.IsMachine(rollbackFolder));
        }

        if (!system.InstallerBusy() && installers.Count == 0 && system.InstallerServiceStuck())
            yield return new("service", RepairKind.InstallerService, "The Windows Installer service is stuck",
                "The service that runs installs is stuck starting or stopping, so installs hang or fail. Stopping it makes Windows start a fresh one.",
                "msiserver", RepairLevel.Recommended, [], ServiceStuck: true);

        foreach (var installer in installers)
        {
            var since = installer.Started is { } started ? $" It started {Ago(DateTimeOffset.Now - started)} ago." : "";
            yield return new($"process:{installer.Id}", RepairKind.RunningInstaller, $"{installer.Name} is still running",
                $"If an install is in progress, let it finish. If nothing is installing, ending it unblocks new installs.{since}",
                $"Process {installer.Id}", RepairLevel.Review, [], installer.Id);
        }

        var reasons = system.PendingRestart();
        if (reasons.Count > 0)
            yield return new("restart", RepairKind.PendingRestart, "Windows is waiting for a restart",
                "An update or install needs a restart to finish. New installs can fail until you restart.", string.Join("; ", reasons), RepairLevel.Info, []);
    }

    private static string Ago(TimeSpan span) => span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes} min" : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} min" : "moments";

    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$")]
    private static partial Regex ProductGuid();
    [GeneratedRegex(@"\\Package Cache\\", RegexOptions.IgnoreCase)]
    private static partial Regex CacheMarker();
    [GeneratedRegex(@"^unins\d{3}\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex InnoUninstaller();
    [GeneratedRegex(@"msiexec", RegexOptions.IgnoreCase)]
    private static partial Regex MsiExec();
    // msiexec takes "/" or "-" for its switches: /x, /i, /f..., /uninstall and /package all name a product by its code.
    [GeneratedRegex(@"(?:^|\s)[/-](?:[xif][a-z]*|uninstall|package)\s*(\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\})", RegexOptions.IgnoreCase)]
    private static partial Regex MsiCode();
    [GeneratedRegex(@"\s(?:/[A-Za-z?]|--?[A-Za-z])")]
    private static partial Regex SwitchInside();
    [GeneratedRegex(@"^(.+?\.(?:exe|bat|cmd|com|msi|cpl|msc|lnk|vbs|ps1))(?=\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ExecutablePrefix();
}

