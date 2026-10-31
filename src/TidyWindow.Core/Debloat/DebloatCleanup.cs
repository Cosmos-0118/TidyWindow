using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

/// <summary>
/// What OneDrive's and Edge's own uninstallers leave behind in every account on the PC: startup entries, scheduled tasks, the OneDrive folder in
/// File Explorer's side bar and Edge's shortcuts. These are the rules for what counts; the steps that apply them are in <see cref="WindowsPlaybooks"/>.
/// Only entries of these two programs are ever matched, and never a file of yours.
/// </summary>
public static partial class DebloatCleanup
{
    /// <summary>The side-bar entry File Explorer shows for OneDrive. Setting <c>System.IsPinnedToNameSpaceTree</c> to 0 takes it away.</summary>
    public const string OneDriveNavigationPane = "{018D5C66-4533-4307-9B53-224DE2ED1FE6}";
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 ._-]{0,127}$")]
    private static partial Regex PlainTaskName();

    public static bool IsOneDriveRunValue(string name) =>
        name.Equals("OneDrive", StringComparison.OrdinalIgnoreCase) || name.Equals("OneDriveSetup", StringComparison.OrdinalIgnoreCase);

    public static bool IsEdgeRunValue(string name) =>
        name.StartsWith("MicrosoftEdgeAutoLaunch_", StringComparison.OrdinalIgnoreCase) || name.Equals("Microsoft Edge Update", StringComparison.OrdinalIgnoreCase);

    /// <summary>The scheduled tasks the OneDrive installer registers for updating and reporting. Their names end in the account they belong to.</summary>
    public static bool IsOneDriveTask(string name) =>
        PlainTaskName().IsMatch(name) &&
        (name.StartsWith("OneDrive Standalone Update Task", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("OneDrive Reporting Task", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("OneDrive Per-Machine Standalone Update Task", StringComparison.OrdinalIgnoreCase));

    /// <summary>A person's account: a local, Microsoft or domain account (S-1-5-21-…) or a work or school account (S-1-12-1-…). Windows' own service accounts are not.</summary>
    public static bool IsAccountSid(string? sid) =>
        DebloatGuard.IsSid(sid) && (sid!.StartsWith("S-1-5-21-", StringComparison.Ordinal) || sid.StartsWith("S-1-12-1-", StringComparison.Ordinal));

    /// <summary>Where Edge puts its shortcuts inside one account's profile folder.</summary>
    public static IEnumerable<string> EdgeShortcuts(string profile)
    {
        yield return Path.Combine(profile, "Desktop", "Microsoft Edge.lnk");
        yield return Path.Combine(profile, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs", "Microsoft Edge.lnk");
        yield return Path.Combine(profile, "AppData", "Roaming", "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar", "Microsoft Edge.lnk");
    }
}
