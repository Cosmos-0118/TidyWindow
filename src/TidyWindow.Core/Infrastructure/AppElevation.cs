using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace TidyWindow.Next.Core;

/// <summary>
/// Whether the app itself was started as administrator. It must not run that way: everything it does runs with the rights it has, and much of what it does is built to run with
/// ordinary rights. It reads its history, backups and undo records from a folder any program of this account can change, and it starts package managers from wherever PATH finds them,
/// including folders under the profile. Done as administrator, an edited file or a planted program would get those rights the moment someone used the app. The elevated helper is
/// written for those rights and trusts none of that; the app is not, so it declines to start and asks to be opened normally.
/// Only a process elevated past a limited token counts. With User Account Control off, or on an account that has no limited token, every program of that account already has these
/// rights, so there is nothing to protect by refusing and nowhere better to go.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AppElevation
{
    private const int TokenElevationType = 18, TokenElevationTypeFull = 2;

    /// <summary>What the person is told when the app declines to start.</summary>
    public const string Refusal =
        "TidyWindow does not run as administrator.\n\n" +
        "Open it the normal way, from the Start menu. It asks Windows for permission each time a change needs it.\n\n" +
        "Started as administrator, it would hand those rights to every program it runs, and would act on settings files that any program on this PC can change.";

    /// <summary>True when this process runs with the full administrator token of an account that also has a limited one, which is what "Run as administrator" gives.</summary>
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return GetTokenInformation(identity.Token, TokenElevationType, out var type, sizeof(int), out _) && type == TokenElevationTypeFull;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, out int information, int length, out int returned);
}
