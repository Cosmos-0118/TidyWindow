using System.Text;
using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

public enum RepairKind { BrokenEntry, InstallerRecord, InstallLock, RunningInstaller, PendingRestart }
public enum RepairLevel { Recommended, Review, Info }

/// <summary>One program as Windows lists it in "Installed apps", read from the registry.</summary>
public sealed record UninstallEntry(string Key, string Name, string Version, string Publisher, string UninstallString, string QuietUninstallString,
    string InstallLocation, string DisplayIcon, bool WindowsInstaller, bool SystemComponent, bool NoRemove, bool IsUpdate)
{
    public string KeyName => Key[(Key.LastIndexOf('\\') + 1)..];
}

/// <summary>A product Windows Installer knows about. <see cref="Context"/> is Machine or User.</summary>
public sealed record MsiProduct(string Code, string Context, string Sid, string Name, string? LocalPackage);

public sealed record RunningInstaller(int Id, string Name, DateTimeOffset? Started);

/// <summary>Something wrong that a repair can fix, with exactly the registry keys that fixing it removes.</summary>
public sealed record RepairFinding(string Id, RepairKind Kind, string Title, string Detail, string Evidence, RepairLevel Level, IReadOnlyList<string> Keys, int? ProcessId = null)
{
    public bool NeedsAdmin => Keys.Any(InstallRepair.IsMachine);
    public bool CanFix => Keys.Count > 0 || ProcessId is not null;
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
}

public interface IKeyEditor
{
    KeyBackup? Delete(string path);
    void Restore(KeyBackup backup);
}

public static partial class InstallRepair
{
    public const int Unknown = -1;

