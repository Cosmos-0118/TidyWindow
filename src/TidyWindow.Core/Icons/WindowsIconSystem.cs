using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace TidyWindow.Next.Core;

/// <summary>The real PC, as far as icons go: App Paths, shortcuts, folders and the installed-programs list.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsIconSystem : IIconSystem
{
    private const string AppPaths = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
    private const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Wow64Uninstall = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    public string Expand(string text) => Environment.ExpandEnvironmentVariables(text);

    public bool FileExists(string path)
    {
        try { return File.Exists(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    public string? FindOnPath(string fileName)
    {
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Append(Environment.SystemDirectory).Append(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        foreach (var folder in folders)
        {
            try
            {
                var candidate = Path.Combine(Expand(folder).Trim().Trim('"'), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { }
        }
        return null;
    }

    public string? AppPath(string fileName)
    {
        if (fileName.Length == 0 || fileName.IndexOfAny(['\\', '/']) >= 0) return null;
        foreach (var (hive, view) in new[] { (RegistryHive.CurrentUser, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32) })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey($@"{AppPaths}\{fileName}");
                if (key?.GetValue(null) is string { Length: > 0 } path) return Expand(path).Trim().Trim('"');
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException) { }
        }
        return null;
    }

    public (string Target, string Icon) Shortcut(string path)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return ("", "");
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic link = shell.CreateShortcut(path);
            return ((string)link.TargetPath ?? "", (string)link.IconLocation ?? "");
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { return ("", ""); }
    }

    public IReadOnlyList<string> Subfolders(string folder)
    {
        try { return Directory.Exists(folder) ? Directory.GetDirectories(folder) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    public IReadOnlyList<string> Files(string folder, string pattern)
    {
        try { return Directory.Exists(folder) ? Directory.GetFiles(folder, pattern) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    public string? ReadText(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length < 512 * 1024 ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    public IReadOnlyList<UninstallIconFact> Programs()
    {
        var found = new List<UninstallIconFact>();
        foreach (var (hive, path) in new[] { (RegistryHive.LocalMachine, Uninstall), (RegistryHive.LocalMachine, Wow64Uninstall), (RegistryHive.CurrentUser, Uninstall), (RegistryHive.CurrentUser, Wow64Uninstall) })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var list = root.OpenSubKey(path);
                if (list is null) continue;
                foreach (var name in list.GetSubKeyNames())
                {
                    try
                    {
                        using var key = list.OpenSubKey(name);
                        if (key is null) continue;
                        var title = Text(key, "DisplayName");
                        if (title.Length > 0) found.Add(new(title, Text(key, "DisplayIcon"), Text(key, "InstallLocation")));
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
        return found;
    }

    public IReadOnlyList<string> StartMenu()
    {
        var found = new List<string>();
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Environment.GetFolderPath(Environment.SpecialFolder.Programs) })
        {
            try
            {
                if (root.Length > 0 && Directory.Exists(root))
                    found.AddRange(Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4 }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return found;
    }

    public string ProductIcon(string productCode)
    {
        string packed;
        try { packed = InstallRepair.Pack(productCode); }
        catch (ArgumentException) { return ""; }
        foreach (var (hive, path) in new[] { (RegistryHive.LocalMachine, $@"SOFTWARE\Classes\Installer\Products\{packed}"), (RegistryHive.CurrentUser, $@"SOFTWARE\Microsoft\Installer\Products\{packed}") })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = root.OpenSubKey(path);
                if (key is not null && Text(key, "ProductIcon") is { Length: > 0 } icon) return icon;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
        return "";
    }

    private static string Text(RegistryKey key, string name) => key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
}

/// <summary>A picture as bottom-up-free 32-bit pixels: blue, green, red, then alpha already multiplied in, which is what a XAML bitmap expects.</summary>
public sealed record IconBitmap(int Width, int Height, byte[] Bgra);

public interface IIconReader
{
    /// <summary>The icon as pixels about <paramref name="size"/> wide, or null when the file has none.</summary>
    IconBitmap? Read(IconRef icon, int size);
}

/// <summary>Pulls icons out of programs, libraries and icon files, or asks the shell for the icon of a kind of file.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsIconReader : IIconReader
{
    public IconBitmap? Read(IconRef icon, int size)
    {
        var handle = icon.Source == IconSource.FileType ? ShellIcon(icon.Path) : FileIcon(icon.Path, icon.Index, size);
        if (handle == IntPtr.Zero) return null;
        try { return Pixels(handle); }
        finally { DestroyIcon(handle); }
    }

    private static IntPtr FileIcon(string path, int index, int size)
    {
        var handles = new IntPtr[1];
        try
        {
            var count = PrivateExtractIcons(path, index, size, size, handles, null, 1, 0);
            return count is 0 or uint.MaxValue ? IntPtr.Zero : handles[0];
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException) { return IntPtr.Zero; }
    }

    // The shell's 48 px image list gives the icon Explorer shows for this kind of file; a file that is not there is judged by its extension alone.
    private static IntPtr ShellIcon(string path)
    {
        try
        {
            var exists = File.Exists(path);
            var info = new ShellFileInfo();
            var flags = SHGFI_SYSICONINDEX | (exists ? 0u : SHGFI_USEFILEATTRIBUTES);
            if (SHGetFileInfo(path, 0x80, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), flags) == IntPtr.Zero) return IntPtr.Zero;
            var id = typeof(IImageList).GUID;
            if (SHGetImageList(SHIL_EXTRALARGE, ref id, out var list) == 0 && list is not null)
            {
                try { if (list.GetIcon(info.IconIndex, 1, out var big) == 0 && big != IntPtr.Zero) return big; }
                finally { Marshal.ReleaseComObject(list); }
            }
            var plain = new ShellFileInfo();
            return SHGetFileInfo(path, 0x80, ref plain, (uint)Marshal.SizeOf<ShellFileInfo>(), SHGFI_ICON | (exists ? 0u : SHGFI_USEFILEATTRIBUTES)) == IntPtr.Zero ? IntPtr.Zero : plain.Icon;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException or COMException or ArgumentException) { return IntPtr.Zero; }
    }

    private static IconBitmap? Pixels(IntPtr icon)
    {
        if (!GetIconInfo(icon, out var info)) return null;
        try
        {
            if (info.Color == IntPtr.Zero || GetObject(info.Color, Marshal.SizeOf<Bitmap>(), out var bitmap) == 0) return null;
            int width = bitmap.Width, height = bitmap.Height;
            if (width <= 0 || height <= 0 || width > 512 || height > 512) return null;
            var screen = GetDC(IntPtr.Zero);
            try
            {
                var colors = Read(screen, info.Color, width, height);
                if (colors is null) return null;
                // Icons from before Windows XP carry no alpha; their transparency is the separate mask.
                var opaque = true;
                for (var i = 3; i < colors.Length; i += 4) if (colors[i] != 0) { opaque = false; break; }
                if (opaque && info.Mask != IntPtr.Zero && Read(screen, info.Mask, width, height) is { } mask)
                    for (var i = 0; i < colors.Length; i += 4) colors[i + 3] = mask[i] == 0 ? (byte)255 : (byte)0;
                for (var i = 0; i < colors.Length; i += 4)
                {
                    var alpha = colors[i + 3];
                    if (alpha == 255) continue;
                    colors[i] = (byte)(colors[i] * alpha / 255); colors[i + 1] = (byte)(colors[i + 1] * alpha / 255); colors[i + 2] = (byte)(colors[i + 2] * alpha / 255);
                }
                return new(width, height, colors);
            }
            finally { ReleaseDC(IntPtr.Zero, screen); }
        }
        finally
        {
            if (info.Color != IntPtr.Zero) DeleteObject(info.Color);
            if (info.Mask != IntPtr.Zero) DeleteObject(info.Mask);
        }
    }

    private static byte[]? Read(IntPtr screen, IntPtr bitmap, int width, int height)
    {
        var header = new BitmapInfo { Size = Marshal.SizeOf<BitmapInfo>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
        var data = new byte[width * height * 4];
        return GetDIBits(screen, bitmap, 0, (uint)height, data, ref header, 0) == height ? data : null;
    }

    private const uint SHGFI_ICON = 0x100, SHGFI_SYSICONINDEX = 0x4000, SHGFI_USEFILEATTRIBUTES = 0x10;
    private const int SHIL_EXTRALARGE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon; public int IconIndex; public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo { [MarshalAs(UnmanagedType.Bool)] public bool IsIcon; public int HotspotX; public int HotspotY; public IntPtr Mask; public IntPtr Color; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Bitmap { public int Type; public int Width; public int Height; public int WidthBytes; public ushort Planes; public ushort BitsPixel; public IntPtr Bits; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Size; public int Width; public int Height; public ushort Planes; public ushort BitCount; public uint Compression; public uint ImageSize;
        public int XPixelsPerMeter; public int YPixelsPerMeter; public uint ColorsUsed; public uint ColorsImportant;
    }

    // Only GetIcon is called; the methods before it are listed so it lands in the right place of the interface.
    [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig] int Add(IntPtr image, IntPtr mask, out int index);
        [PreserveSig] int ReplaceIcon(int index, IntPtr icon, out int result);
        [PreserveSig] int SetOverlayImage(int image, int overlay);
        [PreserveSig] int Replace(int index, IntPtr image, IntPtr mask);
        [PreserveSig] int AddMasked(IntPtr image, int mask, out int index);
        [PreserveSig] int Draw(IntPtr parameters);
        [PreserveSig] int Remove(int index);
        [PreserveSig] int GetIcon(int index, int flags, out IntPtr icon);
    }

    [DllImport("user32.dll", EntryPoint = "PrivateExtractIconsW", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string file, int index, int width, int height, [Out] IntPtr[] icons, uint[]? ids, uint count, uint flags);

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);

    [DllImport("shell32.dll")]
    private static extern int SHGetImageList(int size, ref Guid id, [MarshalAs(UnmanagedType.Interface)] out IImageList? list);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetObject(IntPtr handle, int size, out Bitmap bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfo info, uint usage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);
}
