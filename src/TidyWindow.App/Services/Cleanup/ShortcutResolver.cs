using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TidyWindow.App.Services.Cleanup;

internal static class ShortcutResolver
{
    public static bool TryGetTarget(string shortcutPath, out string? target)
    {
        target = null;
        if (string.IsNullOrWhiteSpace(shortcutPath))
        {
            return false;
        }

        if (!System.IO.File.Exists(shortcutPath))
        {
            return false;
        }

        IShellLinkW? link = null;
        IPersistFile? file = null;
        try
        {
            var shellType = Type.GetTypeFromCLSID(ShellLinkClsid, throwOnError: false);
            if (shellType is null)
            {
                return false;
            }

            link = (IShellLinkW)Activator.CreateInstance(shellType)!;
            file = (IPersistFile)link;
            file.Load(shortcutPath, STGM_READ);
            var builder = new StringBuilder(1024);
            link.GetPath(builder, builder.Capacity, IntPtr.Zero, 0);
            var candidate = builder.ToString();
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                target = candidate;
                return true;
            }
        }
        catch (COMException)
        {
            return false;
        }
        catch (System.IO.FileNotFoundException)
        {
            return false;
        }
        finally
        {
            if (file is not null)
            {
                Marshal.FinalReleaseComObject(file);
            }

            if (link is not null)
            {
                Marshal.FinalReleaseComObject(link);
            }
        }

        return false;
    }

    private const uint STGM_READ = 0;
    private static readonly Guid ShellLinkClsid = new("00021401-0000-0000-C000-000000000046");

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short wHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int iShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int iIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
