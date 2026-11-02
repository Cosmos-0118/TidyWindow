using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace TidyWindow.Next.Core;

internal static class CleanupNative
{
    private const uint NoConfirmation = 1, NoProgress = 2, NoSound = 4;

    /// <summary>
    /// SHQUERYRBINFO: cbSize, then two 64-bit counts, laid out as the shell declares it (natural alignment, 24 bytes on 64-bit Windows).
    /// A packed layout made it 20 bytes, and the shell then either refused the call, hiding the Recycle Bin, or wrote past the end of it.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RecycleBinInfo { public uint Size; public long Bytes; public long Items; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string? root, ref RecycleBinInfo info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(IntPtr window, string? root, uint flags);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid folderId, uint flags, IntPtr token, out IntPtr path);

    /// <summary>Personal folders that users can move to another drive. The variable name is what catalog paths write between % signs.</summary>
    public static readonly IReadOnlyDictionary<string, (Guid Id, string Default)> KnownFolders = new Dictionary<string, (Guid, string)>(StringComparer.OrdinalIgnoreCase)
    {
        ["DOWNLOADS"] = (new Guid("374DE290-123F-4565-9164-39C4925E467B"), "Downloads"),
        ["DOCUMENTS"] = (new Guid("FDD39AD0-238F-46AF-ADB4-6C85480369C7"), "Documents"),
        ["DESKTOP"] = (new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641"), "Desktop"),
        ["PICTURES"] = (new Guid("33E28130-4E1E-4676-835A-98395C3BC3BB"), "Pictures"),
        ["MUSIC"] = (new Guid("4BD8D571-6D19-48D3-BE97-422220080E43"), "Music"),
        ["VIDEOS"] = (new Guid("18989B1D-99B5-455B-841C-AB7C74E4DDFC"), "Videos")
    };

    /// <summary>An environment variable, or where Windows currently keeps one of the personal folders (falling back to its usual place in the profile).</summary>
    public static string? Variable(string name)
    {
        if (!KnownFolders.TryGetValue(name, out var known)) return Environment.GetEnvironmentVariable(name);
        if (OperatingSystem.IsWindows())
        {
            IntPtr buffer = IntPtr.Zero;
            try
            {
                if (SHGetKnownFolderPath(known.Id, 0, IntPtr.Zero, out buffer) == 0 && Marshal.PtrToStringUni(buffer) is { Length: > 0 } path) return path;
            }
            finally { if (buffer != IntPtr.Zero) Marshal.FreeCoTaskMem(buffer); }
        }
        return Environment.GetEnvironmentVariable("USERPROFILE") is { Length: > 0 } profile ? Path.Combine(profile, known.Default) : null;
    }

    private const string VolumeCaches =@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches";

    /// <summary>The folders Windows lists for one Disk Cleanup handler, or null when the handler does not exist on this Windows.</summary>
    public static string[]? VolumeCacheFolders(string handler)
    {
        if (!OperatingSystem.IsWindows() || handler.Length == 0 || handler.AsSpan().IndexOfAny('\\', '/') >= 0) return null;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"{VolumeCaches}\{handler}");
            if (key is null) return null;
            return (key.GetValue("Folder") as string)?.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    public static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static IReadOnlySet<string> RunningProcesses()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses()) { names.Add(process.ProcessName); process.Dispose(); }
        return names;
    }

    public static (long Bytes, long Items) QueryRecycleBin()
    {
        if (!OperatingSystem.IsWindows()) return (0, 0);
        var info = new RecycleBinInfo { Size = (uint)Marshal.SizeOf<RecycleBinInfo>() };
        return SHQueryRecycleBinW(null, ref info) == 0 ? (info.Bytes, info.Items) : (0, 0);
    }

    /// <summary>Returns false when Windows refused; an already empty bin counts as success.</summary>
    public static bool EmptyRecycleBin()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var result = SHEmptyRecycleBinW(IntPtr.Zero, null, NoConfirmation | NoProgress | NoSound);
        return result == 0 || result == unchecked((int)0x80070012) /* nothing to empty */ || result == 1;
    }
}
