using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace TidyWindow.Next.Core;

internal static class CleanupNative
{
    private const uint NoConfirmation = 1, NoProgress = 2, NoSound = 4;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct RecycleBinInfo { public uint Size; public long Bytes; public long Items; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string? root, ref RecycleBinInfo info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(IntPtr window, string? root, uint flags);

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