    public static bool IsMachine(string path) => path.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase);

    public const string InstallLockKey = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\InProgress";
    private static readonly string[] UpdateTypes = ["Update", "Hotfix", "Security Update", "Update Rollup", "Service Pack"];

    public static RepairScan Scan(IInstallSystem system)
    {
        var entries = system.Entries();
        var products = system.Products();
        var findings = new List<RepairFinding>();
        findings.AddRange(BrokenEntries(system, entries));
        findings.AddRange(BrokenRecords(system, products, entries));
        findings.AddRange(StuckInstalls(system));
        return new(entries, products, findings);
    }

    // ---- Programs that Windows still lists but that cannot be removed or reinstalled ----

    private static IEnumerable<RepairFinding> BrokenEntries(IInstallSystem system, IReadOnlyList<UninstallEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || entry.SystemComponent || entry.IsUpdate) continue;
            var code = ProductCode(entry);
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
            if (string.IsNullOrWhiteSpace(command))
            {
                if (entry.NoRemove) continue;
                reason = "It has no uninstaller.";
            }
            else
            {
                var found = Candidates(command).ToList();
                if (found.Count == 0 || found.Any(x => Exists(system, x) || IsUri(x))) continue;
                // A program on a drive that is not plugged in is not broken, only out of reach.
                if (found.Any(x => !system.DriveAvailable(system.Expand(x)))) continue;
                reason = "Its uninstaller is missing.";
            }

            var alive = StillThere(system, entry);
            // Only an entry that says where the program lives, with every one of those places gone, is called safe to remove.
            var named = entry.InstallLocation.Trim().Length > 0 || entry.DisplayIcon.Trim().Length > 0;
            var cached = CacheMarker().IsMatch(command ?? "");
            var (detail, level) =
                alive ? ($"{reason} The program's files are still on this PC, so check that you no longer need it before removing the entry.", RepairLevel.Review)
                : cached ? ($"{reason} Windows keeps it in a cache that has been cleared. Running the program's installer again restores it; remove the entry only if you do not want the program.", RepairLevel.Review)
                : !named ? ($"{reason} It does not say where the program lives, so TidyWindow cannot tell whether it is still installed.", RepairLevel.Review)
                : ($"{reason} The program's files are gone too, so this entry only blocks reinstalling.", RepairLevel.Recommended);
            yield return new($"entry:{entry.Key}", RepairKind.BrokenEntry, entry.Name, detail, entry.Key, level, [entry.Key]);
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
        if (ExecutablePrefix().Match(text) is { Success: true } match) { yield return match.Groups[1].Value; yield break; }
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
            if (string.IsNullOrWhiteSpace(product.LocalPackage) || system.FileExists(product.LocalPackage)) continue;
            var arp = entries.Where(x => string.Equals(x.KeyName, product.Code, StringComparison.OrdinalIgnoreCase)).ToList();
            var alive = arp.Any(x => StillThere(system, x));
            var name = string.IsNullOrWhiteSpace(product.Name) ? arp.FirstOrDefault()?.Name ?? product.Code : product.Name;
            var keys = RecordKeys(product).Concat(arp.Select(x => x.Key)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            yield return new($"record:{product.Code}", RepairKind.InstallerRecord, name,
                alive ? "Windows Installer lost the saved copy it needs to repair or uninstall this program, and the program is still on this PC. Remove the record only if you want to reinstall it."
                      : "Windows Installer lost the saved copy it needs to repair or uninstall this program, and the program is gone. Its leftover record blocks installing it again.",
                product.Code, alive ? RepairLevel.Review : RepairLevel.Recommended, keys);
        }
    }

    /// <summary>The registry keys Windows Installer keeps for one product.</summary>
    public static IReadOnlyList<string> RecordKeys(MsiProduct product)
    {
        var packed = Pack(product.Code);
        var data = $@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\{(product.Context == "Machine" ? "S-1-5-18" : product.Sid)}\Products\{packed}";
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

    private static string Reverse(string text) { var chars = text.ToCharArray(); Array.Reverse(chars); return new string(chars); }

    // ---- An interrupted install that is still in the way ----

    private static IEnumerable<RepairFinding> StuckInstalls(IInstallSystem system)
    {
        var installers = system.Installers();
        var lockExists = system.InstallLockExists();
        if (lockExists && !system.InstallerBusy() && installers.Count == 0)
            yield return new("lock", RepairKind.InstallLock, "Windows thinks an install is still running",
                "An install was interrupted and left its marker behind. Every new install fails with “another installation is in progress” until the marker is removed.",
                InstallLockKey, RepairLevel.Recommended, [InstallLockKey]);

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
    [GeneratedRegex(@"msiexec", RegexOptions.IgnoreCase)]
    private static partial Regex MsiExec();
    [GeneratedRegex(@"/[xXiI]\s*(\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\})")]
    private static partial Regex MsiCode();
    [GeneratedRegex(@"^(.+?\.(?:exe|bat|cmd|com|msi|cpl|msc|lnk|vbs|ps1))(?=\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ExecutablePrefix();
}

public sealed record RepairOutcome(int Fixed, IReadOnlyList<string> Problems, RepairBatch? Batch);

/// <summary>Applies repairs. Every removed key is copied first and kept, so the whole repair can be undone.</summary>
public sealed class RepairService(IInstallSystem system, IKeyEditor user, IKeyEditor machine, Func<CancellationToken, Task> unlock, Action relock, RepairHistory history)
{
    public async Task<RepairOutcome> FixAsync(IReadOnlyList<RepairFinding> findings, string title, CancellationToken cancellationToken = default)
    {
        if (findings.Any(x => x.NeedsAdmin)) await unlock(cancellationToken).ConfigureAwait(false);
        var backups = new List<KeyBackup>();
        var problems = new List<string>();
        var done = 0;
        try
        {
            foreach (var finding in findings)
            {
                var ok = true;
                foreach (var key in finding.Keys)
                {
                    try { if ((InstallRepair.IsMachine(key) ? machine : user).Delete(key) is { } copy) backups.Add(copy); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
                    { ok = false; problems.Add($"{finding.Title}: {ex.Message}"); }
                }
                if (finding.ProcessId is { } id && !system.EndProcess(id)) { ok = false; problems.Add($"{finding.Title}: Windows would not let TidyWindow end it."); }
                if (ok) done++;
            }
        }
        finally { relock(); }
        // A repair that changed nothing leaves nothing to undo.
        var batch = backups.Count > 0 ? history.Save(title, backups) : null;
        return new(done, problems, batch);
    }

    public async Task<int> UndoAsync(RepairBatch batch, CancellationToken cancellationToken = default)
    {
        if (batch.Keys.Any(x => InstallRepair.IsMachine(x.Path))) await unlock(cancellationToken).ConfigureAwait(false);
        var restored = 0;
        try
        {
            foreach (var key in batch.Keys.Reverse())
            {
                (InstallRepair.IsMachine(key.Path) ? machine : user).Restore(key);
                restored++;
            }
        }
        finally { relock(); }
        history.Remove(batch.Id);
        return restored;
    }
}
