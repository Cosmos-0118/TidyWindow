using System;
using System.IO;
using System.Runtime.InteropServices;

namespace TidyWindow.Core.Cleanup;

internal static class CleanupNativeMethods
{
    private const ushort FO_DELETE = 0x0003;

    [Flags]
    private enum FileOperationFlags : ushort
    {
        FOF_MULTIDESTFILES = 0x0001,
        FOF_CONFIRMMOUSE = 0x0002,
        FOF_SILENT = 0x0004,
        FOF_RENAMEONCOLLISION = 0x0008,
        FOF_NOCONFIRMATION = 0x0010,
        FOF_WANTMAPPINGHANDLE = 0x0020,
        FOF_ALLOWUNDO = 0x0040,
        FOF_FILESONLY = 0x0080,
        FOF_SIMPLEPROGRESS = 0x0100,
        FOF_NOCONFIRMMKDIR = 0x0200,
        FOF_NOERRORUI = 0x0400,
        FOF_WANTNUKEWARNING = 0x4000
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public ushort wFunc;
        public string pFrom;
        public string? pTo;
        public FileOperationFlags fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT fileOp);

    public static bool TrySendToRecycleBin(string path, out Exception? failure)
    {
        failure = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var normalizedPath = path.EndsWith("\0\0", StringComparison.Ordinal) ? path : path + "\0\0";
            var fileOp = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = normalizedPath,
                pTo = null,
                fFlags = FileOperationFlags.FOF_ALLOWUNDO |
                         FileOperationFlags.FOF_NOCONFIRMATION |
                         FileOperationFlags.FOF_NOERRORUI |
                         FileOperationFlags.FOF_SILENT |
                         FileOperationFlags.FOF_WANTNUKEWARNING
            };

            var result = SHFileOperation(ref fileOp);
            if (result == 0 && !fileOp.fAnyOperationsAborted)
            {
                return true;
            }

            if (fileOp.fAnyOperationsAborted)
            {
                failure = new IOException("Recycle bin operation was cancelled by the system.");
                return false;
            }

            failure = new IOException($"Recycle bin operation failed with code 0x{result:X}.");
            return false;
        }
        catch (Exception ex)
        {
            failure = ex;
            return false;
        }
    }
}
