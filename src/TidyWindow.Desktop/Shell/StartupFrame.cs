using System.Runtime.InteropServices;
using Windows.Graphics;

namespace TidyWindow.Next.Desktop;

/// <summary>
/// Where the main window opens. Worked out once with plain Win32 calls, so the splash (before any interface exists) and the window that
/// replaces it cover exactly the same rectangle.
/// </summary>
internal static class StartupFrame
{
    private const int DesignWidth = 1100, DesignHeight = 740, Margin = 32;
    private static readonly Lazy<(RectInt32 Rect, RectInt32 WorkArea, double Scale)> frame = new(Compute);

    /// <summary>The window's outer rectangle in screen pixels: its usual size, centered on the main screen.</summary>
    public static RectInt32 Rect => frame.Value.Rect;
    public static RectInt32 WorkArea => frame.Value.WorkArea;
    /// <summary>How many pixels one layout unit is on the main screen.</summary>
    public static double Scale => frame.Value.Scale;

    private static (RectInt32, RectInt32, double) Compute()
    {
        var monitor = MonitorFromPoint(default, 1 /* the primary monitor */);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        var scale = GetDpiForMonitor(monitor, 0 /* effective DPI */, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1.0;
        var area = GetMonitorInfo(monitor, ref info) ? new RectInt32(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top) : new RectInt32(0, 0, 1280, 720);
        var width = Math.Min((int)(DesignWidth * scale), Math.Max(1, area.Width - (int)(Margin * scale)));
        var height = Math.Min((int)(DesignHeight * scale), Math.Max(1, area.Height - (int)(Margin * scale)));
        return (new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height), area, scale);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public NativeRect Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
}
