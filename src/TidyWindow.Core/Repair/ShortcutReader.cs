using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;

namespace TidyWindow.Next.Core;

/// <summary>
/// Reads shortcut files with Windows' own shell link object, which handles every Unicode path (WScript.Shell returns nothing for a name with a character like U+A789)
/// and the properties a shortcut can carry: the target Windows parsed, and the app id a Store or shell shortcut is known by. A shortcut is never resolved:
/// resolving searches the disk for a moved target and can raise the "Problem with Shortcut" dialog.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ShortcutReader
{
    private const uint Raw = 4;                  // SLGP_RAWPATH: the stored path, with its %variables% still in it
    private const int CantAccessFile = unchecked((int)0x80070780), ProviderNotRunning = unchecked((int)0x8007018A);

    /// <summary>Reads every file on a thread of its own: the shell link object lives in a single-threaded apartment.</summary>
    public static IReadOnlyList<ShortcutFile> ReadAll(IReadOnlyList<string> paths)
    {
        var found = new List<ShortcutFile>(paths.Count);
        var thread = new Thread(() =>
        {
            var gone = CloudGone();
            foreach (var path in paths) found.Add(Read(path, gone));
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return found;
    }

    /// <summary>
    /// OneDrive is not installed. A file that is a cloud placeholder and cannot be opened then has no provider left to fetch it; with OneDrive installed
    /// the same error may only mean the PC is offline, and that file is the person's.
    /// </summary>
    private static bool CloudGone()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return !new[]
        {
            Path.Combine(local, @"Microsoft\OneDrive\OneDrive.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft OneDrive\OneDrive.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft OneDrive\OneDrive.exe")
        }.Any(File.Exists) && System.Diagnostics.Process.GetProcessesByName("OneDrive").Length == 0;
    }

    public static ShortcutFile Read(string path, bool cloudGone)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                try { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
                catch (IOException ex) when (cloudGone && ex.HResult is CantAccessFile or ProviderNotRunning) { return new(path, Unreadable: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(path, Unparsed: true); }
            }
            return path.EndsWith(".url", StringComparison.OrdinalIgnoreCase) ? ReadUrl(path) : ReadLink(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return new(path, Unparsed: true); }
    }

    private static ShortcutFile ReadUrl(string path)
    {
        string url = "", icon = "";
        foreach (var line in File.ReadLines(path).Take(200))
        {
            if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) url = line[4..].Trim();
            else if (line.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase)) icon = line[9..].Trim();
        }
        return url.Length == 0 ? new(path, Unparsed: true) : new(path, Icon: icon, Url: url);
    }

    private static ShortcutFile ReadLink(string path)
    {
        IShellLinkW? link = null;
        try
        {
            link = (IShellLinkW)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))!)!;
            ((IPersistFile)link).Load(path, 0);

            var target = new StringBuilder(1024);
            var arguments = new StringBuilder(2048);
            var folder = new StringBuilder(1024);
            var icon = new StringBuilder(1024);
            link.GetPath(target, target.Capacity, IntPtr.Zero, Raw);
            link.GetArguments(arguments, arguments.Capacity);
            link.GetWorkingDirectory(folder, folder.Capacity);
            link.GetIconLocation(icon, icon.Capacity, out var index);

            var shell = false;
            if (link.GetIDList(out var list) == 0 && list != IntPtr.Zero) { shell = true; Marshal.FreeCoTaskMem(list); }

            string parsed = "", appId = "";
            if (link is IPropertyStore store)
            {
                if (target.Length == 0) parsed = Property(store, new(new("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"), 2));
                appId = Property(store, new(new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5));
            }
            var product = Advertised(path);
            return new(path, target.Length > 0 ? target.ToString() : parsed, arguments.ToString(), folder.ToString(), icon.Length > 0 ? $"{icon},{index}" : "", appId, product, shell && target.Length == 0 && parsed.Length == 0);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new(path, Unparsed: true);
        }
        finally { if (link is not null) Marshal.ReleaseComObject(link); }
    }

    private static string Property(IPropertyStore store, PropertyKey key)
    {
        var value = new PropVariant();
        try
        {
            if (store.GetValue(ref key, out value) != 0 || value.Type != 31 || value.Pointer == IntPtr.Zero) return "";
            return Marshal.PtrToStringUni(value.Pointer) ?? "";
        }
        finally { PropVariantClear(ref value); }
    }

    /// <summary>The product code of a shortcut Windows Installer advertises (it starts the program, installing it first when needed), or empty.</summary>
    private static string Advertised(string path)
    {
        var product = new StringBuilder(39);
        var feature = new StringBuilder(39);
        var component = new StringBuilder(39);
        try { return MsiGetShortcutTarget(path, product, feature, component) == 0 ? product.ToString() : ""; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return ""; }
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiGetShortcutTarget(string shortcut, StringBuilder product, StringBuilder feature, StringBuilder component);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid format, uint id) { public Guid Format = format; public uint Id = id; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant { public ushort Type; public ushort R1, R2, R3; public IntPtr Pointer; public IntPtr Extra; }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    // Only the first methods are called; every method before the last one used is listed so each lands in the right slot.
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr data, uint flags);
        [PreserveSig] int GetIDList(out IntPtr list);
        [PreserveSig] int SetIDList(IntPtr list);
        [PreserveSig] int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int max);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        [PreserveSig] int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder folder, int max);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string folder);
        [PreserveSig] int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int max);
        [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        [PreserveSig] int GetHotkey(out short hotkey);
        [PreserveSig] int SetHotkey(short hotkey);
        [PreserveSig] int GetShowCmd(out int show);
        [PreserveSig] int SetShowCmd(int show);
        [PreserveSig] int GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder icon, int max, out int index);
        [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);
        [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        [PreserveSig] int Resolve(IntPtr window, uint flags);
        [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
