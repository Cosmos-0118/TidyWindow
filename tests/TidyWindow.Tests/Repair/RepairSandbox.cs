using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Tests;

/// <summary>
/// A throwaway registry tree under HKCU that stands in for HKLM, HKCU and HKU, so the real repair code runs with real permissions
/// against fake broken installs and nothing on the PC is touched. Paths are written the way the app writes them.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RepairSandbox : IDisposable
{
    public const string Sid = "S-1-5-21-1000-2000-3000-1001";
    private readonly string name = @"SOFTWARE\TidyWindowTests\repair-" + Guid.NewGuid().ToString("N")[..10];
    public RegistryKey Root { get; }
    public RegistryRoots Roots => RegistryRoots.Under(Root);

    public RepairSandbox()
    {
        Root = Registry.CurrentUser.CreateSubKey(name, true);
        foreach (var hive in new[] { "HKLM", "HKCU", "HKU" }) Root.CreateSubKey(hive, true).Dispose();
    }

    public RegistryRepair Elevated() => RegistryRepair.Elevated(Sid, Roots);
    public RegistryRepair User() => RegistryRepair.UserFor(Roots, Sid);

    private static string Map(string path) => path.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase) ? @"HKU\" + Sid + path[4..] : path;

    public RegistryKey Open(string path, bool write = false) => Root.OpenSubKey(Map(path), write) ?? throw new InvalidOperationException(path);
    public bool Has(string path) { try { using var key = Root.OpenSubKey(Map(path)); return key is not null; } catch (System.Security.SecurityException) { return true; } }

    public void Put(string path, params (string Name, object Value)[] values)
    {
        using var key = Root.CreateSubKey(Map(path), true);
        foreach (var (valueName, value) in values) key.SetValue(valueName, value);
    }

    public bool HasValue(string path, string valueName) { using var key = Root.OpenSubKey(Map(path)); return key?.GetValue(valueName) is not null; }

    /// <summary>Makes this account unable to write to, add under or remove the key, as the permissions on a locked down installer key do. The account still owns it.</summary>
    public void Lock(string path, RegistryRights denied = RegistryRights.SetValue | RegistryRights.CreateSubKey | RegistryRights.Delete)
    {
        using var key = Root.OpenSubKey(Map(path), RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ReadPermissions | RegistryRights.ChangePermissions)!;
        var security = key.GetAccessControl();
        security.AddAccessRule(new RegistryAccessRule(WindowsIdentity.GetCurrent().User!, denied, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
        key.SetAccessControl(security);
    }

    public string Sddl(string path)
    {
        using var key = Root.OpenSubKey(Map(path), RegistryKeyPermissionCheck.ReadSubTree, RegistryRights.ReadPermissions)!;
        return key.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
    }

    /// <summary>Every key, value name and value with its type and bytes, and the permissions of every key, as sorted lines. Two equal dumps are the same registry.</summary>
    public List<string> Dump()
    {
        var lines = new List<string>();
        Walk(Root, "", lines);
        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static void Walk(RegistryKey key, string path, List<string> lines)
    {
        // The auto-inherited marker only records that Windows last set the permissions itself; the rules are what matter.
        lines.Add($"K {path} {key.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access).Replace("D:AI", "D:").Replace("D:PAI", "D:P")}");
        foreach (var value in RegistryNative.ReadValues(key)) lines.Add($"V {path} [{value.Name}] {value.RawType} {Convert.ToHexString(value.Bytes ?? [])}");
        foreach (var child in key.GetSubKeyNames())
        {
            try
            {
                using var sub = key.OpenSubKey(child, RegistryKeyPermissionCheck.ReadSubTree, RegistryRights.ReadKey | RegistryRights.ReadPermissions)!;
                Walk(sub, path + "\\" + child, lines);
            }
            catch (System.Security.SecurityException) { lines.Add("K " + path + "\\" + child + " locked"); }
        }
    }

    /// <summary>Fails with the first lines that differ, which a long list of keys needs to be readable.</summary>
    public static void AssertSame(List<string> expected, List<string> actual)
    {
        var missing = expected.Except(actual).Take(4).ToList();
        var extra = actual.Except(expected).Take(4).ToList();
        if (missing.Count + extra.Count > 0) Xunit.Assert.Fail("Missing:" + Environment.NewLine + string.Join(Environment.NewLine, missing) + Environment.NewLine + "Extra:" + Environment.NewLine + string.Join(Environment.NewLine, extra));
    }

    /// <summary>Everywhere the text turns up in a key name, value name or string value.</summary>
    public List<string> Mentions(params string[] needles)
    {
        var found = new List<string>();
        Search(Root, "", needles, found);
        return found;
    }

    private static void Search(RegistryKey key, string path, string[] needles, List<string> found)
    {
        bool Hit(string text) => needles.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));
        if (Hit(path[(path.LastIndexOf('\\') + 1)..])) found.Add(path);
        foreach (var valueName in key.GetValueNames())
        {
            var data = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            var text = data switch { string s => s, string[] many => string.Join("|", many), _ => "" };
            if (Hit(valueName) || Hit(text)) found.Add($"{path} [{valueName}]");
        }
        foreach (var child in key.GetSubKeyNames())
        {
            using var sub = key.OpenSubKey(child)!;
            Search(sub, path + "\\" + child, needles, found);
        }
    }

    public void Dispose()
    {
        try
        {
            UnlockTree(Registry.CurrentUser, name);
            Root.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(name, false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException) { }
        try { using var parent = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\TidyWindowTests", true); if (parent is { SubKeyCount: 0 }) { parent.Close(); Registry.CurrentUser.DeleteSubKey(@"SOFTWARE\TidyWindowTests", false); } }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException) { }
    }

    private static void UnlockTree(RegistryKey parent, string sub)
    {
        var me = WindowsIdentity.GetCurrent().User!;
        using (var key = parent.OpenSubKey(sub, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ReadPermissions | RegistryRights.ChangePermissions))
        {
            if (key is null) return;
            var security = new RegistrySecurity();
            security.SetSecurityDescriptorSddlForm($"D:P(A;CI;KA;;;{me.Value})", AccessControlSections.Access);
            key.SetAccessControl(security);
        }
        using var read = parent.OpenSubKey(sub);
        foreach (var child in read?.GetSubKeyNames() ?? []) UnlockTree(parent, sub + "\\" + child);
    }
}
