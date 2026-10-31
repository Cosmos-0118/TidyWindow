using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace TidyWindow.Next.Desktop;

/// <summary>
/// The window that is on screen from the first moment of launch until the app underneath it is drawn. It is plain Win32 on a thread of its own,
/// for two reasons: it can appear before the interface framework has even started, and its fades are driven by the desktop compositor, so they stay
/// smooth however busy the interface thread is while the real window is being built.
/// It covers the exact rectangle the main window will take. The main window opens underneath, finishes drawing, and only then does this fade out.
/// </summary>
internal sealed class NativeSplash
{
    private static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(240), FadeOut = TimeSpan.FromMilliseconds(320);
    // Long enough to read as a deliberate moment, short enough never to feel like waiting. A slow start simply lasts longer.
    private static readonly TimeSpan MinimumVisible = TimeSpan.FromMilliseconds(650);
    // A splash that is never dismissed would cover the app for good, so it gives up on its own.
    private static readonly TimeSpan GiveUp = TimeSpan.FromSeconds(20);

    private readonly ManualResetEventSlim created = new(), dismissed = new();
    private volatile IntPtr handle;

    private NativeSplash() { }

    /// <summary>Starts the splash on its own thread and returns at once. Never throws: with no splash the app just opens as it always did.</summary>
    public static NativeSplash? Start()
    {
        try
        {
            var splash = new NativeSplash();
            var thread = new Thread(splash.Run) { IsBackground = true, Name = "Splash", Priority = ThreadPriority.AboveNormal };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return splash;
        }
        catch (Exception ex) when (ex is ThreadStartException or OutOfMemoryException or InvalidOperationException) { return null; }
    }

    /// <summary>Asks the splash to fade away, once it has been visible for a moment. Safe from any thread, and safe to repeat.</summary>
    public void Dismiss() => dismissed.Set();

    /// <summary>Puts the given window just below the splash, so the splash stays in front of it without staying in front of everything else.</summary>
    public void KeepAbove(IntPtr window)
    {
        if (!created.Wait(500) || handle == IntPtr.Zero) return;
        SetWindowPos(window, handle, 0, 0, 0, 0, NoMove | NoSize | NoActivate);
    }

    private void Run()
    {
        try
        {
            var system = new UISettings();
            var theme = AppearanceSettings.ThemeFor(new AppearanceSettings(AppearanceSettings.DefaultPath).Load(), system);
            // No fade over Remote Desktop or when Windows animations are off: each step would only be a slow repaint.
            Show(theme == ElementTheme.Light ? Palette.Light : Palette.Dark, animate: system.AnimationsEnabled && GetSystemMetrics(RemoteSession) == 0);
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidOperationException or OutOfMemoryException) { }
        finally { created.Set(); }
    }

