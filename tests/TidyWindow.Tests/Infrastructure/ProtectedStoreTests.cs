using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

[SupportedOSPlatform("windows")]
public sealed class ProtectedStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tw-protected-" + Guid.NewGuid().ToString("N")[..8]);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier Someone = new("S-1-5-21-1111111111-2222222222-3333333333-1001");

    public void Dispose()
    {
        foreach (var folder in Directory.GetParent(root)!.EnumerateDirectories(Path.GetFileName(root) + "*"))
        {
            try { folder.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record Copy(string Command, int Count);

    [Fact]
    public void ACopyComesBackUnderItsIdAndNotUnderAnother()
    {
        var store = new ProtectedStore("startup", root, enforce: false);
        store.Save("run|machine|Run|Updater", new Copy(@"C:\Apps\upd.exe", 2));

        Assert.Equal(new Copy(@"C:\Apps\upd.exe", 2), store.Load<Copy>("run|machine|Run|Updater"));
        Assert.Equal(new Copy(@"C:\Apps\upd.exe", 2), store.Load<Copy>("RUN|MACHINE|Run|UPDATER"));
        Assert.Null(store.Load<Copy>("run|machine|Run|Other"));
        Assert.True(store.Exists("run|machine|Run|Updater"));
        Assert.True(store.Delete("run|machine|Run|Updater"));
        Assert.False(store.Delete("run|machine|Run|Updater"));
        Assert.Null(store.Load<Copy>("run|machine|Run|Updater"));
    }

    [Fact]
    public void ANewerCopyReplacesTheOlderOne()
    {
        var store = new ProtectedStore("tune", root, enforce: false);
        store.Save("x", new Copy("one", 1));
        store.Save("x", new Copy("two", 2));
        Assert.Equal(new Copy("two", 2), store.Load<Copy>("x"));
        Assert.Single(Directory.GetFiles(store.Folder));
    }

    [Fact]
    public void AFileThatDoesNotCarryTheIdItWasAskedForIsIgnored()
    {
        var store = new ProtectedStore("startup", root, enforce: false);
        store.Save("a", new Copy("for a", 1));
        var file = Directory.GetFiles(store.Folder).Single();
        // A record renamed to another id's file name must not be believed, nor a damaged one.
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"Id\": \"a\"", "\"Id\": \"b\""));
        Assert.Null(store.Load<Copy>("a"));
        File.WriteAllText(file, "{ not json");
        Assert.Null(store.Load<Copy>("a"));
    }

    [Fact]
    public void NothingIsReadFromAFolderThatIsNotThereYet()
    {
        var store = new ProtectedStore("startup", root, enforce: false);
        Assert.Null(store.Load<Copy>("a"));
        Assert.False(store.Exists("a"));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void AnAreaNameMustBePlain()
    {
        Assert.Throws<ArgumentException>(() => new ProtectedStore("../startup", root));
        Assert.Throws<ArgumentException>(() => new ProtectedStore("", root));
    }

    [Fact]
    public void AFolderMadeByAnOrdinaryAccountIsMovedAsideAndNeverRead()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Whatever made this folder, it is not owned by administrators, so the store must not trust its contents.
        var planted = new ProtectedStore("startup", root, enforce: false);
        planted.Save("run|machine|Run|Updater", new Copy(@"C:\evil.exe", 1));

        var store = new ProtectedStore("startup", root, enforce: true);
        Assert.Null(store.Load<Copy>("run|machine|Run|Updater"));
        Assert.False(Directory.Exists(root));
        Assert.Single(Directory.GetParent(root)!.EnumerateDirectories(Path.GetFileName(root) + ".untrusted-*"));

        if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            // Running with administrator rights, the store can make a folder that is its own and use it.
            store.Save("run|machine|Run|Updater", new Copy(@"C:\Apps\upd.exe", 1));
            Assert.Equal(new Copy(@"C:\Apps\upd.exe", 1), store.Load<Copy>("run|machine|Run|Updater"));
            Assert.Null(ProtectedStore.Problem(new DirectoryInfo(root).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)));
        }
        else
        {
            // Without them, nothing can be kept there, and the caller hears about it rather than writing somewhere unsafe.
            Assert.ThrowsAny<Exception>(() => store.Save("run|machine|Run|Updater", new Copy(@"C:\Apps\upd.exe", 1)));
        }
    }

    [Fact]
    public void PermissionsAreTrustedOnlyWhenAdministratorsOwnTheFolderAndNobodyElseCanWrite()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Null(ProtectedStore.Problem(Security(Administrators, (Administrators, FileSystemRights.FullControl), (System, FileSystemRights.FullControl), (Users, FileSystemRights.ReadAndExecute))));
        Assert.Null(ProtectedStore.Problem(Security(System, (Administrators, FileSystemRights.FullControl))));
        Assert.Contains("owned", ProtectedStore.Problem(Security(Someone, (Administrators, FileSystemRights.FullControl))));
        Assert.Contains("owned", ProtectedStore.Problem(Security(Users, (Administrators, FileSystemRights.FullControl))));
        Assert.Contains("can change", ProtectedStore.Problem(Security(Administrators, (Administrators, FileSystemRights.FullControl), (Users, FileSystemRights.Modify))));
        Assert.Contains("can change", ProtectedStore.Problem(Security(Administrators, (Administrators, FileSystemRights.FullControl), (Someone, FileSystemRights.Delete))));
        Assert.Contains("can change", ProtectedStore.Problem(Security(Administrators, (Administrators, FileSystemRights.FullControl), (Users, FileSystemRights.TakeOwnership))));
        var inheriting = Security(Administrators, (Administrators, FileSystemRights.FullControl));
        inheriting.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
        Assert.Contains("parent", ProtectedStore.Problem(inheriting));
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
