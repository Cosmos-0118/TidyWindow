using System.Runtime.Versioning;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

/// <summary>An in-memory PC for the shortcut checks: a Start menu, a Desktop and a machine-wide Start menu, laid out as plain paths.</summary>
public sealed class LinkPc : IInstallSystem
{
    public const string User = @"C:\U\Start Menu\Programs", Machine = @"C:\PD\Start Menu\Programs", Desktop = @"C:\U\Desktop";
    public List<UninstallEntry> Programs = [];
    public HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase), Folders = new(StringComparer.OrdinalIgnoreCase), Gone = new(StringComparer.OrdinalIgnoreCase), MissingDrives = new(StringComparer.OrdinalIgnoreCase);
    public List<ShortcutFile> Links = [];
    public List<ShortcutFolder> Dirs = [];
    public List<string> Notified = [];
    public HashSet<string> KnownProducts = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ShortcutRoot> ShortcutRoots() => [new(User, ShortcutArea.StartMenu, false), new(Machine, ShortcutArea.StartMenu, true), new(Desktop, ShortcutArea.Desktop, false)];
    public IReadOnlyList<ShortcutFile> Shortcuts() => Links;
    public IReadOnlyList<ShortcutFolder> ShortcutFolders() => Dirs;
    public void ShellChanged(IReadOnlyList<string> paths) => Notified.AddRange(paths);

    public IReadOnlyList<UninstallEntry> Entries() => Programs;
    public IReadOnlyList<MsiProduct> Products() => [];
    public int ProductState(string code) => KnownProducts.Contains(code) ? 5 : InstallRepair.Unknown;
    public bool FileExists(string path) => Files.Contains(path);
    public bool FolderExists(string path) => Folders.Contains(path);
    public bool DriveAvailable(string path) => !MissingDrives.Contains(Path.GetPathRoot(path) ?? "");
    public string? FindOnPath(string fileName) => null;
    public string Expand(string text) => text.Replace("%ProgramFiles%", @"C:\Program Files");
    public bool InstallLockExists() => false;
    public bool InstallerBusy() => false;
    public IReadOnlyList<RunningInstaller> Installers() => [];
    public IReadOnlyList<string> PendingRestart() => [];
    public bool EndProcess(int id) => true;

    public ShortcutFile Link(string path, string target, string arguments = "") { var link = new ShortcutFile(path, target, arguments); Links.Add(link); return link; }
}

/// <summary>Stands in for the real file editor: shortcuts and folders are entries in a set, and a copy of each removed one is kept like the real one does.</summary>
public sealed class MemoryLinks(HashSet<string> present) : IShortcutEditor
{
    public Func<string, bool> FailRemove = _ => false, FailRestore = _ => false;
    public List<string> Removed = [];
    public IReadOnlyList<ShortcutBackup> RemoveShortcut(string path)
    {
        if (FailRemove(path)) throw new IOException("Windows said no.");
        if (!present.Remove(path)) return [];
        Removed.Add(path);
        return [new(path, path.EndsWith(".lnk") || path.EndsWith(".url") ? Convert.ToBase64String("link"u8.ToArray()) : null)];
    }
    public void RestoreShortcut(ShortcutBackup backup)
    {
        if (FailRestore(backup.Path)) throw new IOException("Windows said no.");
        present.Add(backup.Path);
    }
}

