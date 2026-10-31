using System.Runtime.Versioning;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TidyWindow.Next.Core;

/// <summary>
/// One registry value kept exactly as it was. New copies hold the raw type and bytes, so every kind of value comes back identical;
/// copies saved by older versions hold the typed fields and are still restored.
/// </summary>
public sealed record ValueBackup(string Name, string Kind, string? Text = null, long? Number = null, byte[]? Bytes = null, string[]? Lines = null, int? RawType = null);

/// <summary>
/// A registry key with all its values, its subkeys and its permissions (as SDDL). This is what an undo restores.
/// <see cref="ValuesOnly"/> means only some values were removed or changed in a key other programs also use, so only those values are put back;
/// <see cref="Absent"/> names values that did not exist before a change, which an undo removes again.
/// </summary>
public sealed record KeyBackup(string Path, IReadOnlyList<ValueBackup> Values, IReadOnlyList<KeyBackup> Children, string? Sddl = null, bool ValuesOnly = false, IReadOnlyList<string>? Absent = null);

/// <summary>
/// Removes and restores the few registry keys that leftovers of a failed install live in. It only ever touches keys that match an allow-list,
/// whoever asks, hands back a full copy (values as raw bytes, permissions included) of what it removed, and never removes what it could not copy.
/// A key whose permissions say no is taken over first, and the copy puts the original permissions back.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryRepair : IKeyEditor
{
    private const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Wow64Uninstall = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Installer = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer";
    private const string Packed = @"[0-9A-F]{32}";
    private const string UserKey = @"(?:HKCU|HKU\\S-1-[0-9-]+)";

    private readonly IReadOnlyList<Regex> allowed;
    private readonly IReadOnlyList<(Regex Path, Regex Name)> values;
    private readonly RegistryRoots roots;
    private readonly string? userSid;
    private readonly bool privileged;

    /// <param name="allowed">Key paths that may be removed or restored.</param>
    /// <param name="roots">Where the hives come from; the real ones unless a test supplies its own.</param>
    /// <param name="userSid">When set, HKCU means that user's hive, so a helper running as another administrator reaches the right one.</param>
    /// <param name="values">Single values that may be removed from a shared key, as key path and value name.</param>
    /// <param name="privileged">Use the backup and restore privileges when permissions say no. Only the elevated helper has them.</param>
    public RegistryRepair(IReadOnlyList<Regex> allowed, RegistryRoots? roots = null, string? userSid = null, IReadOnlyList<(Regex Path, Regex Name)>? values = null, bool privileged = false)
    {
        this.allowed = allowed;
        this.roots = roots ?? RegistryRoots.Real;
        this.userSid = userSid;
        this.values = values ?? [];
        this.privileged = privileged;
    }

    private static Regex Rule(string pattern) => new("^" + pattern + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static IEnumerable<Regex> UserRules(string hive) =>
    [
        Rule(hive + @"\\" + Regex.Escape(Uninstall) + @"\\[^\\]+"),
        Rule(hive + @"\\" + Regex.Escape(Wow64Uninstall) + @"\\[^\\]+"),
        Rule(hive + @"\\SOFTWARE\\Microsoft\\Installer\\(?:Products|Features|UpgradeCodes|Patches)\\" + Packed),
        Rule(hive + @"\\SOFTWARE\\Classes\\Installer\\(?:Products|Features|UpgradeCodes|Patches)\\" + Packed),
        Rule(hive + @"\\" + Regex.Escape(Installer) + @"\\UpgradeCodes\\" + Packed),
        Rule(hive + @"\\SOFTWARE\\Classes\\Installer\\Dependencies\\[^\\]+(?:\\Dependents\\[^\\]+)?")
    ];

    private static IEnumerable<Regex> MachineRules() =>
    [
        Rule(@"HKLM\\" + Regex.Escape(Uninstall) + @"\\[^\\]+"),
        Rule(@"HKLM\\" + Regex.Escape(Wow64Uninstall) + @"\\[^\\]+"),
        Rule(@"HKLM\\" + Regex.Escape(Installer) + @"\\UserData\\S-1-[0-9-]+\\(?:Products|Patches)\\" + Packed),
        Rule(@"HKLM\\" + Regex.Escape(Installer) + @"\\UserData\\S-1-[0-9-]+\\Components\\" + Packed),
        Rule(@"HKLM\\" + Regex.Escape(Installer) + @"\\Managed\\S-1-[0-9-]+\\Installer\\(?:Products|Features|UpgradeCodes)\\" + Packed),
        Rule(@"HKLM\\" + Regex.Escape(Installer) + @"\\(?:Products|Features|UpgradeCodes)\\" + Packed),
        Rule(@"HKLM\\SOFTWARE\\Classes\\Installer\\(?:Products|Features|UpgradeCodes|Patches)\\" + Packed),
        Rule(@"HKLM\\SOFTWARE\\Classes\\Installer\\Dependencies\\[^\\]+(?:\\Dependents\\[^\\]+)?"),
        Rule(@"HKLM\\" + Regex.Escape(Installer) + @"\\InProgress")
    ];

    private static readonly (Regex Path, Regex Name)[] SharedValues =
    [
        // A product's link in a key every product with that upgrade code or component shares: only that one value goes.
        (Rule(UserKey + @"\\SOFTWARE\\(?:Microsoft|Classes)\\Installer\\UpgradeCodes\\" + Packed), Rule(Packed)),
        (Rule(UserKey + @"\\" + Regex.Escape(Installer) + @"\\UpgradeCodes\\" + Packed), Rule(Packed)),
        (Rule(@"HKLM\\SOFTWARE\\Classes\\Installer\\UpgradeCodes\\" + Packed), Rule(Packed)),
        (Rule(@"HKLM\\" + Regex.Escape(Installer) + @"\\UpgradeCodes\\" + Packed), Rule(Packed)),
        (Rule(@"HKLM\\" + Regex.Escape(Installer) + @"\\Managed\\S-1-[0-9-]+\\Installer\\UpgradeCodes\\" + Packed), Rule(Packed)),
        (Rule(@"HKLM\\" + Regex.Escape(Installer) + @"\\UserData\\S-1-[0-9-]+\\Components\\" + Packed), Rule(Packed)),
        // The command a list entry runs to uninstall: pointed at an uninstaller that is still there when the one it names is gone.
        (Rule(@"HKLM\\(?:" + Regex.Escape(Uninstall) + "|" + Regex.Escape(Wow64Uninstall) + @")\\[^\\]+"), Rule("UninstallString|QuietUninstallString")),
        (Rule(UserKey + @"\\(?:" + Regex.Escape(Uninstall) + "|" + Regex.Escape(Wow64Uninstall) + @")\\[^\\]+"), Rule("UninstallString|QuietUninstallString"))
    ];

    /// <summary>Windows-wide keys. Only the elevated helper uses these.</summary>
    public static RegistryRepair Machine { get; } = new([.. MachineRules()], values: [.. SharedValues.Where(x => x.Path.ToString().Contains("HKLM"))]);

    /// <summary>Keys of the signed-in user, which need no administrator permission.</summary>
    public static RegistryRepair User { get; } = new([.. UserRules("HKCU")], values: [.. SharedValues.Where(x => x.Path.ToString().Contains("HKCU"))]);

    /// <summary>The rules of <see cref="User"/> on hives of the caller's choosing, so the same code can run against a throwaway registry tree.</summary>
    public static RegistryRepair UserFor(RegistryRoots roots, string? userSid = null) =>
        new([.. UserRules("HKCU")], roots, userSid, [.. SharedValues.Where(x => x.Path.ToString().Contains("HKCU"))]);

    /// <summary>What the elevated helper may change: the Windows-wide keys and the signed-in user's own, reached through that user's hive.</summary>
    public static RegistryRepair Elevated(string userSid, RegistryRoots? roots = null) =>
        new([.. MachineRules(), .. UserRules(UserKey)], roots, userSid, SharedValues, privileged: true);

    public bool IsAllowed(string path) => !path.Contains("..") && allowed.Any(x => x.IsMatch(path));
    public bool IsValueAllowed(string path, string name) => !path.Contains("..") && values.Any(x => x.Path.IsMatch(path) && x.Name.IsMatch(name));

    // ---- Reading, for planning and for checking afterwards ----

    public bool Exists(string path)
    {
        var (root, sub) = Open(path);
        using var parent = root;
        return Present(parent, sub);
    }

    public IReadOnlyList<string> SubKeys(string path)
    {
        var (root, sub) = Open(path);
        using var parent = root;
        using var key = OpenKey(parent, sub, false);
        return key?.GetSubKeyNames() ?? [];
    }

    public IReadOnlyList<string> ValueNames(string path)
    {
        var (root, sub) = Open(path);
        using var parent = root;
        using var key = OpenKey(parent, sub, false);
        return key?.GetValueNames() ?? [];
    }

    public string? ReadText(string path, string name)
    {
        var (root, sub) = Open(path);
        using var parent = root;
        using var key = OpenKey(parent, sub, false);
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public KeyBackup? Snapshot(string path)
    {
        var (root, sub) = Open(path);
        using var parent = root;
        using var key = OpenKey(parent, sub, false);
        return key is null ? null : Capture(key, path);
    }

    public FootprintPlan Footprint(FootprintRequest request) => new InstallerFootprint(this, userSid ?? CurrentSid()).Plan(request);

    public static string CurrentSid() => WindowsIdentity.GetCurrent().User?.Value ?? "";

    // ---- Removing ----

    /// <summary>Copies the key and removes it. A key that is already gone counts as done, with no copy. A key that cannot be copied in full is never removed.</summary>
    public KeyBackup? Delete(string path)
    {
        if (!IsAllowed(path)) throw new InvalidOperationException("TidyWindow does not change that part of the registry.");
        var (root, sub) = Open(path);
        using var parent = root;
        KeyBackup copy;
        using (var key = OpenKey(parent, sub, false))
        {
            if (key is null) return null;
            copy = Capture(key, path);
        }
        try
        {
            // Windows answers a key whose permissions forbid the removal either with an error or by quietly leaving it. Either way the
            // permissions are replaced for the removal (the copy holds the originals for an undo) and it is tried once more.
            for (var attempt = 0; ; attempt++)
            {
                try { parent.DeleteSubKeyTree(sub, throwOnMissingSubKey: false); }
                catch (Exception ex) when (attempt == 0 && ex is UnauthorizedAccessException or SecurityException) { TakeControl(parent, sub); continue; }
                if (!Present(parent, sub)) break;
                if (attempt > 0) throw new IOException($"Windows would not remove '{Describe(path)}'.");
                TakeControl(parent, sub);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            // Part of the tree may be gone already; put the rest of it back so nothing is left half removed.
            try { Write(parent, sub, copy); } catch (Exception back) when (back is UnauthorizedAccessException or SecurityException or IOException) { }
            if (ex is SecurityException) throw new UnauthorizedAccessException(ex.Message, ex);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
            throw;
        }
        return copy;
    }

    /// <summary>Removes one value from a key other programs also use. The key goes too when that leaves it empty, as Windows Installer does itself.</summary>
    public KeyBackup? DeleteValue(string path, string name)
    {
        if (!IsValueAllowed(path, name)) throw new InvalidOperationException("TidyWindow does not change that part of the registry.");
        var (root, sub) = Open(path);
        using var parent = root;
        ValueBackup copy;
        string? sddl;
        using (var read = OpenKey(parent, sub, false))
        {
            if (read is null) return null;
            if (RegistryNative.ReadValues(read).FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } found) return null;
            copy = found;
            sddl = SecurityOf(read);
        }
        try
        {
            RemoveValue(parent, sub, copy.Name);
            using var left = OpenKey(parent, sub, false);
            if (left is not null && left.ValueCount == 0 && left.SubKeyCount == 0) { left.Close(); parent.DeleteSubKey(sub, throwOnMissingSubKey: false); }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            throw new UnauthorizedAccessException($"Access to the registry key '{Describe(path)}' is denied.", ex);
        }
        return new(path, [copy], [], sddl, ValuesOnly: true);
    }

    /// <summary>Changes one text value of a list entry, keeping its type, and hands back the old value (or the fact that there was none) so it can be put back.</summary>
    public KeyBackup? SetText(string path, string name, string value)
    {
        if (!IsValueAllowed(path, name)) throw new InvalidOperationException("TidyWindow does not change that part of the registry.");
        var (root, sub) = Open(path);
        using var parent = root;
        ValueBackup? old;
        string? sddl;
        using (var read = OpenKey(parent, sub, false))
        {
            if (read is null) return null;
            old = RegistryNative.ReadValues(read).FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            sddl = SecurityOf(read);
        }
        var kind = old?.RawType == 2 ? RegistryValueKind.ExpandString : RegistryValueKind.String;
        try
        {
            try { using var key = OpenKey(parent, sub, true) ?? throw new UnauthorizedAccessException(); key.SetValue(name, value, kind); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException) { TakeControl(parent, sub, recurse: false); using var key = OpenKey(parent, sub, true) ?? throw new UnauthorizedAccessException(); key.SetValue(name, value, kind); }
            // The permissions the entry had are put back, so a change made with borrowed rights leaves no trace.
            if (sddl is not null) ApplySecurity(parent, sub, sddl, path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            throw new UnauthorizedAccessException($"Access to the registry key '{Describe(path)}' is denied.", ex);
        }
        return new(path, old is null ? [] : [old], [], sddl, ValuesOnly: true, Absent: old is null ? [name] : null);
    }

    private void RemoveValue(RegistryKey parent, string sub, string name)
    {
        try
        {
            using var key = OpenKey(parent, sub, true) ?? throw new UnauthorizedAccessException();
            key.DeleteValue(name, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            TakeControl(parent, sub, recurse: false);
            using var key = OpenKey(parent, sub, true) ?? throw new UnauthorizedAccessException();
            key.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    // ---- Restoring ----

    /// <summary>Writes a removed key back with its values, subkeys and permissions. Values that already exist are replaced.</summary>
    public void Restore(KeyBackup backup)
    {
        if (!IsAllowed(backup.Path)) throw new InvalidOperationException("TidyWindow does not change that part of the registry.");
        var (root, sub) = Open(backup.Path);
        using var parent = root;
        Write(parent, sub, backup);
    }

    private void Write(RegistryKey parent, string sub, KeyBackup backup)
    {
        var existed = Present(parent, sub);
        using (var key = CreateKey(parent, sub, backup.Path))
        {
            foreach (var value in backup.Values)
            {
                try { RegistryNative.WriteValue(key, value); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
                { throw new UnauthorizedAccessException($"Access to the registry key '{Describe(backup.Path)}' is denied.", ex); }
            }
        }
        foreach (var child in backup.Children)
            Write(parent, sub + "\\" + child.Path[(child.Path.LastIndexOf('\\') + 1)..], child);
        if (backup.Absent is { Count: > 0 } absent)
        {
            using var key = OpenKey(parent, sub, true);
            foreach (var name in absent) key?.DeleteValue(name, throwOnMissingValue: false);
        }
        // Permissions go on last: the originals may forbid the writes that were just made.
        if (backup.Sddl is not null && (!backup.ValuesOnly || !existed)) ApplySecurity(parent, sub, backup.Sddl, backup.Path);
    }

    private RegistryKey CreateKey(RegistryKey parent, string sub, string path)
    {
        try { return parent.CreateSubKey(sub, true) ?? throw new IOException($"Windows would not create '{Describe(path)}'."); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            if (Present(parent, sub))
            {
                // The key is there, with permissions that keep this account out. Take it over; the copy's permissions go back after the values do.
                TakeControl(parent, sub, recurse: false);
                try { return parent.CreateSubKey(sub, true)!; } catch (Exception again) when (again is UnauthorizedAccessException or SecurityException) { }
            }
            if (privileged && RegistryNative.CreatePrivileged(parent, sub) is { } made) return made;
            throw new UnauthorizedAccessException($"Access to the registry key '{Describe(path)}' is denied.", ex);
        }
    }

    // ---- Permissions ----

    private static string? SecurityOf(RegistryKey key)
    {
        const AccessControlSections Sections = AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access;
        // The managed read keeps the auto-inherited marker, which putting the list back relies on; it fails on a key opened with the backup privilege, which the native read handles.
        try { return key.GetAccessControl(Sections).GetSecurityDescriptorSddlForm(Sections); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException) { return RegistryNative.GetSddl(key); }
    }

    private void ApplySecurity(RegistryKey parent, string sub, string sddl, string path)
    {
        // The owner first, while this account can still change it; handing it back to another account needs the restore privilege, so that part may fail quietly.
        try
        {
            using var key = parent.OpenSubKey(sub, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.TakeOwnership) ?? RegistryNative.OpenPrivileged(parent, sub, true);
            if (key is not null)
            {
                var owner = new RegistrySecurity();
                owner.SetSecurityDescriptorSddlForm(sddl, AccessControlSections.Owner | AccessControlSections.Group);
                key.SetAccessControl(owner);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or InvalidOperationException or IOException) { }
        try
        {
            using var key = OpenForPermissions(parent, sub) ?? throw new UnauthorizedAccessException();
            var dacl = new RegistrySecurity();
            dacl.SetSecurityDescriptorSddlForm(sddl, AccessControlSections.Access);
            key.SetAccessControl(dacl);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or InvalidOperationException)
        {
            throw new UnauthorizedAccessException($"The permissions of '{Describe(path)}' could not be put back.", ex);
        }
    }

    private RegistryKey? OpenForPermissions(RegistryKey parent, string sub)
    {
        try { return parent.OpenSubKey(sub, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ReadPermissions | RegistryRights.ChangePermissions); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException) { return privileged ? TakeOwnership(parent, sub) : null; }
    }

    /// <summary>
    /// The classic way in to a key that only SYSTEM may change: the take-ownership privilege lets this account become the owner, and an owner may change the permissions.
    /// The key is backed up with its original owner first, so an undo gives it back.
    /// </summary>
    private static RegistryKey? TakeOwnership(RegistryKey parent, string sub)
    {
        try
        {
            using (var key = parent.OpenSubKey(sub, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.TakeOwnership))
            {
                if (key is null) return null;
                var security = new RegistrySecurity();
                security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
                key.SetAccessControl(security);
            }
            return parent.OpenSubKey(sub, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ReadPermissions | RegistryRights.ChangePermissions);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException) { return RegistryNative.OpenPrivileged(parent, sub, true); }
    }

    /// <summary>Gives this account full control of a key and everything under it, so it can be changed or removed. Needs to own the key, or the backup privileges.</summary>
    private void TakeControl(RegistryKey parent, string sub, bool recurse = true)
    {
        var me = WindowsIdentity.GetCurrent().User?.Value ?? "";
        using (var key = OpenForPermissions(parent, sub))
        {
            if (key is null) throw new UnauthorizedAccessException($"Access to the registry key '{sub}' is denied, and this account does not own it.");
            var security = new RegistrySecurity();
            security.SetSecurityDescriptorSddlForm($"D:P(A;CI;KA;;;{me})(A;CI;KA;;;BA)(A;CI;KA;;;SY)", AccessControlSections.Access);
            key.SetAccessControl(security);
        }
        if (!recurse) return;
        using var opened = OpenKey(parent, sub, false);
        foreach (var child in opened?.GetSubKeyNames() ?? []) TakeControl(parent, sub + "\\" + child);
    }

    // ---- Finding the key ----

    private (RegistryKey Root, string Sub) Open(string path)
    {
        var split = path.IndexOf('\\');
        if (split < 0) throw new ArgumentException("A registry path needs a key under the hive.", nameof(path));
        var hive = path[..split].ToUpperInvariant();
        var rest = path[(split + 1)..];
        // The Windows-wide WOW6432Node keys are named in full, so one view reaches every key without the redirection getting in the way.
        if (hive == "HKCU" && userSid is not null) { hive = "HKU"; rest = userSid + "\\" + rest; }
        if (hive is not ("HKLM" or "HKCU" or "HKU")) throw new ArgumentException("Only HKLM, HKCU and HKU are supported.", nameof(path));
        return (roots.Open(hive) ?? throw new IOException("That part of the registry is not available."), rest);
    }

    private static string Describe(string path) => path.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase) ? "HKEY_CURRENT_USER" + path[4..] : path.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase) ? "HKEY_LOCAL_MACHINE" + path[4..] : path;

    private bool Present(RegistryKey parent, string sub)
    {
        try { using var key = parent.OpenSubKey(sub); return key is not null; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException) { return true; }
    }

    private RegistryKey? OpenKey(RegistryKey parent, string sub, bool write)
    {
        try { return write ? parent.OpenSubKey(sub, true) : parent.OpenSubKey(sub); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            if (privileged && RegistryNative.OpenPrivileged(parent, sub, write) is { } key) return key;
            throw new UnauthorizedAccessException($"Access to the registry key '{sub}' is denied.", ex);
        }
    }

    private KeyBackup Capture(RegistryKey key, string path)
    {
        var children = new List<KeyBackup>();
        foreach (var child in key.GetSubKeyNames())
        {
            using var sub = OpenKey(key, child, false);
            if (sub is not null) children.Add(Capture(sub, path + "\\" + child));
        }
        string? sddl;
        try { sddl = SecurityOf(key); }
        catch (UnauthorizedAccessException ex) { throw new UnauthorizedAccessException($"The permissions of '{Describe(path)}' cannot be read ({ex.Message}).", ex); }
        return new(path, RegistryNative.ReadValues(key), children, sddl);
    }
}

/// <summary>Where the registry hives come from. The real ones, or a test's own throwaway tree.</summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryRoots(Func<string, RegistryKey?> open)
{
    public RegistryKey? Open(string hive) => open(hive.ToUpperInvariant());

    public static RegistryRoots Real { get; } = new(hive => hive switch
    {
        "HKLM" => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
        "HKCU" => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64),
        "HKU" => RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64),
        _ => null
    });

    /// <summary>Every hive is a key under <paramref name="sandbox"/>, so real permissions apply but nothing on the PC is touched.</summary>
    public static RegistryRoots Under(RegistryKey sandbox) => new(hive => sandbox.CreateSubKey(hive, true));
}

/// <summary>Everything one repair changed, kept so the whole repair can be undone in one go: the registry keys it removed or changed, and the files it put back (a setup program in the package cache).</summary>
public sealed record RepairBatch(string Id, DateTimeOffset Created, string Title, IReadOnlyList<KeyBackup> Keys, IReadOnlyList<string>? Files = null)
{
    public IReadOnlyList<string> PutBack => Files ?? [];
}

/// <summary>Remembers the last few repairs on disk, as plain JSON files.</summary>
public sealed class RepairHistory(string folder, int keep = 10)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public RepairBatch Save(string title, IReadOnlyList<KeyBackup> keys, DateTimeOffset? now = null, IReadOnlyList<string>? files = null)
    {
        Directory.CreateDirectory(folder);
        var created = now ?? DateTimeOffset.Now;
        var batch = new RepairBatch(created.ToString("yyyyMMdd-HHmmss-fff"), created, title, keys, files is { Count: > 0 } ? files : null);
        // Written to the side first, so a crash or a full disk never leaves a copy that cannot be read.
        var file = FileFor(batch.Id);
        var temporary = file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(batch, Json));
        File.Move(temporary, file, overwrite: true);
        foreach (var old in All().Skip(keep)) Remove(old.Id);
        return batch;
    }

    public IReadOnlyList<RepairBatch> All()
    {
        if (!Directory.Exists(folder)) return [];
        var batches = new List<RepairBatch>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            try { if (JsonSerializer.Deserialize<RepairBatch>(File.ReadAllText(file), Json) is { } batch) batches.Add(batch); }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        }
        return batches.OrderByDescending(x => x.Created).ToList();
    }

    public RepairBatch? Latest() => All().FirstOrDefault();

    public void Remove(string id)
    {
        try { File.Delete(FileFor(id)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string FileFor(string id) => Path.Combine(folder, id + ".json");
}