    private void Show(Palette palette, bool animate)
    {
        var frame = StartupFrame.Rect;
        int width = frame.Width, height = frame.Height;
        // Windows 11 rounds the corners of its windows; on older versions the window is square, and so is this.
        var radius = Environment.OSVersion.Version.Build >= 22000 ? 8 * StartupFrame.Scale : 0;
        var pixels = Painting.Render(width, height, StartupFrame.Scale, radius, palette);

        var window = CreateWindow(frame);
        if (window == IntPtr.Zero) return;
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var info = new BitmapInfoHeader { Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
        var bitmap = CreateDIBSection(memory, ref info, 0, out var bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero) { DestroyWindow(window); ReleaseDC(IntPtr.Zero, screen); DeleteDC(memory); return; }
        var previous = SelectObject(memory, bitmap);
        Marshal.Copy(pixels, 0, bits, pixels.Length);

        var position = new NativePoint { X = frame.X, Y = frame.Y };
        var size = new NativeSize { Width = width, Height = height };
        var origin = new NativePoint();
        // The picture is handed over once, at no opacity. After that only the opacity changes, which costs the compositor almost nothing.
        var blend = new LayerBlend { Operation = 0 /* source over */, Alpha = 0, Format = 1 /* the picture carries its own alpha */ };
        UpdateLayeredWindow(window, screen, ref position, ref size, memory, ref origin, 0, ref blend, 2 /* use the blend */);
        handle = window;
        ShowWindow(window, 4 /* shown without taking focus */);
        created.Set();

        var shownAt = Stopwatch.StartNew();
        var cheap = true;
        void SetOpacity(byte alpha)
        {
            blend.Alpha = alpha;
            if (cheap && UpdateLayeredWindow(window, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref blend, 2)) return;
            // A system that will not change the opacity alone gets the whole picture again instead.
            cheap = false;
            UpdateLayeredWindow(window, screen, ref position, ref size, memory, ref origin, 0, ref blend, 2);
        }

        Fade(0, 255, animate ? FadeIn : TimeSpan.Zero, Ease.Out, SetOpacity);
        while (shownAt.Elapsed < GiveUp)
        {
            if (dismissed.IsSet && shownAt.Elapsed >= MinimumVisible) break;
            // Once asked to leave but still inside the minimum, there is nothing to wake for, so it only waits out the clock.
            if (dismissed.IsSet) Thread.Sleep(8); else dismissed.Wait(16);
            Pump();
        }
        Fade(255, 0, animate ? FadeOut : TimeSpan.Zero, Ease.InOut, SetOpacity);

        handle = IntPtr.Zero;
        DestroyWindow(window);
        SelectObject(memory, previous);
        DeleteObject(bitmap);
        DeleteDC(memory);
        ReleaseDC(IntPtr.Zero, screen);
    }

    /// <summary>Changes the opacity once per frame the desktop draws, so every step lands on a frame instead of between two.</summary>
    private static void Fade(byte from, byte to, TimeSpan length, Func<double, double> ease, Action<byte> apply)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var progress = length <= TimeSpan.Zero ? 1 : Math.Min(1, clock.Elapsed / length);
            apply((byte)Math.Round(from + (to - from) * ease(progress)));
            if (progress >= 1) return;
            if (DwmFlush() != 0) Thread.Sleep(8);
            Pump();
        }
    }

    private static class Ease
    {
        public static double Out(double t) => 1 - Math.Pow(1 - t, 3);
        public static double InOut(double t) => t * t * (3 - 2 * t);
    }

    private static void Pump()
    {
        while (PeekMessage(out var message, IntPtr.Zero, 0, 0, 1 /* remove */)) { TranslateMessage(ref message); DispatchMessage(ref message); }
    }

    private static readonly WindowProcedure Procedure = (window, message, wParam, lParam) => DefWindowProc(window, message, wParam, lParam);

    private static IntPtr CreateWindow(Windows.Graphics.RectInt32 frame)
    {
        var instance = GetModuleHandle(null);
        var wndClass = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = Marshal.GetFunctionPointerForDelegate(Procedure), Instance = instance, Cursor = LoadCursor(IntPtr.Zero, (IntPtr)32512 /* the arrow */), ClassName = "TidyWindow.Splash" };
        // A second registration fails harmlessly: the class from the first one is still there.
        RegisterClassEx(ref wndClass);
        // Tool window and no-activate: out of the taskbar and Alt+Tab, and never takes the keyboard from the window that is opening.
        return CreateWindowEx(0x80000 | 0x80 | 0x08000000, wndClass.ClassName, "TidyWindow", 0x80000000, frame.X, frame.Y, frame.Width, frame.Height, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
    }

    private const uint NoSize = 0x1, NoMove = 0x2, NoActivate = 0x10;
    private const int RemoteSession = 0x1000;

    private readonly record struct Palette(int Background, int Line, int GradientStart, int GradientEnd, int Gold, int Quiet)
    {
        // The same colours as the app's own theme and loading mark, so the splash and the window beneath it match.
        public static readonly Palette Light = new(0xFFFFFF, 0xDADADA, 0x0E9F8E, 0x6D28D9, 0xF59E0B, 0xA78BFA);
        public static readonly Palette Dark = new(0x0A0A0A, 0x2E2E2E, 0x5EEAD4, 0xA78BFA, 0xFFC857, 0xE9E5FF);
    }

    /// <summary>Draws the splash with anti-aliased edges: a window-sized card in the app's background colour, with the mark in the middle.</summary>
    private static class Painting
    {
        public static int[] Render(int width, int height, double scale, double radius, Palette palette)
        {
            var pixels = new int[width * height];
            Array.Fill(pixels, Pack(255, palette.Background));
            // Only the rim and the corners differ from plain background, so only they are worked out pixel by pixel.
            var band = (int)Math.Ceiling(radius) + 2;
            for (var y = 0; y < height; y++)
            {
                var edgeRow = y < band || y >= height - band;
                for (var x = 0; x < width; x++)
                {
                    if (!edgeRow && x >= band && x < width - band) { x = width - band - 1; continue; }
                    pixels[y * width + x] = Rim(x, y, width, height, radius, palette);
                }
            }
            DrawMark(pixels, width, height, scale, palette);
            return pixels;
        }

        /// <summary>One pixel of the rim: transparent outside the rounded corners, the line colour along the edge, background inside.</summary>
        private static int Rim(int x, int y, int width, int height, double radius, Palette palette)
        {
            // Signed distance to the rounded rectangle: negative inside, positive outside.
            double px = Math.Abs(x + 0.5 - width / 2.0) - (width / 2.0 - radius), py = Math.Abs(y + 0.5 - height / 2.0) - (height / 2.0 - radius);
            var distance = Math.Sqrt(Math.Pow(Math.Max(px, 0), 2) + Math.Pow(Math.Max(py, 0), 2)) + Math.Min(Math.Max(px, py), 0) - radius;
            var outer = Math.Clamp(0.5 - distance, 0, 1);
            var inner = Math.Clamp(0.5 - (distance + 1), 0, 1);
            double Channel(int shift) => ((palette.Line >> shift) & 0xFF) * (outer - inner) + ((palette.Background >> shift) & 0xFF) * inner;
            // Colours in a layered window are stored already multiplied by their opacity.
            return ((int)Math.Round(outer * 255) << 24) | ((int)Math.Round(Channel(16)) << 16) | ((int)Math.Round(Channel(8)) << 8) | (int)Math.Round(Channel(0));
        }

        private static void DrawMark(int[] pixels, int width, int height, double scale, Palette palette)
        {
            // The three panes of the app icon, at the same proportions as the loading animation draws them.
            (double X, double Y, double W, double H, int Colour)[] panes = [(3, 3, 30, 66, -1), (38, 3, 31, 29, palette.Gold), (38, 37, 31, 32, palette.Quiet)];
            double originX = width / 2.0 - 36 * scale, originY = height / 2.0 - 36 * scale;
            foreach (var (left, top, paneWidth, paneHeight, colour) in panes)
            {
                int x0 = (int)Math.Floor(originX + (left - 1) * scale), x1 = (int)Math.Ceiling(originX + (left + paneWidth + 1) * scale);
                int y0 = (int)Math.Floor(originY + (top - 1) * scale), y1 = (int)Math.Ceiling(originY + (top + paneHeight + 1) * scale);
                for (var y = y0; y < y1; y++)
                    for (var x = x0; x < x1; x++)
                    {
                        // Back to the mark's own units, where a pane is a rounded rectangle with 7-unit corners.
                        double u = (x + 0.5 - originX) / scale, v = (y + 0.5 - originY) / scale;
                        double qx = Math.Abs(u - (left + paneWidth / 2)) - (paneWidth / 2 - 7), qy = Math.Abs(v - (top + paneHeight / 2)) - (paneHeight / 2 - 7);
                        var distance = (Math.Sqrt(Math.Pow(Math.Max(qx, 0), 2) + Math.Pow(Math.Max(qy, 0), 2)) + Math.Min(Math.Max(qx, qy), 0) - 7) * scale;
                        var cover = Math.Clamp(0.5 - distance, 0, 1);
                        if (cover <= 0) continue;
                        // The long pane fades corner to corner; the other two are one colour.
                        var fill = colour >= 0 ? colour : Blend(palette.GradientStart, palette.GradientEnd, Math.Clamp(((u - left) / paneWidth + (v - top) / paneHeight) / 2, 0, 1));
                        pixels[y * width + x] = Pack(255, Blend(pixels[y * width + x] & 0xFFFFFF, fill, cover));
                    }
            }
        }

        private static int Blend(int from, int to, double amount)
        {
            int Mix(int shift) => (int)Math.Round(((from >> shift) & 0xFF) * (1 - amount) + ((to >> shift) & 0xFF) * amount);
            return (Mix(16) << 16) | (Mix(8) << 8) | Mix(0);
        }

        private static int Pack(int alpha, int rgb) => (alpha << 24) | rgb;
    }

    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct LayerBlend { public byte Operation, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, ImageSize; public int XPixelsPerMeter, YPixelsPerMeter; public uint ColorsUsed, ColorsImportant; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style; public IntPtr Procedure; public int ClassExtra, WindowExtra; public IntPtr Instance, Icon, Cursor, Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName; [MarshalAs(UnmanagedType.LPWStr)] public string ClassName; public IntPtr SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public IntPtr Window; public uint Id; public IntPtr WParam, LParam; public uint Time; public NativePoint Point; public uint Private; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr context);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destination, ref NativePoint position, ref NativeSize size, IntPtr source, ref NativePoint sourcePosition, uint colourKey, ref LayerBlend blend, uint flags);
    [DllImport("user32.dll", EntryPoint = "UpdateLayeredWindow")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destination, IntPtr position, IntPtr size, IntPtr source, IntPtr sourcePosition, uint colourKey, ref LayerBlend blend, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr context);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr context, ref BitmapInfoHeader info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr context, IntPtr item);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr item);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr context);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