[SupportedOSPlatform("windows")]
public sealed class StaleShortcutTests : IDisposable
{
    private readonly RepairSandbox sandbox = new();
    private readonly string folder = Path.Combine(Path.GetTempPath(), "tw-links-" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose() { sandbox.Dispose(); try { Directory.Delete(folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

    private static string Start(string app, string name) => $@"{LinkPc.User}\{app}\{name}";

    private static ShortcutGroup Only(LinkPc pc) => Assert.Single(InstallRepair.StaleShortcuts(pc));

    // ---- Which shortcuts count as broken ----

    [Fact]
    public void AShortcutWhoseTargetIsGoneIsBrokenAndOneWhoseTargetIsThereIsNot()
    {
        var pc = new LinkPc();
        pc.Files.Add(@"C:\Program Files\Good\good.exe");
        pc.Link(Start("Good", "Good.lnk"), @"C:\Program Files\Good\good.exe");
        pc.Link(Start("Gone", "Gone.lnk"), @"C:\Program Files\Gone\gone.exe");

        var group = Only(pc);

        Assert.Equal("Gone", group.Name);
        Assert.Equal([Start("Gone", "Gone.lnk")], group.Links.Select(x => x.File.Path));
    }

    [Fact]
    public void AShortcutToAFolderThatIsThereIsNotBroken()
    {
        var pc = new LinkPc();
        pc.Folders.Add(@"C:\Games");
        pc.Link(Start("Games", "Games.lnk"), @"C:\Games");
        Assert.Empty(InstallRepair.StaleShortcuts(pc));
    }

    [Fact]
    public void AProgramInTheOtherProgramFilesFolderIsFound()
    {
        var pc = new LinkPc();
        pc.Files.Add(@"C:\Program Files (x86)\Old\old.exe");
        pc.Link(Start("Old", "Old.lnk"), @"C:\Program Files\Old\old.exe");
        Assert.Empty(InstallRepair.StaleShortcuts(pc));
    }

    [Fact]
    public void VariablesAreExpandedBeforeTheTargetIsChecked()
    {
        var pc = new LinkPc();
        pc.Files.Add(@"C:\Program Files\Tool\tool.exe");
        pc.Link(Start("Tool", "Tool.lnk"), @"%ProgramFiles%\Tool\tool.exe");
        Assert.Empty(InstallRepair.StaleShortcuts(pc));
    }

    [Fact]
    public void AShortcutOnADriveThatIsNotPluggedInIsLeftAlone()
    {
        var pc = new LinkPc();
        pc.MissingDrives.Add(@"E:\");
        pc.Link(Start("Portable", "Portable.lnk"), @"E:\Apps\portable.exe");
        Assert.Empty(InstallRepair.StaleShortcuts(pc));
    }

    [Theory]
    [InlineData(@"\\server\share\tool.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\Vendor.App_1.0_x64__abc\app.exe")]
    [InlineData("shell:AppsFolder\\Vendor.App")]
    [InlineData("notepad.exe")]
    public void TargetsThatCannotBeJudgedAreNeverCalledBroken(string target)
    {
        var pc = new LinkPc();
        pc.Link(Start("Odd", "Odd.lnk"), target);
        Assert.Empty(InstallRepair.StaleShortcuts(pc));
    }

    [Fact]
    public void AShortcutWithNoFilePathIsFineWhenItNamesAnAppOrPointsIntoTheShellAndBrokenOnlyWhenItIsEmpty()
    {
        var pc = new LinkPc();
        pc.Links.Add(new(Start("Explorer", "File Explorer.lnk"), AppId: "Microsoft.Windows.Explorer"));
        pc.Links.Add(new(Start("Control", "Control Panel.lnk"), ShellTarget: true));
        pc.Links.Add(new($@"{LinkPc.User}\Windows PowerShell\New shortcut.lnk"));

        var group = Only(pc);

        Assert.Equal("Windows PowerShell", group.Name);
        Assert.True(group.Review, "A shortcut in a Windows folder is only offered.");
    }

    [Fact]
    public void AShortcutThatStartsAProgramWindowsInstallerNoLongerHasIsBrokenAndOneItStillHasIsNot()
    {
        var pc = new LinkPc();
        const string gone = "{DF9D7290-4411-4C78-A247-DD1E35A0C913}", there = "{13FB33E3-5BEC-416A-BB79-8C7EF529BCC1}";
        pc.KnownProducts.Add(there);
        pc.Files.Add(@"C:\Windows\System32\msiexec.exe");
        pc.Link(Start("CMake", "Uninstall CMake.lnk"), @"C:\Windows\System32\msiexec.exe", $"/x {gone}");
        pc.Link(Start("Go", "Uninstall Go.lnk"), @"C:\Windows\System32\msiexec.exe", $"/x {there}");
        pc.Links.Add(new(Start("Advertised", "Tool.lnk"), Product: gone));

        var found = InstallRepair.StaleShortcuts(pc);

        Assert.Equal(["Advertised", "CMake"], found.Select(x => x.Name).Order());
    }

    [Fact]
    public void AToolPromptWhoseProgramFolderIsGoneIsBrokenEvenThoughCmdIsThere()
    {
        var pc = new LinkPc();
        pc.Files.Add(@"C:\Windows\System32\cmd.exe");
        pc.Folders.Add(@"C:\Rust\Present");
        pc.Link(Start("Rust", "Rust 1.91 Shell.lnk"), @"C:\Windows\System32\cmd.exe", @"/K path C:\Rust\Gone\bin;%PATH%");
        pc.Link(Start("Rust2", "Rust Present Shell.lnk"), @"C:\Windows\System32\cmd.exe", @"/K path C:\Rust\Present;%PATH%");

        Assert.Equal("Rust", Only(pc).Name);
    }

    [Fact]
    public void AScriptRunByAHostIsCheckedNotTheHost()
    {
        var pc = new LinkPc();
        pc.Files.Add(@"C:\Windows\System32\wscript.exe");
        pc.Files.Add(@"C:\Tools\present.vbs");
        pc.Link(Start("Script", "Gone.lnk"), @"C:\Windows\System32\wscript.exe", @"""C:\Tools\gone.vbs""");
        pc.Link(Start("Script2", "Present.lnk"), @"C:\Windows\System32\wscript.exe", @"""C:\Tools\present.vbs""");

        Assert.Equal("Script", Only(pc).Name);
    }

    [Fact]
    public void AnUnreadableOneDrivePlaceholderIsBrokenButOnlyOffered()
    {
        var pc = new LinkPc();
        pc.Links.Add(new($@"{LinkPc.Desktop}\Old.lnk", Unreadable: true));

        var group = Only(pc);

        Assert.True(group.Review);
        var finding = Assert.Single(InstallRepair.Scan(pc).Findings);
        Assert.Equal(RepairLevel.Review, finding.Level);
        Assert.Contains("OneDrive", finding.Detail);
    }

    [Fact]
    public void AWebLinkIsLeftAloneButAFileLinkToAMissingFileIsBroken()
    {
        var pc = new LinkPc();
        pc.Links.Add(new($@"{LinkPc.Desktop}\Web.url", Url: "https://example.com"));
        pc.Links.Add(new($@"{LinkPc.Desktop}\Game.url", Url: "steam://rungameid/1"));
        pc.Links.Add(new($@"{LinkPc.Desktop}\Doc.url", Url: "file:///C:/Gone/doc.txt"));

        Assert.Equal(@"C:\U\Desktop\Doc.url", Assert.Single(Only(pc).Links).File.Path);
    }

    [Fact]
    public void AFileThatCouldNotBeReadIsNeverJudged()
    {
        var pc = new LinkPc();
        pc.Links.Add(new($@"{LinkPc.Desktop}\Odd.lnk", Unparsed: true));
        Assert.Empty(InstallRepair.StaleShortcuts(pc));
    }

    // ---- Companion links and empty folders ----

    [Fact]
    public void WhenEveryProgramShortcutInAFolderIsBrokenItsWebLinksAndTheFolderGoWithThem()
    {
        var pc = new LinkPc();
        var folder = $@"{LinkPc.User}\Tesseract";
        pc.Link($@"{folder}\Tesseract.lnk", @"C:\Gone\t.exe");
        pc.Links.Add(new($@"{folder}\Homepage.url", Url: "https://example.com"));
        pc.Dirs.Add(new(folder, [$@"{folder}\Tesseract.lnk", $@"{folder}\Homepage.url"], []));

        var group = Only(pc);

        Assert.Equal(2, group.Links.Count);
        Assert.Contains(group.Links, x => x.Companion);
        Assert.Equal([folder], group.Folders);
        Assert.Equal(RepairKind.BrokenShortcut, Assert.Single(InstallRepair.Scan(pc).Findings).Kind);
    }

    [Fact]
    public void AWebLinkMadeAsAShortcutFileCountsAsACompanionToo()
    {
        var pc = new LinkPc();
        var folder = $@"{LinkPc.User}\Tesseract-OCR";
        pc.Link($@"{folder}\Console.lnk", @"C:\Gone\winpath.exe");
        pc.Links.Add(new($@"{folder}\FAQ.lnk", ShellTarget: true));
        pc.Dirs.Add(new(folder, [$@"{folder}\Console.lnk", $@"{folder}\FAQ.lnk"], []));

        var group = Only(pc);

        Assert.Equal(2, group.Links.Count);
        Assert.Equal([folder], group.Folders);
    }

    [Fact]
    public void AShellShortcutAloneInAFolderIsNotTouched()
    {
        var pc = new LinkPc();
        pc.Links.Add(new($@"{LinkPc.User}\Tools\Control Panel.lnk", ShellTarget: true));
        Assert.Empty(InstallRepair.StaleShortcuts(pc));
    }

    [Fact]
    public void ALinkToAHealthyProgramKeepsTheFolderAndItsWebLinks()
    {
        var pc = new LinkPc();
        var folder = $@"{LinkPc.User}\Suite";
        pc.Files.Add(@"C:\Suite\good.exe");
        pc.Link($@"{folder}\Good.lnk", @"C:\Suite\good.exe");
        pc.Link($@"{folder}\Old.lnk", @"C:\Suite\old.exe");
        pc.Links.Add(new($@"{folder}\Homepage.url", Url: "https://example.com"));
        pc.Dirs.Add(new(folder, [$@"{folder}\Good.lnk", $@"{folder}\Old.lnk", $@"{folder}\Homepage.url"], []));

        var group = Only(pc);

        Assert.Equal([$@"{folder}\Old.lnk"], group.Links.Select(x => x.File.Path));
        Assert.Empty(group.Folders);
    }

    [Fact]
    public void AnEmptyFolderIsFoundButWindowsOwnFoldersAreKept()
    {
        var pc = new LinkPc();
        foreach (var name in new[] { "Blender", "Accessories", "Startup", "Windows PowerShell" })
            pc.Dirs.Add(new($@"{LinkPc.User}\{name}", [], []));
        pc.Dirs.Add(new($@"{LinkPc.User}\Python", [$@"{LinkPc.User}\Python\desktop.ini"], []));

        var found = InstallRepair.StaleShortcuts(pc);

        Assert.Equal(["Blender", "Python"], found.Select(x => x.Name).Order());
        Assert.All(found, x => Assert.Empty(x.Links));
        var finding = InstallRepair.Scan(pc).Findings.First(x => x.Title.StartsWith("Blender"));
        Assert.Equal(RepairKind.EmptyFolder, finding.Kind);
        Assert.Equal(RepairLevel.Recommended, finding.Level);
    }

    [Fact]
    public void AFolderEmptiedByTheSweepIsRemovedDeepestFirstAndOneWithOtherContentIsKept()
    {
        var pc = new LinkPc();
        var tools = $@"{LinkPc.User}\VS\Tools";
        var inner = $@"{tools}\VC";
        pc.Dirs.Add(new(inner, [], []));
        pc.Dirs.Add(new(tools, [$@"{tools}\readme.txt"], [inner]));
        pc.Dirs.Add(new($@"{LinkPc.User}\VS", [], [tools]));

        var group = Only(pc);

        Assert.Equal([inner], group.Folders);
    }

    [Fact]
    public void NestedEmptyFoldersAreRemovedTogetherDeepestFirst()
    {
        var pc = new LinkPc();
        pc.Dirs.Add(new($@"{LinkPc.User}\A\B", [], []));
        pc.Dirs.Add(new($@"{LinkPc.User}\A", [], [$@"{LinkPc.User}\A\B"]));

        Assert.Equal([$@"{LinkPc.User}\A\B", $@"{LinkPc.User}\A"], Only(pc).Folders);
    }

    [Fact]
    public void BrokenShortcutsInTheAllUsersMenuNeedTheHelperAndTheirOwnFindingsAreSeparate()
    {
        var pc = new LinkPc();
        pc.Link($@"{LinkPc.Machine}\Gone\Gone.lnk", @"C:\Gone\g.exe");
        pc.Link($@"{LinkPc.User}\Mine\Mine.lnk", @"C:\Gone\m.exe");

        var findings = InstallRepair.Scan(pc).Findings;

        Assert.True(findings.Single(x => x.Title.StartsWith("Gone")).NeedsAdmin);
        Assert.False(findings.Single(x => x.Title.StartsWith("Mine")).NeedsAdmin);
    }

    // ---- Fixing and undoing ----

    private RepairService Service(LinkPc pc, MemoryLinks user, MemoryLinks machine, int[]? unlocks = null) =>
        new(pc, sandbox.User(), sandbox.Elevated(), _ => { if (unlocks is not null) unlocks[0]++; return Task.CompletedTask; }, () => { }, new RepairHistory(folder), userLinks: user, machineLinks: machine);

    [Fact]
    public async Task RemovingStaleShortcutsKeepsACopyTellsWindowsAndUndoPutsEverythingBack()
    {
        var pc = new LinkPc();
        var dir = $@"{LinkPc.User}\Gone";
        var lnk = pc.Link($@"{dir}\Gone.lnk", @"C:\Gone\g.exe").Path;
        pc.Dirs.Add(new(dir, [lnk], []));
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { lnk, dir };
        var user = new MemoryLinks(present);
        var finding = Assert.Single(InstallRepair.Scan(pc).Findings);
        var service = Service(pc, user, new MemoryLinks([]));

        var outcome = await service.FixAsync([finding], "Removed leftovers");

        Assert.Equal(RepairStatus.Fixed, Assert.Single(outcome.Results!).Status);
        Assert.Empty(present);
        Assert.Equal([lnk, dir], pc.Notified);
        Assert.NotNull(outcome.Batch);
        Assert.Equal(2, outcome.Batch!.Shortcuts.Count);

        var undo = await service.UndoAsync(outcome.Batch);

        Assert.True(undo.Complete, string.Join(" ", undo.Problems));
        Assert.Equal(1, undo.ShortcutsRestored);
        Assert.Equal([dir, lnk], present.Order());
        Assert.Null(new RepairHistory(folder).Latest());
    }

    [Fact]
    public async Task AllUsersShortcutsGoThroughTheHelperAfterThePermissionPrompt()
    {
        var pc = new LinkPc();
        var lnk = pc.Link($@"{LinkPc.Machine}\Gone\Gone.lnk", @"C:\Gone\g.exe").Path;
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { lnk };
        var machine = new MemoryLinks(present);
        var user = new MemoryLinks([]);
        var unlocks = new[] { 0 };

        var outcome = await Service(pc, user, machine, unlocks).FixAsync([Assert.Single(InstallRepair.Scan(pc).Findings)], "Removed");

        Assert.Equal(1, unlocks[0]);
        Assert.Equal([lnk], machine.Removed);
        Assert.Empty(user.Removed);
        Assert.NotNull(outcome.Batch);
    }

    [Fact]
    public async Task AFailurePartWayPutsBackWhatWasAlreadyRemoved()
    {
        var pc = new LinkPc();
        var a = pc.Link($@"{LinkPc.User}\App\A.lnk", @"C:\Gone\a.exe").Path;
        var b = pc.Link($@"{LinkPc.User}\App\B.lnk", @"C:\Gone\b.exe").Path;
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { a, b };
        var user = new MemoryLinks(present) { FailRemove = path => path == b };

        var outcome = await Service(pc, user, new MemoryLinks([])).FixAsync([Assert.Single(InstallRepair.Scan(pc).Findings)], "Removed");

        Assert.Equal(RepairStatus.NotFixed, Assert.Single(outcome.Results!).Status);
        Assert.Equal([a, b], present.Order());
        Assert.Null(outcome.Batch);
    }

    [Fact]
    public async Task AnUndoThatCannotPutOneBackTakesBackWhatItRestoredAndKeepsTheRepair()
    {
        var pc = new LinkPc();
        var a = pc.Link($@"{LinkPc.User}\App\A.lnk", @"C:\Gone\a.exe").Path;
        var b = pc.Link($@"{LinkPc.User}\App\B.lnk", @"C:\Gone\b.exe").Path;
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { a, b };
        var user = new MemoryLinks(present);
        var service = Service(pc, user, new MemoryLinks([]));
        var outcome = await service.FixAsync([Assert.Single(InstallRepair.Scan(pc).Findings)], "Removed");
        user.FailRestore = path => path == a;

        var undo = await service.UndoAsync(outcome.Batch!);

        Assert.False(undo.Complete);
        Assert.Empty(present);
        Assert.NotNull(new RepairHistory(folder).Latest());
    }

    [Fact]
    public async Task RemovingAProgramsRecordAlsoRemovesItsBrokenShortcutsButNeverAWorkingOneOrAnotherProgramsBrokenOne()
    {
        var pc = new LinkPc();
        pc.Programs = [new(@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Ghost", "Ghost App", "1.0", "Maker", @"""C:\Program Files\Ghost\unins000.exe""", "", @"C:\Program Files\Ghost", "", false, false, false, false)];
        pc.Files.Add(@"C:\Program Files\Other\other.exe");
        var own = pc.Link($@"{LinkPc.User}\Ghost App\Ghost App.lnk", @"C:\Program Files\Ghost\ghost.exe").Path;
        var ownDir = $@"{LinkPc.User}\Ghost App";
        pc.Dirs.Add(new(ownDir, [own], []));
        var working = pc.Link($@"{LinkPc.User}\Other\Other.lnk", @"C:\Program Files\Other\other.exe").Path;
        var stranger = pc.Link($@"{LinkPc.User}\Stranger\Stranger.lnk", @"C:\Program Files\Stranger\s.exe").Path;
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { own, ownDir, working, stranger };
        var user = new MemoryLinks(present);
        var entryFinding = Assert.Single(InstallRepair.Scan(pc).Findings, x => x.Kind == RepairKind.BrokenEntry);
        sandbox.Put(entryFinding.Keys[0], ("DisplayName", "Ghost App"));

        var outcome = await Service(pc, user, new MemoryLinks([])).FixAsync([entryFinding], "Repaired Ghost App");

        Assert.Equal(RepairStatus.Fixed, Assert.Single(outcome.Results!).Status);
        Assert.DoesNotContain(own, present);
        Assert.DoesNotContain(ownDir, present);
        Assert.Contains(working, present);
        Assert.Contains(stranger, present);
        Assert.Equal(2, outcome.Batch!.Shortcuts.Count);
    }

    [Fact]
    public async Task ASweepOfTheAllUsersMenuWaitsForTheNextScanWhenTheHelperWasNotApproved()
    {
        var pc = new LinkPc();
        pc.Programs = [new(@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Ghost", "Ghost App", "1.0", "Maker", @"""C:\Program Files\Ghost\unins000.exe""", "", @"C:\Program Files\Ghost", "", false, false, false, false)];
        var shared = pc.Link($@"{LinkPc.Machine}\Ghost App\Ghost App.lnk", @"C:\Program Files\Ghost\ghost.exe").Path;
        var machine = new MemoryLinks(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { shared });
        var entry = new RepairFinding("entry:x", RepairKind.BrokenEntry, "Ghost App", "", "k", RepairLevel.Recommended, [pc.Programs[0].Key], Entry: pc.Programs[0]);
        sandbox.Put(pc.Programs[0].Key, ("DisplayName", "Ghost App"));
        // The entry lives under HKCU, so no permission prompt is needed and the helper stays closed.
        var outcome = await Service(pc, new MemoryLinks([]), machine).FixAsync([entry], "Repaired Ghost App");

        Assert.Equal(RepairStatus.Fixed, Assert.Single(outcome.Results!).Status);
        Assert.Empty(machine.Removed);
        Assert.Contains(Assert.Single(outcome.Results!).Steps, x => x.Contains("all-users Start menu"));
    }

    [Fact]
    public void TheFileEditorKeepsEveryByteAndRefusesPlacesItWasNotGiven()
    {
        Directory.CreateDirectory(folder);
        var inside = Path.Combine(folder, "Menu");
        Directory.CreateDirectory(inside);
        var editor = new FileShortcutEditor(path => ShortcutPlaces.Under(path, inside));
        var file = Path.Combine(inside, "App", "App.lnk");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var bytes = Enumerable.Range(0, 300).Select(x => (byte)(x * 7)).ToArray();
        File.WriteAllBytes(file, bytes);
        File.SetAttributes(file, FileAttributes.ReadOnly);
        File.WriteAllText(Path.Combine(inside, "App", "desktop.ini"), "[.ShellClassInfo]");

        var removed = editor.RemoveShortcut(file).Concat(editor.RemoveShortcut(Path.GetDirectoryName(file)!)).ToList();

        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(Path.GetDirectoryName(file)));
        Assert.Equal(3, removed.Count);
        foreach (var copy in Enumerable.Reverse(removed)) editor.RestoreShortcut(copy);
        Assert.Equal(bytes, File.ReadAllBytes(file));
        Assert.True(File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly));
        File.SetAttributes(file, FileAttributes.Normal);
        Assert.True(File.Exists(Path.Combine(inside, "App", "desktop.ini")));

        var outside = Path.Combine(folder, "keep.lnk");
        File.WriteAllText(outside, "x");
        Assert.Throws<IOException>(() => editor.RemoveShortcut(outside));
        Assert.True(File.Exists(outside));
        var document = Path.Combine(inside, "notes.txt");
        File.WriteAllText(document, "x");
        Assert.Throws<IOException>(() => editor.RemoveShortcut(document));
        Assert.Throws<IOException>(() => editor.RemoveShortcut(Path.Combine(inside, "App")));
    }

    [Fact]
    public void AFolderWithAnythingElseInItIsNeverRemoved()
    {
        Directory.CreateDirectory(folder);
        var inside = Path.Combine(folder, "Menu2");
        var app = Path.Combine(inside, "App");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "notes.txt"), "x");
        var editor = new FileShortcutEditor(path => ShortcutPlaces.Under(path, inside));

        Assert.Throws<IOException>(() => editor.RemoveShortcut(app));
        Assert.True(File.Exists(Path.Combine(app, "notes.txt")));
    }

    [Fact]
    public void WindowsFoldersAreProtectedByName()
    {
        Assert.All(new[] { "Startup", "accessories", "Windows PowerShell", "Windows Tools", "Maintenance" }, name => Assert.True(InstallRepair.IsProtectedFolder(name), name));
        Assert.False(InstallRepair.IsProtectedFolder("Python 3.12"));
    }
}
