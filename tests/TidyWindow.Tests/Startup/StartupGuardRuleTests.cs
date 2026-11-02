using System.Runtime.InteropServices;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

/// <summary>The rules that stop a startup item from talking its way into being left alone, and the checks on what a put-back writes.</summary>
public sealed class StartupGuardRuleTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tw-guard-" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    /// <summary>The start of every real shortcut file: its header size and the shell link class id, then a few bytes of flags.</summary>
    public static readonly byte[] Shortcut = [0x4C, 0, 0, 0, 0x01, 0x14, 0x02, 0, 0, 0, 0, 0, 0xC0, 0, 0, 0, 0, 0, 0, 0x46, 0x9B, 0, 0x08, 0];

    public const string UserTask = "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Principals><Principal id=\"Author\"><UserId>S-1-5-21-1-2-3-1001</UserId><LogonType>InteractiveToken</LogonType></Principal></Principals><Actions><Exec><Command>C:\\Apps\\a.exe</Command></Exec></Actions></Task>";
    public const string SystemTask = "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Principals><Principal id=\"Author\"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals><Actions><Exec><Command>C:\\Users\\me\\evil.exe</Command></Exec></Actions></Task>";

    private static StartupRequest Remove(string id) => new(id, false, Action: StartupAction.Remove);
    private static StartupRequest Restore(string id, StartupVault? vault) => new(id, true, Action: StartupAction.Restore, Vault: vault);
    private static StartupEntry Only(FakeStartupPc pc) => Assert.Single(StartupScan.Run(pc).Entries);

    [Fact]
    public void AnUnsignedProgramIsNotProtectedForCallingItselfSecuritySoftwareOrMicrosoft()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Users\me\AppData\Roaming\av\defender.exe", "Microsoft Corporation", StartupTrust.Unsigned);
        pc.Add(@"C:\ProgramData\svc\guard.exe", "Microsoft Corporation", StartupTrust.Unsigned);
        pc.Add(@"C:\Windows\System32\cmd.exe", "Microsoft Corporation");
        pc.Runs.Add(new(StartupScope.User, false, "Windows Defender", @"C:\Users\me\AppData\Roaming\av\defender.exe"));
        pc.Runs.Add(new(StartupScope.User, false, "Avast Service", @"C:\Windows\System32\cmd.exe /c C:\Users\me\av.bat"));
        pc.ServiceList.Add(new("Guard", "Guard", "", "\"C:\\ProgramData\\svc\\guard.exe\"", 0x10, 2, false, "LocalSystem", []));

        var entries = StartupScan.Run(pc).Entries.ToDictionary(x => x.Key);

        Assert.All(entries.Values, x => Assert.True(x.CanChange, x.Reason));
        Assert.Equal(StartupRisk.Careful, entries["Windows Defender"].Risk);
        Assert.Contains("security software", entries["Windows Defender"].Reason);
        Assert.Equal(StartupRisk.Careful, entries["Guard"].Risk);
        Assert.Contains("from Microsoft", entries["Guard"].Reason);
        Assert.Contains("script", entries["Avast Service"].Reason);
    }

    [Fact]
    public void ASignedSecurityProductAndAStoreAppKeepTheirProtection()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Program Files\Avast Software\Avast\avastui.exe", "Avast Software");
        pc.Runs.Add(new(StartupScope.Machine, false, "Shield", @"C:\Program Files\Avast Software\Avast\avastui.exe"));
        pc.Store.Add(new("Microsoft.Defender_8wekyb3d8bbwe", "Boot", 2, "", "Microsoft Defender", "Microsoft Corporation", ""));

        Assert.All(StartupScan.Run(pc).Entries, x => Assert.Equal(StartupRisk.Protected, x.Risk));
    }

    [Fact]
    public void AFileInACornerOfWindowsThatAnyProgramCanWriteToIsNotPartOfWindows()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Windows\Temp\helper.exe", "Microsoft Corporation", StartupTrust.Unsigned);
        pc.Add(@"C:\Windows\Tasks\svc.exe", "", StartupTrust.Unsigned);
        pc.Runs.Add(new(StartupScope.Machine, false, "Helper", @"C:\Windows\Temp\helper.exe"));
        pc.ServiceList.Add(new("Lurker", "Lurker", "", "\"C:\\Windows\\Tasks\\svc.exe\"", 0x10, 2, false, "LocalSystem", []));

        var result = StartupScan.Run(pc);
        var entries = result.Entries.ToDictionary(x => x.Key);

        Assert.True(entries["Helper"].CanChange);
        Assert.Contains("could not be verified", entries["Helper"].Reason);
        // The service is listed, not hidden among Windows' own.
        Assert.True(entries["Lurker"].CanChange);
        Assert.Equal(0, result.WindowsServices);
        Assert.True(pc.Places.InScratch(@"C:\Windows\System32\spool\drivers\color\x.exe"));
        Assert.False(pc.Places.InScratch(@"C:\Windows\System32\x.exe"));
    }

    [Theory]
    [InlineData("S-1-5-18", true)]
    [InlineData("NT AUTHORITY\\SYSTEM", true)]
    [InlineData("LocalSystem", true)]
    [InlineData("S-1-5-19", false)]
    [InlineData("NT AUTHORITY\\NETWORK SERVICE", false)]
    public void ATaskRunningAsThePcsOwnAccountIsWindowsWideWhateverSpellingTaskSchedulerUsed(string runAs, bool fullRights)
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Apps\boot.exe");
        pc.TaskList.Add(new(@"\Boot", "Boot", "", "Acme", true, @"C:\Apps\boot.exe", "", false, true, runAs));

        var entry = Only(pc);

        Assert.Equal(StartupScope.Machine, entry.Scope);
        Assert.Equal(@"task|machine|\Boot", entry.Id);
        Assert.Equal(fullRights, entry.Reason.Contains("full system rights"));
    }

    [Theory]
    [InlineData("DESKTOP\\SYSTEMADMIN")]
    [InlineData("me")]
    [InlineData("S-1-5-21-1-2-3-1001")]
    [InlineData("BUILTIN\\Users")]
    [InlineData("")]
    public void ATaskRunningAsAPersonOrAGroupIsTheirs(string runAs)
    {
        Assert.False(StartupGuard.IsSystemAccount(runAs));
        Assert.False(StartupGuard.IsLocalSystem(runAs));
        Assert.Equal(StartupScope.User, StartupGuard.TaskScope(runAs));
    }

    [Fact]
    public void FindingOneItemReadsOnlyThePlaceItLivesInAndOpensOnlyItsOwnFile()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Apps\a.exe"); pc.Add(@"C:\Apps\b.exe"); pc.Add(@"C:\Apps\svc.exe");
        pc.Runs.Add(new(StartupScope.User, false, "A", @"C:\Apps\a.exe"));
        pc.Runs.Add(new(StartupScope.User, false, "B", @"C:\Apps\b.exe"));
        pc.TaskList.Add(new(@"\T", "T", "", "Acme", true, @"C:\Apps\b.exe", "", true, false, "me"));
        pc.ServiceList.Add(new("Acme", "Acme", "", "\"C:\\Apps\\svc.exe\"", 0x10, 2, false, "LocalSystem", []));
        pc.Reads.Clear();

        var found = StartupScan.Find(pc, "run|user|Run|A");

        Assert.Equal("A", found!.Name);
        Assert.Equal(["runs", @"describe C:\Apps\a.exe"], pc.Reads);

        pc.Reads.Clear();
        Assert.NotNull(StartupScan.Find(pc, "service|machine|Acme"));
        Assert.Equal(["services", @"describe C:\Apps\svc.exe"], pc.Reads);

        pc.Reads.Clear();
        Assert.Null(StartupScan.Find(pc, @"task|user|\Nope"));
        Assert.Equal(["tasks"], pc.Reads);
    }

    [Fact]
    public void PuttingBackAnItemWhoseNameAnotherProgramHasTakenIsRefusedAndTheCopyKept()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Apps\setup.exe"); pc.Add(@"C:\Users\me\evil.exe", "", StartupTrust.Unsigned);
        pc.Runs.Add(new(StartupScope.User, false, "Setup", @"C:\Apps\setup.exe"));
        var changer = new LocalStartupChanger(pc, pc);
        var id = Only(pc).Id;
        var removed = changer.Change(Remove(id));
        Assert.True(removed.Ok, removed.Error);

        pc.Runs.Add(new(StartupScope.User, false, "Setup", @"C:\Users\me\evil.exe"));
        var refused = changer.Change(Restore(id, removed.Vault));

        Assert.False(refused.Ok);
        Assert.Contains("taken this item's place", refused.Error);
        Assert.Contains(@"C:\Users\me\evil.exe", refused.Error);
        Assert.DoesNotContain(pc.Writes, x => x.StartsWith("restore", StringComparison.Ordinal));

        // The same item, back by hand: nothing to write, and the copy is no longer needed.
        pc.Runs.Clear();
        pc.Runs.Add(new(StartupScope.User, false, "Setup", @"C:\Apps\setup.exe"));
        Assert.True(changer.Change(Restore(id, removed.Vault)).Ok);
        Assert.DoesNotContain(pc.Writes, x => x.StartsWith("restore", StringComparison.Ordinal));
    }

    [Fact]
    public void ATaskDefinitionMustRunAsTheKindOfAccountItsItemWasListedUnder()
    {
        const string ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.Null(StartupGuard.TaskDefinitionProblem(UserTask, StartupScope.User));
        Assert.Null(StartupGuard.TaskDefinitionProblem(SystemTask, StartupScope.Machine));
        Assert.Contains("different kind of account", StartupGuard.TaskDefinitionProblem(SystemTask, StartupScope.User));
        Assert.Contains("different kind of account", StartupGuard.TaskDefinitionProblem(UserTask, StartupScope.Machine));
        // A group, or no principal at all, means whoever signs in.
        Assert.Null(StartupGuard.TaskDefinitionProblem(UserTask.Replace("<UserId>S-1-5-21-1-2-3-1001</UserId>", "<GroupId>S-1-5-32-545</GroupId>"), StartupScope.User));
        Assert.Null(StartupGuard.TaskDefinitionProblem($"<Task xmlns=\"{ns}\"/>", StartupScope.User));
        // Anything that is not a task definition: no namespace, broken, empty, or carrying a document type definition.
        Assert.Contains("not a task definition", StartupGuard.TaskDefinitionProblem("<Task>sync</Task>", StartupScope.User));
        Assert.Contains("not a task definition", StartupGuard.TaskDefinitionProblem($"<Task xmlns=\"{ns}\">", StartupScope.User));
        Assert.Contains("not a task definition", StartupGuard.TaskDefinitionProblem($"<!DOCTYPE Task [<!ENTITY x \"S-1-5-18\">]><Task xmlns=\"{ns}\"><Principals><Principal><UserId>&x;</UserId></Principal></Principals></Task>", StartupScope.Machine));
        Assert.Contains("empty", StartupGuard.TaskDefinitionProblem("", StartupScope.User));
        Assert.Contains("too large", StartupGuard.TaskDefinitionProblem(new string(' ', StartupGuard.LargestTaskDefinition + 1), StartupScope.User));
    }

    [Fact]
    public void APutBackRefusesACopyThatWouldRegisterASystemTaskUnderAPersonsItemWhoeverKeptIt()
    {
        var pc = new FakeStartupPc();
        var forged = new StartupVault(StartupKind.ScheduledTask, StartupScope.User, "Task", @"\Sync", Data: SystemTask);
        var request = Restore(@"task|user|\Sync", forged);

        Assert.Contains("different kind of account", new LocalStartupChanger(pc, pc).Change(request).Error);

        // Even the helper's own protected copy is checked again before it is written.
        var store = new ProtectedStore("startup", root, enforce: false);
        store.Save(@"task|user|\Sync", forged);
        Assert.Contains("different kind of account", new LocalStartupChanger(pc, pc, store).Change(request with { Vault = null }).Error);
        Assert.Empty(pc.Writes);
    }

    [Fact]
    public void AFileWithAShortcutsNameHasToBeAShortcutToBeTakenOutOrPutBack()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Apps\a.exe");
        pc.FolderBytes["A.lnk"] = [77, 90, 1, 2];
        pc.Folder.Add(new(StartupScope.User, "A.lnk", @"C:\Startup\A.lnk", @"C:\Apps\a.exe", ""));
        var changer = new LocalStartupChanger(pc, pc);

        var outcome = changer.Change(Remove("folder|user|A.lnk"));

        Assert.False(outcome.Ok);
        Assert.Contains("not one", outcome.Error);
        Assert.Contains("left alone", outcome.Error);
        Assert.Single(pc.Folder);

        var forged = new StartupVault(StartupKind.StartupFolder, StartupScope.Machine, "StartupFolder", "B.lnk", Data: Convert.ToBase64String(new byte[] { 77, 90, 1, 2 }));
        var refused = changer.Change(Restore("folder|machine|B.lnk", forged));
        Assert.False(refused.Ok);
        Assert.Contains("not put back", refused.Error);
        Assert.DoesNotContain(pc.Writes, x => x.StartsWith("restore-folder Machine", StringComparison.Ordinal));

        Assert.Null(StartupGuard.FolderItemProblem("Tool.bat", "@echo off"u8));
        Assert.Null(StartupGuard.FolderItemProblem("A.lnk", Shortcut));
        Assert.NotNull(StartupGuard.FolderItemProblem("A.lnk", []));
    }

    [Theory]
    [InlineData(@"\Acme\Sync", null)]
    [InlineData(@"\Sync", null)]
    [InlineData(@"\Microsoft\Office\Update", null)]
    [InlineData(@"\Microsoft\Windows\Defrag\ScheduledDefrag", "Windows")]
    [InlineData("Sync", "not valid")]
    [InlineData(@"\Acme\", "not valid")]
    [InlineData(@"\Acme\\Sync", "not valid")]
    [InlineData(@"\Acme\..\Sync", "not valid")]
    [InlineData("\\Acme\\Sy\nnc", "not valid")]
    [InlineData("\\Acme\\Sync ", "not valid")]
    [InlineData(@"\Acme\Sy|nc", "not valid")]
    public void OnlyPlainTaskPathsOutsideWindowsOwnFolderMayBeChanged(string path, string? problem)
    {
        var found = StartupGuard.TaskPathProblem(path);
        if (problem is null) Assert.Null(found);
        else Assert.Contains(problem, found);
    }

    [Fact]
    public void ASignedRuntimeFileStillChecksOutWhenRevocationIsLookedAt()
    {
        if (!OperatingSystem.IsWindows()) return;
        var file = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "coreclr.dll");
        if (!File.Exists(file)) return;
        var (trust, signer) = WindowsStartupSystem.Verify(file);
        // A runtime built without a signature cannot say anything here.
        if (trust == StartupTrust.Unsigned) return;
        Assert.Equal(StartupTrust.Verified, trust);
        Assert.Contains("Microsoft", signer);
    }
}
