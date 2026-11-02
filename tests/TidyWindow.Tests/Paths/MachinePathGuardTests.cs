using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

[SupportedOSPlatform("windows")]
public sealed class MachinePathGuardTests
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier AuthenticatedUsers = new(WellKnownSidType.AuthenticatedUserSid, null);
    private static readonly SecurityIdentifier Someone = new("S-1-5-21-1111111111-2222222222-3333333333-1001");

    private static string Expand(string item) => item.Replace("%SystemRoot%", @"C:\Windows", StringComparison.OrdinalIgnoreCase);
    private static readonly HashSet<string> Existing = new(StringComparer.OrdinalIgnoreCase) { @"C:\Windows", @"C:\Windows\system32", @"C:\Program Files\Git\cmd", @"C:\Tools", @"C:\Users\me\bin" };
    private static readonly HashSet<string> Open = new(StringComparer.OrdinalIgnoreCase) { @"C:\Tools", @"C:\Users\me\bin" };

    private static string? Problem(string? current, string value) => MachinePathGuard.Problem(current, value, Expand, Existing.Contains, Open.Contains);

    [Fact]
    public void FoldersAlreadyOnTheListStayWhateverTheirPermissions()
    {
        const string current = @"%SystemRoot%\system32;C:\Tools;C:\Users\me\bin";
        Assert.Null(Problem(current, current));
        Assert.Null(Problem(current, @"C:\Users\me\bin;C:\Tools;%SystemRoot%\system32"));
        Assert.Null(Problem(current, @"C:\Windows\System32\;""C:\Tools"""));
        Assert.Null(Problem(current, @"C:\Tools"));
    }

    [Fact]
    public void AFolderThatOrdinaryAccountsCanChangeIsNotAdded()
    {
        var problem = Problem(@"%SystemRoot%\system32", @"%SystemRoot%\system32;C:\Tools");
        Assert.NotNull(problem);
        Assert.Contains("C:\\Tools", problem);
        Assert.Contains("your own PATH", problem);
        Assert.Contains("administrator", Problem(@"C:\Windows", @"C:\Windows;C:\Users\me\bin"));
    }

    [Fact]
    public void AFolderAdministratorsOwnIsAdded()
    {
        Assert.Null(Problem(@"%SystemRoot%\system32", @"%SystemRoot%\system32;C:\Program Files\Git\cmd"));
        Assert.Null(Problem("", @"C:\Windows"));
    }

    [Fact]
    public void OnlyFullPathsToFoldersOnThisPcAreAdded()
    {
        Assert.Contains("does not exist", Problem(@"C:\Windows", @"C:\Windows;C:\Nope"));
        Assert.Contains("not a full folder path", Problem(@"C:\Windows", @"C:\Windows;bin\tools"));
        Assert.Contains("not a full folder path", Problem(@"C:\Windows", @"C:\Windows;C:"));
        Assert.Contains("network location", Problem(@"C:\Windows", @"C:\Windows;\\server\share\bin"));
        Assert.Contains("variable", Problem(@"C:\Windows", @"C:\Windows;%NOPE%\bin"));
        Assert.Contains("longer", Problem(@"C:\Windows", @"C:\Windows;C:\" + new string('a', 1100)));
    }

    [Fact]
    public async Task TheHelperRefusesAValueTheGuardRejectsAndWritesOneItAccepts()
    {
        var machine = @"C:\Windows";
        var writes = 0;
        using var session = new MachinePathSession(pipe => MachinePathHost.RunAsync([MachinePathHost.Command, pipe], () => machine, value => { machine = value; writes++; }, () => true, TimeSpan.FromSeconds(20),
            (current, value) => value.Contains("evil", StringComparison.OrdinalIgnoreCase) ? "\"C:\\evil\" can be changed by accounts without administrator rights." : null));
        await session.OpenAsync();

        var refused = Assert.Throws<IOException>(() => session.Write(@"C:\Windows", @"C:\Windows;C:\evil"));
        Assert.Contains("administrator", refused.Message);
        Assert.Equal(@"C:\Windows", machine);
        Assert.Equal(0, writes);

        session.Write(@"C:\Windows", @"C:\Windows;C:\Program Files\Git\cmd");
        Assert.Equal(@"C:\Windows;C:\Program Files\Git\cmd", machine);
        Assert.Equal(1, writes);
    }

    [Fact]
    public void PermissionsThatLetAnOrdinaryAccountWriteOrRenameAreOpen()
    {
        if (!OperatingSystem.IsWindows()) return;
        // The folder itself: planting a file or renaming it away is enough.
        Assert.False(MachinePathGuard.Opens(Security(Administrators, (Administrators, FileSystemRights.FullControl), (System, FileSystemRights.FullControl), (Users, FileSystemRights.ReadAndExecute)), target: true));
        Assert.True(MachinePathGuard.Opens(Security(Administrators, (Administrators, FileSystemRights.FullControl), (AuthenticatedUsers, FileSystemRights.Modify)), target: true));
        Assert.True(MachinePathGuard.Opens(Security(Administrators, (Administrators, FileSystemRights.FullControl), (Users, FileSystemRights.CreateFiles)), target: true));
        Assert.True(MachinePathGuard.Opens(Security(Administrators, (Administrators, FileSystemRights.FullControl), (Someone, FileSystemRights.Delete)), target: true));
        Assert.True(MachinePathGuard.Opens(Security(Someone, (Administrators, FileSystemRights.FullControl)), target: true));
        Assert.True(MachinePathGuard.Opens(Security(Administrators, (Administrators, FileSystemRights.FullControl), (new SecurityIdentifier("S-1-15-2-1"), FileSystemRights.Write)), target: true));
        // A folder above it: creating siblings is harmless, taking the folder away is not.
        Assert.False(MachinePathGuard.Opens(Security(Administrators, (Administrators, FileSystemRights.FullControl), (AuthenticatedUsers, FileSystemRights.AppendData)), target: false));
        Assert.True(MachinePathGuard.Opens(Security(Administrators, (Administrators, FileSystemRights.FullControl), (Users, FileSystemRights.DeleteSubdirectoriesAndFiles)), target: false));
        Assert.True(MachinePathGuard.Opens(Security(Administrators, (Administrators, FileSystemRights.FullControl), (Users, FileSystemRights.ChangePermissions)), target: false));
        // A rule that only applies to what will be created inside does not open the folder itself.
        var inheritOnly = Security(Administrators, (Administrators, FileSystemRights.FullControl));
        inheritOnly.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
        inheritOnly.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
        Assert.False(MachinePathGuard.Opens(inheritOnly, target: true));
    }

    [Fact]
    public void TheProfilesFolderIsAlwaysOpen()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.True(MachinePathGuard.IsOpenToOrdinaryAccounts(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Assert.True(MachinePathGuard.IsOpenToOrdinaryAccounts(Path.Combine(Path.GetTempPath(), "anything")));
        Assert.False(MachinePathGuard.IsOpenToOrdinaryAccounts(Environment.SystemDirectory));
    }

    private static DirectorySecurity Security(SecurityIdentifier owner, params (SecurityIdentifier Who, FileSystemRights Rights)[] rules)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(owner);
        foreach (var (who, rights) in rules)
            security.AddAccessRule(new FileSystemAccessRule(who, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }
}
