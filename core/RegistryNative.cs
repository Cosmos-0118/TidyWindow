using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace TidyWindow.Next.Core;

/// <summary>
/// What the managed registry classes cannot do: read and write a value as its raw type and bytes (so REG_NONE, REG_EXPAND_SZ and odd Windows Installer
/// values survive a backup exactly), and use the backup and restore privileges to open a key whose permissions would otherwise say no.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class RegistryNative
{
    private const int Success = 0, NoMoreItems = 259, MoreData = 234;
    private const uint BackupRestoreOption = 0x00000004;

    public static IReadOnlyList<ValueBackup> ReadValues(RegistryKey key)
    {
        var handle = key.Handle;
        var check = RegQueryInfoKey(handle, null, IntPtr.Zero, IntPtr.Zero, out _, out _, out _, out var valueCount, out var maxName, out var maxData, IntPtr.Zero, IntPtr.Zero);
        if (check != Success) throw Error(check, key.Name);
        var values = new List<ValueBackup>((int)valueCount);
        for (uint index = 0; ; index++)
        {
            var name = new StringBuilder((int)maxName + 2);
            var nameLength = (uint)name.Capacity;
            var data = new byte[maxData + 2];
            var dataLength = (uint)data.Length;
            var result = RegEnumValue(handle, index, name, ref nameLength, IntPtr.Zero, out var type, data, ref dataLength);
            if (result == MoreData)
            {
                // The value grew between the size query and the read; ask again with room for it.
                name = new StringBuilder(32768); nameLength = 32768; data = new byte[dataLength + 2]; dataLength = (uint)data.Length;
                result = RegEnumValue(handle, index, name, ref nameLength, IntPtr.Zero, out type, data, ref dataLength);
            }
            if (result == NoMoreItems) break;
            if (result != Success) throw Error(result, key.Name);
            var bytes = new byte[dataLength];
            Array.Copy(data, bytes, (int)dataLength);
            values.Add(new(name.ToString(), KindName((int)type), Bytes: bytes, RawType: (int)type));
        }
        return values;
    }

    public static void WriteValue(RegistryKey key, ValueBackup value)
    {
        if (value.RawType is { } type)
        {
            var bytes = value.Bytes ?? [];
            var result = RegSetValueEx(key.Handle, value.Name, 0, (uint)type, bytes, (uint)bytes.Length);
            if (result != Success) throw Error(result, key.Name);
            return;
        }
        // Copies saved before values were kept raw store a typed value.
        var kind = Enum.TryParse<RegistryValueKind>(value.Kind, out var parsed) ? parsed : RegistryValueKind.String;
        switch (kind)
        {
            case RegistryValueKind.String or RegistryValueKind.ExpandString: key.SetValue(value.Name, value.Text ?? "", kind); break;
            case RegistryValueKind.DWord: key.SetValue(value.Name, unchecked((int)(value.Number ?? 0)), kind); break;
            case RegistryValueKind.QWord: key.SetValue(value.Name, value.Number ?? 0, kind); break;
            case RegistryValueKind.MultiString: key.SetValue(value.Name, value.Lines ?? [], kind); break;
            default: key.SetValue(value.Name, value.Bytes ?? [], RegistryValueKind.Binary); break;
        }
    }

    private static string KindName(int type) => type switch
    {
        1 => "String", 2 => "ExpandString", 3 => "Binary", 4 => "DWord", 7 => "MultiString", 11 => "QWord", 0 => "None", _ => "Unknown"
    };

    /// <summary>Opens a key with the backup and restore privileges, which skip the permission check. Needs those privileges to be on; returns null otherwise.</summary>
    public static RegistryKey? OpenPrivileged(RegistryKey parent, string subKey, bool write)
    {
        var access = write ? 0xF003F : 0x20019;
        var result = RegOpenKeyEx(parent.Handle, subKey, BackupRestoreOption, access, out var handle);
        if (result != Success) { handle.Dispose(); return null; }
        return RegistryKey.FromHandle(handle);
    }

    /// <summary>
    /// The owner, group and permissions of a key as SDDL, read straight from the handle. The managed call fails on a handle opened with the backup
    /// privilege, which is the only way to open a key that only SYSTEM may read.
    /// </summary>
    public static string GetSddl(RegistryKey key)
    {
        uint length = 512;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = new byte[length];
            var result = RegGetKeySecurity(key.Handle, 0x7, buffer, ref length);
            if (result == MoreData || result == 122) continue;
            if (result != Success) throw Error(result, key.Name);
            return new System.Security.AccessControl.RawSecurityDescriptor(buffer, 0).GetSddlForm(System.Security.AccessControl.AccessControlSections.Owner | System.Security.AccessControl.AccessControlSections.Group | System.Security.AccessControl.AccessControlSections.Access);
        }
        throw new IOException($"Windows could not read the permissions of '{key.Name}'.");
    }

    /// <summary>Creates a key with the backup and restore privileges, which skip the permission check on the parent.</summary>
    public static RegistryKey? CreatePrivileged(RegistryKey parent, string subKey)
    {
        var result = RegCreateKeyEx(parent.Handle, subKey, 0, null, BackupRestoreOption, 0xF003F, IntPtr.Zero, out var handle, out _);
        if (result != Success) { handle.Dispose(); return null; }
        return RegistryKey.FromHandle(handle);
    }

    /// <summary>Turns on the privileges an elevated process has but keeps switched off. True when all of them are on.</summary>
    public static bool EnablePrivileges()
    {
        var all = true;
        foreach (var name in new[] { "SeBackupPrivilege", "SeRestorePrivilege", "SeTakeOwnershipPrivilege" }) all &= Enable(name);
        return all;
    }

    private static bool Enable(string privilege)
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x20 | 0x8, out var token)) return false;
        try
        {
            if (!LookupPrivilegeValue(null, privilege, out var luid)) return false;
            var state = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
            return AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero) && Marshal.GetLastWin32Error() == 0;
        }
        finally { CloseHandle(token); }
    }

    private static Exception Error(int code, string path) =>
        code is 5 or 1314 ? new UnauthorizedAccessException($"Access to the registry key '{path}' is denied.") : new IOException($"Windows could not read '{path}' (error {code}).");

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegQueryInfoKey(SafeRegistryHandle key, StringBuilder? className, IntPtr classLength, IntPtr reserved, out uint subKeys, out uint maxSubKey, out uint maxClass, out uint values, out uint maxValueName, out uint maxValueData, IntPtr security, IntPtr lastWrite);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegEnumValue(SafeRegistryHandle key, uint index, StringBuilder name, ref uint nameLength, IntPtr reserved, out uint type, byte[] data, ref uint dataLength);

    [DllImport("advapi32.dll")]
    private static extern int RegGetKeySecurity(SafeRegistryHandle key, uint information, byte[] descriptor, ref uint length);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegSetValueEx(SafeRegistryHandle key, string name, int reserved, uint type, byte[] data, uint length);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegOpenKeyEx(SafeRegistryHandle key, string subKey, uint options, int access, out SafeRegistryHandle result);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegCreateKeyEx(SafeRegistryHandle key, string subKey, int reserved, string? className, uint options, int access, IntPtr security, out SafeRegistryHandle result, out uint disposition);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state, uint length, IntPtr previous, IntPtr returned);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
