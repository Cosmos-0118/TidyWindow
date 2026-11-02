using System.Diagnostics;
using System.Runtime.Versioning;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

/// <summary>
/// The administrator helper puts back only its own protected copies, named by token: whatever a request or the app's own history file says about values, permissions,
/// shortcut bytes or folder paths is never written with administrator rights. A forged copy changes nothing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RepairVaultTests : IDisposable
{
    private const string Key = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Ghost";
    private const string Link = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Gone\Gone.lnk";
    private readonly string root = Path.Combine(Path.GetTempPath(), "tw-vault-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly RepairVault vault;

    public RepairVaultTests() => vault = new(new ProtectedStore("repair", root, enforce: false));

    public void Dispose()
    {
        try { foreach (var junction in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Where(x => new DirectoryInfo(x).LinkTarget is not null)) Directory.Delete(junction, false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class Recorder : IKeyEditor
    {
        public List<KeyBackup> Restored = [];
        public bool IsAllowed(string path) => path.StartsWith(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\", StringComparison.OrdinalIgnoreCase);
        public bool Exists(string path) => true;
        public KeyBackup? Snapshot(string path) => new(path, [new("DisplayName", "String", Text: "Ghost")], [], "O:BAG:SYD:(A;CI;KR;;;BU)");
        public KeyBackup? Delete(string path) => Snapshot(path);
        public KeyBackup? DeleteValue(string path, string name) => new(path, [], [], ValuesOnly: true);
        public KeyBackup? SetText(string path, string name, string value) => new(path, [], [], ValuesOnly: true);
        public void Restore(KeyBackup backup) => Restored.Add(backup);
        public FootprintPlan Footprint(FootprintRequest request) => FootprintPlan.Empty;
    }

    private sealed class Links : IShortcutEditor
    {
        public List<ShortcutBackup> Restored = [];
        public IReadOnlyList<ShortcutBackup> RemoveShortcut(string path) => [new(path, Convert.ToBase64String("real shortcut"u8.ToArray()))];
        public void RestoreShortcut(ShortcutBackup backup) => Restored.Add(backup);
    }

    private sealed class Folders : IFolderEditor
    {
        public List<FolderBackup> PutBacks = [], Discards = [], TakenAgain = [];
        public FolderBackup? SetAside(string path, string vault) => new(path, Path.Combine(vault, "x", Path.GetFileName(path)));
        public void PutBack(FolderBackup backup) => PutBacks.Add(backup);
        public void TakeAgain(FolderBackup backup) => TakenAgain.Add(backup);
        public void Discard(FolderBackup backup) => Discards.Add(backup);
    }

    private sealed class Ops : IElevatedOps
    {
        public RunResult Reinstall(string msiPath, MsiProduct product, CancellationToken cancellationToken = default) => new(0, "");
        public bool StopInstallerService() => true;
        public RunResult RestoreBundleCopy(string exePath, string programName, string publisher, string targetPath) => new(0, "");
        public bool RemoveCopy(string path) => true;
    }

    private RepairSession Session(Recorder? editor = null, Links? links = null, Folders? folders = null) =>
        new(pipe => RepairHost.RunAsync([RepairHost.Command, pipe], _ => editor ?? new Recorder(), _ => new Ops(), () => true, TimeSpan.FromSeconds(20), links, folders, vault));

    // ---- Registry keys ----

    [Fact]
    public async Task TheHelperPutsBackItsOwnCopyOfAKeyNotTheValuesAndPermissionsThatArrivedWithTheRequest()
    {
        var editor = new Recorder();
        using var session = Session(editor);
        await session.OpenAsync();

        var copy = session.Delete(Key)!;
        Assert.True(RepairVault.IsToken(copy.Token));
        // The app's history file could say anything: a permission list that opens the key to everyone, values of someone else's choosing, extra subkeys.
        var forged = copy with { Sddl = "D:(A;CI;KA;;;WD)", Values = [new("UninstallString", "String", Text: @"C:\Users\me\evil.exe")], Children = [new(Key + @"\Evil", [], [])] };
        session.Restore(forged);

        var restored = Assert.Single(editor.Restored);
        Assert.Equal(Key, restored.Path);
        Assert.Null(restored.Token);
        Assert.Equal("O:BAG:SYD:(A;CI;KR;;;BU)", restored.Sddl);
        Assert.Equal("Ghost", Assert.Single(restored.Values).Text);
        Assert.Empty(restored.Children);
        // Once put back, the copy is gone: a second put-back has nothing to write.
        Assert.Throws<IOException>(() => session.Restore(copy));
        Assert.Single(editor.Restored);
    }

    [Fact]
    public async Task ACopyWithoutATokenOrWithOneTheHelperNeverGaveOutPutsNothingBack()
    {
        var editor = new Recorder();
        using var session = Session(editor);
        await session.OpenAsync();

        var ex = Assert.Throws<IOException>(() => session.Restore(new(Key, [new("DisplayName", "String", Text: "x")], [], "D:(A;CI;KA;;;WD)")));
        Assert.Contains("protected copy", ex.Message);
        Assert.Throws<IOException>(() => session.Restore(new(Key, [], [], Token: RepairVault.NewToken())));
        Assert.Throws<IOException>(() => session.Restore(new(Key, [], [], Token: @"..\..\startup\something")));

        Assert.Empty(editor.Restored);
    }

    [Fact]
    public async Task ATokenOnlyPutsBackTheKeyItWasGivenFor()
    {
        var editor = new Recorder();
        using var session = Session(editor);
        await session.OpenAsync();

        var copy = session.Delete(Key)!;
        var other = copy with { Path = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Other" };

        Assert.Throws<IOException>(() => session.Restore(other));
        Assert.Empty(editor.Restored);
        // The copy is still there for the key it belongs to.
        session.Restore(copy);
        Assert.Equal(Key, Assert.Single(editor.Restored).Path);
    }

    // ---- Shortcuts ----

    [Fact]
    public async Task TheHelperWritesBackItsOwnCopyOfAShortcutNeverTheBytesOrNameInTheRequest()
    {
        var links = new Links();
        using var session = Session(links: links);
        await session.OpenAsync();

        var copy = Assert.Single(session.RemoveShortcut(Link));
        Assert.True(RepairVault.IsToken(copy.Token));
        var evil = Convert.ToBase64String("not a shortcut"u8.ToArray());
        Assert.Throws<IOException>(() => session.RestoreShortcut(copy with { Path = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp\evil.lnk", Data = evil }));
        Assert.Throws<IOException>(() => session.RestoreShortcut(new(Link, evil)));
        Assert.Throws<IOException>(() => session.RestoreShortcut(new(Link, evil, Token: RepairVault.NewToken())));
        Assert.Empty(links.Restored);

        session.RestoreShortcut(copy with { Data = evil, Attributes = 7 });

        var restored = Assert.Single(links.Restored);
        Assert.Equal(Link, restored.Path);
        Assert.Equal("real shortcut", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(restored.Data!)));
        Assert.Equal(0, restored.Attributes);
    }

    [Fact]
    public async Task OnlyAShortcutFileIsEverWrittenBackEvenFromTheHelpersOwnCopy()
    {
        var links = new Links();
        using var session = Session(links: links);
        await session.OpenAsync();

        var copy = Assert.Single(session.RemoveShortcut(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp\evil.exe"));

        var ex = Assert.Throws<IOException>(() => session.RestoreShortcut(copy));
        Assert.Contains("Only a shortcut file", ex.Message);
        Assert.Empty(links.Restored);
    }

    // ---- Folders ----

    [Fact]
    public async Task AFolderGoesIntoTheHelpersOwnQuarantineAndComesBackOnlyFromThere()
    {
        var folders = new Folders();
        using var session = Session(folders: folders);
        await session.OpenAsync();

        var moved = session.SetAside(@"C:\Program Files\Ghost", @"C:\Users\me\AppData\Local\TidyWindow.Next\RepairBackups\SetAside")!;
        Assert.True(RepairVault.IsToken(moved.Token));
        Assert.StartsWith(vault.Quarantine + "\\" + moved.Token + "\\", moved.Stored);

        // The history file could name any folder as the one to move into Program Files; the helper moves what its own record says.
        session.PutBack(moved with { Stored = @"C:\Users\me\planted\Ghost" });
        Assert.Equal(moved.Stored, Assert.Single(folders.PutBacks).Stored);
        Assert.Throws<IOException>(() => session.PutBack(moved with { Original = @"C:\Program Files\Other" }));
        Assert.Throws<IOException>(() => session.PutBack(new(@"C:\Program Files\Ghost", @"C:\Users\me\planted\Ghost")));
        Assert.Throws<IOException>(() => session.TakeAgain(new(@"C:\Program Files\Ghost", @"C:\Users\me\planted\Ghost", RepairVault.NewToken())));
        Assert.Single(folders.PutBacks);
        Assert.Empty(folders.TakenAgain);

        // A put-back keeps the record, so a failed undo can take the folder again; a discard is the end of it.
        session.TakeAgain(moved);
        Assert.Equal(moved.Stored, Assert.Single(folders.TakenAgain).Stored);
        session.Discard(moved with { Stored = @"C:\Windows" });
        Assert.Equal(moved.Stored, Assert.Single(folders.Discards).Stored);
        // Discarding what is already gone is nothing to do, so the Quarantine tab can let it go; putting it back is not.
        session.Discard(moved);
        Assert.Single(folders.Discards);
        Assert.Throws<IOException>(() => session.PutBack(moved));
    }

    private static bool Junction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", link, target }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
        process!.WaitForExit(10000);
        return process.ExitCode == 0 && Directory.Exists(link);
    }

    [Fact]
    public void TheRealFolderEditorRefusesToMoveOrDeleteThroughALink()
    {
        var quarantine = Path.Combine(root, "SetAside");
        var editor = new FileFolderEditor(_ => true, quarantine);
        var stored = Path.Combine(quarantine, "t", "Ghost");
        var elsewhere = Path.Combine(root, "elsewhere");
        Directory.CreateDirectory(stored);
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "keep.txt"), "x");
        if (!Junction(Path.Combine(stored, "inside"), elsewhere)) return;
        var original = Path.Combine(root, "Program Files", "Ghost");

        Assert.Contains("link", Assert.Throws<IOException>(() => editor.PutBack(new(original, stored))).Message);
        Assert.False(Directory.Exists(original));
        Assert.Contains("link", Assert.Throws<IOException>(() => editor.Discard(new(original, stored))).Message);
        Assert.True(File.Exists(Path.Combine(elsewhere, "keep.txt")));
        // A stored folder outside the quarantine is never touched, whatever the record says.
        var outside = Path.Combine(root, "SetAside-not", "Ghost");
        Directory.CreateDirectory(outside);
        Assert.Throws<IOException>(() => editor.PutBack(new(original, outside)));
        Assert.Throws<IOException>(() => editor.Discard(new(original, outside)));
        Assert.True(Directory.Exists(outside));
    }

    [Fact]
    public void ARollbackFolderWithALinkInsideIsNeverUnlockedOrDeleted()
    {
        var folder = Path.Combine(root, "Config.Msi");
        var elsewhere = Path.Combine(root, "elsewhere");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(elsewhere);
        if (!Junction(Path.Combine(folder, "link"), elsewhere)) return;

        Assert.True(WindowsInstallerOps.HoldsLink(folder));
        Assert.False(WindowsInstallerOps.HoldsLink(elsewhere));
        WindowsInstallerOps.UnlockRollbackFolder(folder);
        Assert.True(Directory.Exists(Path.Combine(folder, "link")));
    }

    // ---- Setup programs in the package cache ----

    [Fact]
    public void TheHelperTakesOutOnlyTheSetupProgramsItPutInAndNeverThroughALink()
    {
        var cache = Path.Combine(root, "Package Cache");
        var ops = new WindowsInstallerOps(vault, [cache], elevated: true);
        var planted = Path.Combine(cache, "{A}", "setup.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(planted)!);
        File.WriteAllBytes(planted, [0x4D, 0x5A]);

        Assert.False(ops.RemoveCopy(planted));
        Assert.True(File.Exists(planted));

        vault.MarkStaged(planted);
        Assert.True(ops.RemoveCopy(planted));
        Assert.False(File.Exists(planted));
        Assert.False(ops.RemoveCopy(planted));

        var elsewhere = Path.Combine(root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var target = Path.Combine(elsewhere, "setup.exe");
        File.WriteAllBytes(target, [0x4D, 0x5A]);
        if (!Junction(Path.Combine(cache, "{B}"), elsewhere)) return;
        var through = Path.Combine(cache, "{B}", "setup.exe");
        vault.MarkStaged(through);
        Assert.Null(ops.CachePath(through));
        Assert.False(ops.RemoveCopy(through));
        Assert.True(File.Exists(target));
    }

    [Fact]
    public void WithAdministratorRightsAnUnsignedInstallerFileFromAFolderAnyoneCanWriteToIsNeverRun()
    {
        var msi = Path.Combine(root, "evil.msi");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(msi, new byte[64]);
        var product = new MsiProduct("{34E4C3B0-353E-4E04-88B1-EF41B1219C86}", "Machine", "", "App", null, "Contoso");

        var result = new WindowsInstallerOps(vault, elevated: true).Reinstall(msi, product);

        Assert.Equal(87, result.ExitCode);
        Assert.Contains("not signed", result.Output);
        Assert.False(InstallerTrust.InProtectedPlace(msi));
    }

    // ---- The hive and the plan ----

    [Fact]
    public void TheHelperReachesOnlyTheHiveOfTheAccountItWasStartedFor()
    {
        var repair = RegistryRepair.Elevated("S-1-5-21-1000-2000-3000-1001");
        const string key = @"\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Ghost";
        Assert.True(repair.IsAllowed("HKCU" + key));
        Assert.True(repair.IsAllowed(@"HKU\S-1-5-21-1000-2000-3000-1001" + key));
        Assert.False(repair.IsAllowed(@"HKU\S-1-5-21-1000-2000-3000-1002" + key));
        Assert.False(repair.IsAllowed(@"HKU\S-1-5-18" + key));
        Assert.True(repair.IsValueAllowed(@"HKU\S-1-5-21-1000-2000-3000-1001" + key, "UninstallString"));
        Assert.False(repair.IsValueAllowed(@"HKU\S-1-5-21-1000-2000-3000-1002" + key, "UninstallString"));
    }

    [Fact]
    public void ABundlePlanOnlyEverListsKeysARepairMayRemove()
    {
        using var sandbox = new RepairSandbox();
        const string run = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        sandbox.Put(run, ("Updater", @"C:\x.exe"));

        var plan = sandbox.Elevated().Footprint(new("bundle", Keys: [run]));

        Assert.Empty(plan.Keys);
        Assert.True(sandbox.Has(run));
    }

    // ---- A run that is cut short ----

    private sealed class Editor : IKeyEditor
    {
        public Dictionary<string, KeyBackup> Keys = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Locked = new(StringComparer.OrdinalIgnoreCase);
        public bool Exists(string path) => Keys.ContainsKey(path);
        public KeyBackup? Snapshot(string path) => Keys.GetValueOrDefault(path);
        public KeyBackup? Delete(string path) { if (Locked.Contains(path)) throw new UnauthorizedAccessException("denied"); return Keys.Remove(path, out var key) ? key : null; }
        public KeyBackup? DeleteValue(string path, string name) => null;
        public KeyBackup? SetText(string path, string name, string value) => null;
        public void Restore(KeyBackup backup) => Keys[backup.Path] = backup;
        public FootprintPlan Footprint(FootprintRequest request) => FootprintPlan.Empty;
    }

    private static RepairFinding Finding(string title, string key) => new(title, RepairKind.BrokenEntry, title, "", "", RepairLevel.Recommended, [key]);

    [Fact]
    public async Task ARepairCutShortByADeclinedPromptKeepsUndoForWhatItHadAlreadyChanged()
    {
        const string ghost = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Ghost";
        const string locked = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Locked";
        const string third = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Third";
        var users = new Editor { Keys = { [ghost] = new(ghost, [], []), [locked] = new(locked, [], []), [third] = new(third, [], []) }, Locked = { locked } };
        var history = new RepairHistory(Path.Combine(root, "history"));
        // Nothing asks for permission up front; the second key turns out to need the helper, and the prompt is declined.
        var service = new RepairService(new FakePc(), users, new Editor(), _ => throw new OperationCanceledException("declined"), () => { }, history);

        var outcome = await service.FixAsync([Finding("Ghost", ghost), Finding("Locked", locked), Finding("Third", third)], "Repaired 3 leftovers");

        Assert.Equal("Administrator permission was declined.", outcome.Stopped);
        Assert.Equal(1, outcome.Fixed);
        Assert.Equal(2, outcome.Results!.Count);
        Assert.Equal(RepairStatus.NotFixed, outcome.Results[1].Status);
        Assert.Contains(third, users.Keys.Keys);
        // The key that was removed before the prompt is in the Undo history, so it can be put back.
        Assert.Equal(ghost, Assert.Single(outcome.Batch!.Keys).Path);
        Assert.Equal(outcome.Batch.Id, history.Latest()!.Id);
        Assert.True((await service.UndoAsync(outcome.Batch)).Complete);
        Assert.Contains(ghost, users.Keys.Keys);
    }

    [Fact]
    public async Task ACancelledReinstallNeverFallsThroughToRemovingTheRecordAndClearsTheMarkerItLeft()
    {
        using var sandbox = new RepairSandbox();
        BrokenPython.Machine(sandbox);
        sandbox.Put(InstallRepair.InstallLockKey, ("Started", "x"));
        var product = new MsiProduct(BrokenPython.Code, "Machine", "", "Machine product", @"C:\Windows\Installer\gone.msi", "Contoso");
        var pc = new FakePc { Msi = [product], Lock = true, State = _ => 5, Folders = { @"C:\Machine" } };
        pc.Programs = [new($@"HKLM\{BrokenPython.Cv}\Uninstall\{BrokenPython.Code}", product.Name, "1", "Contoso", $"MsiExec.exe /X{BrokenPython.Code}", "", @"C:\Machine", "", true, false, false, false)];
        using var cancel = new CancellationTokenSource();
        var ops = new FakeOps { Candidates = [@"C:\cache\core.msi"], Registered = "{Y}", ExitCode = 1602, OnReinstall = () => cancel.Cancel() };
        ops.Packages[@"C:\cache\core.msi"] = new(BrokenPython.Code, "{Y}", "1", "core");
        var history = new RepairHistory(Path.Combine(root, "history"));
        var service = new RepairService(pc, sandbox.User(), sandbox.Elevated(), _ => Task.CompletedTask, () => { }, history, ops, ops);
        var finding = Assert.Single(InstallRepair.Scan(pc, shortcuts: false).Findings, x => x.Footprint is { Kind: "product" });

        var outcome = await service.FixAsync([finding], "Repaired", cancel.Token);

        Assert.Equal("The repair was stopped.", outcome.Stopped);
        Assert.Equal(RepairStatus.NotFixed, Assert.Single(outcome.Results!).Status);
        Assert.Single(ops.Reinstalled);
        // The program's record is still there: a stopped repair is not a failed one that removes the record.
        Assert.True(sandbox.Has($@"HKLM\SOFTWARE\Classes\Installer\Products\{BrokenPython.Packed}"));
        // The marker the stopped install left behind is gone, and is in the Undo history.
        Assert.False(sandbox.Has(InstallRepair.InstallLockKey));
        Assert.Equal(InstallRepair.InstallLockKey, Assert.Single(outcome.Batch!.Keys).Path);
    }

    // ---- The vault's own housekeeping ----

    [Fact]
    public void OldCopiesAndQuarantinedFoldersWhoseTimeIsUpArePruned()
    {
        var token = RepairVault.NewToken();
        var kept = Path.Combine(vault.Quarantine, token, "x", "Ghost");
        Directory.CreateDirectory(kept);
        vault.Keep(token, new FolderBackup(@"C:\Program Files\Ghost", kept));
        var orphan = Path.Combine(vault.Quarantine, RepairVault.NewToken(), "x", "Orphan");
        Directory.CreateDirectory(orphan);
        var fresh = vault.Keep(new KeyBackup(Key, [], []));

        Assert.Equal(1, vault.Prune());
        Assert.True(Directory.Exists(kept));
        Assert.False(Directory.Exists(orphan));
        Assert.NotNull(vault.Take<KeyBackup>(fresh));

        Assert.Equal(1, vault.Prune(DateTimeOffset.UtcNow + RepairVault.QuarantineRetention + TimeSpan.FromDays(1)));
        Assert.False(Directory.Exists(kept));
        Assert.Null(vault.Take<FolderBackup>(token));
    }
}
