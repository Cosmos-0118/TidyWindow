using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TidyWindow.App.Services;

internal static class AppUserModelIdService
{
    // Keep in sync with installer/AppUserModelID entries.
    private const string AppUserModelId = "TidyWindow";

    public static void EnsureCurrentProcessAppUserModelId()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var current = TryGetCurrentProcessAppUserModelId();
            if (string.Equals(current, AppUserModelId, StringComparison.Ordinal))
            {
                return;
            }

            var hr = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            if (hr < 0)
            {
                Debug.WriteLine($"SetCurrentProcessExplicitAppUserModelID failed with HRESULT 0x{hr:X8}.");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to set AppUserModelID: {ex}");
        }
    }

    private static string? TryGetCurrentProcessAppUserModelId()
    {
        var hr = GetCurrentProcessExplicitAppUserModelID(out var rawId);
        if (hr != 0 || rawId == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(rawId);
        }
        finally
        {
            Marshal.FreeCoTaskMem(rawId);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetCurrentProcessExplicitAppUserModelID(out IntPtr appID);
}
