using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

internal sealed class FakeStartupPc : IStartupSystem, IStartupWriter
{
    public StartupPlaces Places { get; } = new(@"C:\Windows", @"C:\Users\me", [@"C:\Users\me\AppData\Local\Temp", @"C:\Users\me\Downloads"]);
    public List<RunValue> Runs = [];
    public Dictionary<string, byte[]> ApprovedState = new(StringComparer.OrdinalIgnoreCase);
    public List<FolderItem> Folder = [];
    public List<ScheduledStartup> TaskList = [];
    public int OwnedTasks;
    public List<ServiceFacts> ServiceList = [];
    public List<StoreStartup> Store = [];
    public List<WindowsSetting> Settings = [];
    public Dictionary<string, FileFacts> Files = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Running = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Writes = [];
    public bool DenyWrites;
    /// <summary>Every place and file the scan looked at, so a test can tell what a scan of one item left alone.</summary>
    public List<string> Reads = [];

    public IReadOnlyList<RunValue> RunValues() { Reads.Add("runs"); return Runs; }
    public byte[]? Approved(StartupScope scope, string subKey, string name) => ApprovedState.GetValueOrDefault($"{scope}|{subKey}|{name}");
    public IReadOnlyList<FolderItem> FolderItems() { Reads.Add("folder"); return Folder; }
    public (IReadOnlyList<ScheduledStartup>, int) Tasks() { Reads.Add("tasks"); return (TaskList, OwnedTasks); }
    public IReadOnlyList<ServiceFacts> Services() { Reads.Add("services"); return ServiceList; }
    public IReadOnlyList<StoreStartup> StoreTasks() { Reads.Add("store"); return Store; }
    public IReadOnlyList<WindowsSetting> WindowsSettings() { Reads.Add("settings"); return Settings; }
    public FileFacts Describe(string path) { Reads.Add("describe " + path); return Files.GetValueOrDefault(path) ?? new(false, "", "", "", StartupTrust.Unknown); }
    public List<UninstallEntry> Installed = [];
    public Dictionary<string, string> Packages = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<UninstallEntry> InstalledPrograms() => Installed;
    public string PackagePublisher(string exePath) => Packages.FirstOrDefault(x => exePath.Contains(x.Key, StringComparison.OrdinalIgnoreCase)).Value ?? "";
    public string Expand(string text) => text.Replace("%SystemRoot%", @"C:\Windows", StringComparison.OrdinalIgnoreCase);
    public bool FileExists(string path) => Files.TryGetValue(path, out var facts) && facts.Exists;
    public string? FindOnPath(string name) => Files.Keys.FirstOrDefault(x => x.EndsWith("\\" + name, StringComparison.OrdinalIgnoreCase));
    public ISet<string> RunningImages() => Running;
    public Dictionary<string, List<string>> Subfolder = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> Subfolders(string folder) => Subfolder.GetValueOrDefault(folder) ?? [];
    public Dictionary<string, byte[]> FolderBytes = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> TaskXml = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string path, string company = "Acme Corp", StartupTrust trust = StartupTrust.Verified) => Files[path] = new(true, company, "", "", trust);

    // The writer half changes the fake PC the same way Windows would, so a change can be read back.
    private void Guard() { if (DenyWrites) throw new UnauthorizedAccessException("denied"); }
    public void SetApproved(StartupScope scope, string subKey, string name, bool enabled)
    {
        Guard(); Writes.Add($"approved {scope} {subKey} {name} {enabled}");
        ApprovedState[$"{scope}|{subKey}|{name}"] = StartupApproved.Make(enabled, DateTimeOffset.UnixEpoch);
    }
    public void SetTask(string path, bool enabled)
    {
        Guard(); Writes.Add($"task {path} {enabled}");
        TaskList = [.. TaskList.Select(x => x.Path == path ? x with { Enabled = enabled } : x)];
    }
    public void SetService(string name, int start, bool delayed)
    {
        Guard(); Writes.Add($"service {name} {start} {delayed}");
        ServiceList = [.. ServiceList.Select(x => x.Name == name ? x with { Start = start, Delayed = delayed } : x)];
    }
    public void SetStoreTask(string family, string taskId, bool enabled)
    {
        Guard(); Writes.Add($"store {family} {taskId} {enabled}");
        Store = [.. Store.Select(x => x.Family == family && x.TaskId == taskId ? x with { State = enabled ? 2 : 1 } : x)];
    }
    public void RemoveRun(StartupScope scope, string source, string name)
    {
        Guard(); Writes.Add($"remove-run {scope} {source} {name}");
        Runs = [.. Runs.Where(x => !(x.Scope == scope && x.Where == source && x.Name == name))];
    }
    public void RestoreRun(StartupScope scope, string source, string name, string command, bool expandable)
    {
        Guard(); Writes.Add($"restore-run {scope} {source} {name} {expandable}");
        Runs.Add(new(scope, source is "Run32" or "RunOnce32", name, command, source, expandable));
    }
    public byte[] RemoveFolderItem(StartupScope scope, string fileName)
    {
        Guard(); Writes.Add($"remove-folder {scope} {fileName}");
        Folder = [.. Folder.Where(x => !(x.Scope == scope && x.FileName == fileName))];
        return FolderBytes.GetValueOrDefault(fileName) ?? [1, 2, 3];
    }
    public void RestoreFolderItem(StartupScope scope, string fileName, byte[] contents)
    {
        Guard(); Writes.Add($"restore-folder {scope} {fileName} {contents.Length}");
        Folder.Add(new(scope, fileName, $@"C:\Startup\{fileName}", @"C:\Apps\a.exe", ""));
    }
    private ScheduledStartup? removedTask;
    public string RemoveTask(string path)
    {
        Guard(); Writes.Add($"remove-task {path}");
        removedTask = TaskList.First(x => x.Path == path);
        TaskList = [.. TaskList.Where(x => x.Path != path)];
        return TaskXml.GetValueOrDefault(path) ?? "<Task/>";
    }
    public void RestoreTask(string path, string xml)
    {
        Guard(); Writes.Add($"restore-task {path} {xml}");
        if (removedTask is not null) TaskList.Add(removedTask);
    }
}

public sealed class StartupScanTests
{
    private static StartupEntry Only(FakeStartupPc pc) => Assert.Single(StartupScan.Run(pc).Entries);

    [Fact]
    public void ExplorersOnOffRecordIsReadAndWrittenTheWayWindowsDoesIt()
    {
        Assert.True(StartupApproved.IsEnabled(null));
        Assert.True(StartupApproved.IsEnabled([2, 0, 0, 0]));
        Assert.True(StartupApproved.IsEnabled([6, 0, 0, 0]));
        Assert.False(StartupApproved.IsEnabled([3, 0, 0, 0]));
        Assert.False(StartupApproved.IsEnabled([7, 0, 0, 0]));
        var on = StartupApproved.Make(true, DateTimeOffset.UnixEpoch);
        var off = StartupApproved.Make(false, DateTimeOffset.Parse("2026-01-02T03:04:05Z"));
        Assert.Equal(12, on.Length); Assert.Equal(12, off.Length);
        Assert.Equal(2, on[0]); Assert.All(on.Skip(1), x => Assert.Equal(0, x));
        Assert.Equal(3, off[0]); Assert.Equal(DateTimeOffset.Parse("2026-01-02T03:04:05Z").UtcDateTime.ToFileTimeUtc(), BitConverter.ToInt64(off, 4));
    }

    [Fact]
    public void ARunEntryIsOnUnlessExplorerRecordsItAsOff()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Program Files\Steam\steam.exe");
        pc.Runs.Add(new(StartupScope.User, false, "Steam", "\"C:\\Program Files\\Steam\\steam.exe\" -silent"));
        Assert.True(Only(pc).Enabled);

        pc.ApprovedState["User|Run|Steam"] = [3, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8];
        var entry = Only(pc);
        Assert.False(entry.Enabled);
        Assert.Equal(StartupRisk.Optional, entry.Risk);
        Assert.Equal(@"C:\Program Files\Steam\steam.exe", entry.ExePath);
    }

    [Fact]
    public void The32BitRunKeyUsesItsOwnApprovalKey()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Program Files (x86)\Old\old.exe");
        pc.Runs.Add(new(StartupScope.Machine, true, "Old", @"C:\Program Files (x86)\Old\old.exe"));
        pc.ApprovedState["Machine|Run32|Old"] = [3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.False(Only(pc).Enabled);
    }

    [Fact]
    public void AnEntryWhoseFileIsGoneIsSafeToTurnOff()
    {
        var pc = new FakeStartupPc();
        pc.Runs.Add(new(StartupScope.User, false, "Ghost", @"C:\Gone\ghost.exe"));
        var entry = Only(pc);
        Assert.True(entry.FileMissing);
        Assert.Equal(StartupRisk.Optional, entry.Risk);
        Assert.Contains("gone", entry.Reason);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\SecurityHealthSystray.exe")]
    [InlineData(@"C:\Windows\System32\ctfmon.exe")]
    public void ThingsInTheWindowsFolderAreNeverOffered(string path)
    {
        var pc = new FakeStartupPc();
        pc.Add(path, "Microsoft Corporation");
        pc.Runs.Add(new(StartupScope.Machine, false, "Core", path));
        var entry = Only(pc);
        Assert.Equal(StartupRisk.Protected, entry.Risk);
        Assert.False(entry.CanChange);
    }

    [Fact]
    public void AScriptHostIsNotTreatedAsWindowsJustBecauseItLivesThere()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Windows\System32\cmd.exe", "Microsoft Corporation");
        pc.Add(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "Microsoft Corporation");
        pc.Runs.Add(new(StartupScope.User, false, "Quiet", "cmd.exe /c start \"\" C:\\Users\\me\\x.bat"));
        pc.Runs.Add(new(StartupScope.User, false, "Sneaky", @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -enc AAAA"));
        var entries = StartupScan.Run(pc).Entries;
        Assert.Equal(2, entries.Count);
        Assert.All(entries, x => { Assert.Equal(StartupRisk.Careful, x.Risk); Assert.True(x.CanChange); });
    }

    [Fact]
    public void ALauncherDoesNotLendItsMicrosoftIdentityOrRunningStateToTheScriptItRuns()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Windows\System32\cmd.exe", "Microsoft Corporation");
        pc.Add(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "Microsoft Corporation");
        pc.Add(@"C:\Users\me\tools\sync.bat", "Acme Tools");
        pc.Running.Add(@"C:\Windows\System32\cmd.exe"); pc.Running.Add(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe");
        pc.Runs.Add(new(StartupScope.User, false, "Sync", @"C:\Windows\System32\cmd.exe /c start """" C:\Users\me\tools\sync.bat"));
        pc.Runs.Add(new(StartupScope.User, false, "Inline", @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -enc AAAA"));
        pc.Runs.Add(new(StartupScope.User, false, "Gone", @"C:\Windows\System32\cmd.exe /c C:\Users\me\tools\deleted.bat"));
        pc.Runs.Add(new(StartupScope.User, false, "Bare", @"C:\Windows\System32\cmd.exe /c start notepad"));
        var entries = StartupScan.Run(pc).Entries.ToDictionary(x => x.Key);

        Assert.Equal(@"C:\Users\me\tools\sync.bat", entries["Sync"].ExePath);
        Assert.Equal("Acme Tools", entries["Sync"].Publisher);
        Assert.False(entries["Sync"].Running);

        Assert.Equal(StartupTrust.Unknown, entries["Inline"].Trust);
        Assert.DoesNotContain("Microsoft", entries["Inline"].Publisher);
        Assert.False(entries["Inline"].Running);
        Assert.False(entries["Inline"].FileMissing);

        Assert.True(entries["Gone"].FileMissing);
        Assert.False(entries["Bare"].FileMissing);
        Assert.All(entries.Values, x => Assert.Equal(StartupRisk.Careful, x.Risk));
    }

    [Fact]
    public void ACommandBuiltIntoCmdIsNotMistakenForAProgramOfTheSameNameOnThePath()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Windows\System32\cmd.exe", "Microsoft Corporation"); pc.Add(@"C:\Program Files\Git\usr\bin\echo.exe", "GNU");
        pc.Runs.Add(new(StartupScope.User, false, "Once", @"C:\Windows\System32\cmd.exe /c echo hi"));
        var entry = Only(pc);
        Assert.Equal(@"C:\Windows\System32\cmd.exe", entry.ExePath);
        Assert.Equal(StartupTrust.Unknown, entry.Trust);
        Assert.False(entry.FileMissing);
    }

    [Fact]
    public void SquirrelAppsAreFoundInsideTheirNewestVersionFolderNotAsTheirUpdater()
    {
        var pc = new FakeStartupPc();
        const string root = @"C:\Users\me\AppData\Local\Discord";
        pc.Add($@"{root}\Update.exe", "GitHub"); pc.Add($@"{root}\app-1.0.9001\Discord.exe", "Discord Inc."); pc.Add($@"{root}\app-1.0.9010\Discord.exe", "Discord Inc.");
        pc.Subfolder[root] = [$@"{root}\app-1.0.9001", $@"{root}\app-1.0.9010", $@"{root}\packages"];
        pc.Running.Add($@"{root}\app-1.0.9010\Discord.exe");
        pc.Runs.Add(new(StartupScope.User, false, "Discord", $@"""{root}\Update.exe"" --processStart Discord.exe"));
        var entry = Only(pc);
        Assert.Equal($@"{root}\app-1.0.9010\Discord.exe", entry.ExePath);
        Assert.Equal("Discord Inc.", entry.Publisher);
        Assert.True(entry.Running);
        Assert.Equal(StartupGuard.OrdinaryReason, entry.Reason);
    }

    [Fact]
    public void ATasksArgumentsDecideWhatItStarts()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Windows\System32\cmd.exe", "Microsoft Corporation"); pc.Add(@"C:\Apps\run.bat", "Acme");
        pc.TaskList.Add(new(@"\Wrapped", "Wrapped", "", "Acme", true, @"C:\Windows\System32\cmd.exe", @"/c C:\Apps\run.bat", true, false, "me"));
        var entry = Only(pc);
        Assert.Equal(@"C:\Apps\run.bat", entry.ExePath);
        Assert.Equal("Acme", entry.Publisher);
    }

    [Fact]
    public void OnlyWindowsOwnTasksUnderMicrosoftAreLeftOut()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Windows\System32\sihost.exe", "Microsoft Corporation"); pc.Add(@"C:\Program Files\Office\office.exe", "Microsoft Corporation");
        pc.TaskList.Add(new(@"\Microsoft\XblGameSave\Sign-in", "Sign-in", "", "Microsoft", true, @"C:\Windows\System32\sihost.exe", "", true, false, "me"));
        pc.TaskList.Add(new(@"\Microsoft\Office\Logon", "Office Logon", "", "Microsoft", true, @"C:\Program Files\Office\office.exe", "", true, false, "me"));
        var result = StartupScan.Run(pc);
        Assert.Equal("Office Logon", Assert.Single(result.Entries).Name);
        Assert.Equal(1, result.WindowsTasks);
    }

    [Theory]
    [InlineData("RunOnce", StartupRisk.Optional)]
    [InlineData("RunOnce32", StartupRisk.Optional)]
    [InlineData("PolicyRun", StartupRisk.Careful)]
    public void RunKeysExplorerCannotSwitchOffAreListedAsRemovableOnly(string source, StartupRisk risk)
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Apps\setup.exe");
        pc.Runs.Add(new(StartupScope.User, source == "RunOnce32", "Setup", @"C:\Apps\setup.exe /finish", source));
        var entry = Only(pc);
        Assert.Equal($"run|user|{source}|Setup", entry.Id);
        Assert.True(entry.Enabled);
        Assert.False(entry.CanToggle);
        Assert.True(entry.CanRemove);
        Assert.Equal(risk, entry.Risk);
        Assert.Contains("remove", entry.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(new LocalStartupChanger(pc, pc).Change(new(entry.Id, false)).Ok);
        Assert.Empty(pc.Writes);
    }

    [Fact]
    public void AServiceWindowsStartsOnAnEventSaysSoAndASharedHostServiceIsKnownByItsLibrary()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Apps\vpn.exe"); pc.Add(@"C:\Windows\System32\svchost.exe", "Microsoft Corporation");
        pc.Add(@"C:\Windows\System32\wuauserv.dll", "Microsoft Corporation"); pc.Add(@"C:\Program Files\Acme\acme.dll", "Acme Corp");
        pc.ServiceList.Add(new("Vpn", "Vpn", "", "\"C:\\Apps\\vpn.exe\"", 0x10, 2, false, "LocalSystem", [], Triggered: true));
        pc.ServiceList.Add(new("Hosted", "Hosted", "", @"C:\Windows\System32\svchost.exe -k netsvcs", 0x20, 2, false, "LocalSystem", [], ServiceDll: @"C:\Program Files\Acme\acme.dll"));
        pc.ServiceList.Add(new("wuauserv", "Windows Update", "", @"C:\Windows\System32\svchost.exe -k netsvcs", 0x20, 2, false, "LocalSystem", [], ServiceDll: @"C:\Windows\System32\wuauserv.dll"));
        var result = StartupScan.Run(pc);
        var entries = result.Entries.ToDictionary(x => x.Key);
        Assert.True(entries["Vpn"].ServiceTriggered);
        Assert.Contains("events", entries["Vpn"].Reason);
        Assert.Equal("Acme Corp", entries["Hosted"].Publisher);
        Assert.False(entries["Hosted"].ServiceTriggered);
        Assert.DoesNotContain("wuauserv", entries.Keys);
        Assert.Equal(1, result.WindowsServices);
    }

    [Fact]
    public void PerUserWinlogonSettingsAreMarkedAsTheUsers()
    {
        var pc = new FakeStartupPc();
        pc.Settings.Add(new("User.Winlogon.Shell", "Your own desktop program (Shell)", @"C:\evil.exe", "", "HKCU", StartupScope.User));
        var entry = Only(pc);
        Assert.Equal("setting|user|User.Winlogon.Shell", entry.Id);
        Assert.Equal(StartupScope.User, entry.Scope);
        Assert.False(entry.CanChange);
        Assert.Contains("not the usual", entry.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AShortcutWhoseProgramIsGoneCanStillBeRevealedByItsOwnPath()
    {
        var pc = new FakeStartupPc();
        pc.Folder.Add(new(StartupScope.User, "Old.lnk", @"C:\Users\me\Startup\Old.lnk", @"C:\Gone\old.exe", ""));
        var entry = Only(pc);
        Assert.True(entry.FileMissing);
        Assert.Equal(@"C:\Users\me\Startup\Old.lnk", entry.FolderPath);
        Assert.True(entry.CanRemove);
    }

    [Theory]
    [InlineData("Norton Security", @"C:\Program Files\Norton\nortonui.exe", "Gen Digital")]
    [InlineData("Defender tray", @"C:\Program Files\Windows Defender\mpcmdrun.exe", "Microsoft Corporation")]
    [InlineData("Shield", @"C:\Program Files\Avast Software\Avast\avastui.exe", "Avast Software")]
    public void SecuritySoftwareIsProtected(string name, string path, string company)
    {
        var pc = new FakeStartupPc();
        pc.Add(path, company);
        pc.Runs.Add(new(StartupScope.Machine, false, name, path));
        Assert.Equal(StartupRisk.Protected, Only(pc).Risk);
    }

    [Fact]
    public void HardwareHelpersCarryAWarningButCanBeChanged()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Program Files\Realtek\Audio\RtkNGUI64.exe", "Realtek Semiconductor");
        pc.Runs.Add(new(StartupScope.Machine, false, "RtkAudio", @"C:\Program Files\Realtek\Audio\RtkNGUI64.exe"));
        var entry = Only(pc);
        Assert.Equal(StartupRisk.Careful, entry.Risk);
        Assert.True(entry.CanChange);
    }

    [Fact]
    public void AnUnsignedProgramStartingFromATempFolderIsFlagged()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Users\me\AppData\Local\Temp\x\run.exe", "", StartupTrust.Unsigned);
        pc.Runs.Add(new(StartupScope.User, false, "Updater", @"C:\Users\me\AppData\Local\Temp\x\run.exe"));
        var entry = Only(pc);
        Assert.Equal(StartupRisk.Careful, entry.Risk);
        Assert.Contains("temporary", entry.Reason);
    }

    [Fact]
    public void ProgramsThatClaimToBeMicrosoftButAreNotSignedAreFlaggedAndSignedOnesAreNot()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Program Files\Microsoft OneDrive\OneDrive.exe", "Microsoft Corporation");
        pc.Add(@"C:\Users\me\AppData\Roaming\svc\svchost.exe", "Microsoft Corporation", StartupTrust.Unsigned);
        pc.Runs.Add(new(StartupScope.User, false, "OneDrive", @"""C:\Program Files\Microsoft OneDrive\OneDrive.exe"" /background"));
        pc.Runs.Add(new(StartupScope.User, false, "Fake", @"C:\Users\me\AppData\Roaming\svc\svchost.exe"));
        var entries = StartupScan.Run(pc).Entries.ToDictionary(x => x.Key);
        Assert.Equal(StartupRisk.Optional, entries["OneDrive"].Risk);
        Assert.Equal(StartupRisk.Careful, entries["Fake"].Risk);
    }

    [Fact]
    public void ARunningProgramIsMarkedAsRunning()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Apps\a.exe"); pc.Running.Add(@"C:\Apps\a.exe");
        pc.Runs.Add(new(StartupScope.User, false, "A", @"C:\Apps\a.exe"));
        Assert.True(Only(pc).Running);
    }

    [Fact]
    public void ARunValueIsNamedWithoutItsRandomSuffix()
    {
        Assert.Equal("MicrosoftEdgeAutoLaunch", StartupScan.CleanName("MicrosoftEdgeAutoLaunch_8714F0D917266FE3AFB7F8BB98EEBC18"));
        Assert.Equal("Discord", StartupScan.CleanName("Discord"));
        Assert.Equal("OneDrive Startup Task", StartupScan.TaskName("OneDrive Startup Task-S-1-5-21-2263392280-518463016-1748006429-1001"));
        Assert.Equal("WhatsAppDesktop", StartupScan.StoreName("5319275A.WhatsAppDesktop_cv1g1gvanyjgm"));
        Assert.Equal("Claude", StartupScan.StoreName("Claude_pzs8sxrjxfjjc"));
    }

    [Fact]
    public void StartupFolderShortcutsAreReadFromTheirTarget()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Program Files\Tailscale\tailscale-ipn.exe", "Tailscale Inc.");
        pc.Folder.Add(new(StartupScope.Machine, "Tailscale.lnk", @"C:\ProgramData\Startup\Tailscale.lnk", @"C:\Program Files\Tailscale\tailscale-ipn.exe", "--unattended"));
        pc.ApprovedState["Machine|StartupFolder|Tailscale.lnk"] = [7, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        var entry = Only(pc);
        Assert.Equal("Tailscale", entry.Name);
        Assert.False(entry.Enabled);
        Assert.Equal("StartupFolder", entry.Source);
    }

    [Fact]
    public void OnlyTasksThatRunAtSignInOrBootAreListed()
    {
        var pc = new FakeStartupPc { OwnedTasks = 190 };
        pc.Add(@"C:\Apps\login.exe"); pc.Add(@"C:\Apps\daily.exe");
        pc.TaskList.Add(new(@"\Login", "Login", "", "Acme", true, @"C:\Apps\login.exe", "", true, false, "me"));
        pc.TaskList.Add(new(@"\Daily", "Daily", "", "Acme", true, @"C:\Apps\daily.exe", "", false, false, "me"));
        var result = StartupScan.Run(pc);
        Assert.Equal("Login", Assert.Single(result.Entries).Name);
        Assert.Equal(190, result.WindowsTasks);
    }

    [Fact]
    public void AServiceRunningAsSystemAtBootIsFlaggedCareful()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Apps\boot.exe");
        pc.TaskList.Add(new(@"\Boot", "Boot", "", "Acme", true, @"C:\Apps\boot.exe", "", false, true, "SYSTEM"));
        var entry = Only(pc);
        Assert.Equal(StartupRisk.Careful, entry.Risk);
        Assert.Equal(StartupScope.Machine, entry.Scope);
    }

    private static ServiceFacts Service(string name, int start = 2, string image = @"C:\Apps\svc.exe", params string[] depends) =>
        new(name, name, "", $"\"{image}\"", 0x10, start, false, "LocalSystem", depends);

    [Fact]
    public void ThirdPartyServicesAreOfferedAsCarefulAndWindowsOnesAreLeftOut()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Apps\svc.exe"); pc.Add(@"C:\Windows\System32\svchost.exe", "Microsoft Corporation");
        pc.ServiceList.Add(Service("Acme", 2));
        pc.ServiceList.Add(Service("Dhcp", 2, @"C:\Windows\System32\svchost.exe"));
        pc.ServiceList.Add(Service("Idle", 3));
        pc.ServiceList.Add(new("Driver", "Driver", "", @"\SystemRoot\drv.sys", 0x1, 2, false, "", []));
        pc.ServiceList.Add(new("UserSvc_1234", "User", "", @"C:\Apps\svc.exe", 0xD0, 2, false, "", []));
        var result = StartupScan.Run(pc);
        var entry = Assert.Single(result.Entries);
        Assert.Equal("Acme", entry.Name);
        Assert.Equal(StartupRisk.Careful, entry.Risk);
        Assert.Equal(1, result.WindowsServices);
    }

    [Fact]
    public void AServiceThatOthersDependOnIsProtected()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Apps\svc.exe");
        pc.ServiceList.Add(Service("Base", 2));
        pc.ServiceList.Add(Service("Top", 2, @"C:\Apps\svc.exe", "Base"));
        var entries = StartupScan.Run(pc).Entries.ToDictionary(x => x.Name);
        Assert.Equal(StartupRisk.Protected, entries["Base"].Risk);
        Assert.Equal(StartupRisk.Careful, entries["Top"].Risk);
    }

    [Fact]
    public void MicrosoftServicesOutsideWindowsAreProtected()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Program Files\Microsoft SQL Server\sqlwriter.exe", "Microsoft Corporation");
        pc.ServiceList.Add(Service("SQLWriter", 2, @"C:\Program Files\Microsoft SQL Server\sqlwriter.exe"));
        Assert.Equal(StartupRisk.Protected, Only(pc).Risk);
    }

    [Fact]
    public void AServiceTurnedToManualStaysListedWhenAskedFor()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Apps\svc.exe");
        pc.ServiceList.Add(Service("Acme", 3));
        Assert.Empty(StartupScan.Run(pc).Entries);
        var entry = Assert.Single(StartupScan.Run(pc, ["Acme"]).Entries);
        Assert.False(entry.Enabled);
        Assert.Equal(entry.Id, StartupScan.Find(pc, entry.Id)!.Id);
    }

    [Fact]
    public void StoreAppsMapTheirStateAndPolicyLocksThem()
    {
        var pc = new FakeStartupPc();
        pc.Store.Add(new("Acme.App_abc", "Boot", 2, "Acme.App_1_x64__abc", "Acme App", "Acme", ""));
        pc.Store.Add(new("Acme.Two_abc", "Boot", 1, "", "", "", ""));
        pc.Store.Add(new("Corp.App_abc", "Boot", 3, "", "Corp App", "Corp", ""));
        var entries = StartupScan.Run(pc).Entries.ToDictionary(x => x.Name);
        Assert.True(entries["Acme App"].Enabled);
        Assert.False(entries["Two"].Enabled);
        Assert.Equal(StartupRisk.Protected, entries["Corp App"].Risk);
    }

    [Fact]
    public void WindowsSignInSettingsAreShownButNeverChangeable()
    {
        var pc = new FakeStartupPc();
        pc.Settings.Add(new("Winlogon.Shell", "Windows desktop (Shell)", "explorer.exe", "explorer.exe", "Winlogon"));
        pc.Settings.Add(new("Winlogon.Userinit", "Sign-in program", @"C:\Windows\system32\userinit.exe,evil.exe", @"C:\Windows\system32\userinit.exe,", "Winlogon"));
        var entries = StartupScan.Run(pc).Entries;
        Assert.All(entries, x => { Assert.Equal(StartupRisk.Protected, x.Risk); Assert.False(x.CanChange); });
        Assert.Contains("not the usual", entries.Single(x => x.Key == "Winlogon.Userinit").Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtectedItemsSortAfterEverythingElse()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Apps\z.exe"); pc.Add(@"C:\Windows\a.exe", "Microsoft Corporation");
        pc.Runs.Add(new(StartupScope.User, false, "Zed", @"C:\Apps\z.exe"));
        pc.Runs.Add(new(StartupScope.Machine, false, "Alpha", @"C:\Windows\a.exe"));
        Assert.Equal(["Zed", "Alpha"], StartupScan.Run(pc).Entries.Select(x => x.Name));
    }
}

public sealed class StartupChangeTests
{
    private static (FakeStartupPc Pc, StartupEntry Entry) Pc()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Apps\a.exe");
        pc.Runs.Add(new(StartupScope.User, false, "A", @"C:\Apps\a.exe"));
        return (pc, StartupScan.Run(pc).Entries[0]);
    }

    [Fact]
    public void TurningARunEntryOffOnlyWritesExplorersRecordAndIsReadBack()
    {
        var (pc, entry) = Pc();
        var outcome = new LocalStartupChanger(pc, pc).Change(new(entry.Id, false));
        Assert.True(outcome.Ok);
        Assert.False(outcome.Entry!.Enabled);
        Assert.Equal(["approved User Run A False"], pc.Writes);
        Assert.Single(pc.Runs);
    }

    [Fact]
    public void ProtectedItemsAreRefusedByTheRulesNotByTheCaller()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Windows\System32\SecurityHealthSystray.exe", "Microsoft Corporation");
        pc.Runs.Add(new(StartupScope.Machine, false, "SecurityHealth", @"C:\Windows\System32\SecurityHealthSystray.exe"));
        var id = StartupScan.Run(pc).Entries[0].Id;
        var outcome = new LocalStartupChanger(pc, pc).Change(new(id, false));
        Assert.False(outcome.Ok);
        Assert.Empty(pc.Writes);
    }

    [Fact]
    public void AnUnknownItemIsRefused()
    {
        var pc = new FakeStartupPc();
        Assert.False(new LocalStartupChanger(pc, pc).Change(new("run|user|Run|Nope", false)).Ok);
        Assert.Empty(pc.Writes);
    }

    [Fact]
    public void ServicesAreMadeManualNeverDisabledAndComeBackAsTheyWere()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Apps\svc.exe");
        pc.ServiceList.Add(new("Acme", "Acme", "", "\"C:\\Apps\\svc.exe\"", 0x10, 2, true, "LocalSystem", []));
        var changer = new LocalStartupChanger(pc, pc);
        var id = StartupScan.Run(pc).Entries[0].Id;

        Assert.True(changer.Change(new(id, false)).Ok);
        Assert.Equal(["service Acme 3 False"], pc.Writes);
        Assert.True(changer.Change(new(id, true, 2, true)).Ok);
        Assert.Equal("service Acme 2 True", pc.Writes[^1]);
        Assert.DoesNotContain(pc.Writes, x => x.StartsWith("service Acme 4"));
    }

    [Fact]
    public void ARequestCannotAskForAStartTypeOtherThanAutomaticWhenTurningOn()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Apps\svc.exe");
        pc.ServiceList.Add(new("Acme", "Acme", "", "\"C:\\Apps\\svc.exe\"", 0x10, 3, false, "LocalSystem", []));
        var id = StartupScan.Find(pc, "service|machine|Acme")!.Id;
        new LocalStartupChanger(pc, pc).Change(new(id, true, 4, false));
        Assert.Equal("service Acme 2 False", pc.Writes.Single());
    }

    [Fact]
    public void StoreAppsAreChangedThroughTheirStartupState()
    {
        var pc = new FakeStartupPc();
        pc.Store.Add(new("Acme.App_abc", "Boot", 2, "", "Acme", "Acme", ""));
        var id = StartupScan.Run(pc).Entries[0].Id;
        Assert.True(new LocalStartupChanger(pc, pc).Change(new(id, false)).Ok);
        Assert.Equal("store Acme.App_abc Boot False", pc.Writes.Single());
    }

    private static StartupRequest Remove(string id) => new(id, false, Action: StartupAction.Remove);
    private static StartupRequest Restore(string id, StartupVault vault) => new(id, true, Action: StartupAction.Restore, Vault: vault);

    [Fact]
    public void RemovingARunValueKeepsACopyAndRestoringPutsItBackExactlyAsItWas()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Apps\setup.exe");
        pc.Runs.Add(new(StartupScope.User, false, "Setup", @"C:\Apps\setup.exe /finish", "RunOnce", Expandable: true));
        var changer = new LocalStartupChanger(pc, pc);
        var id = StartupScan.Run(pc).Entries[0].Id;

        var removed = changer.Change(Remove(id));

        Assert.True(removed.Ok);
        Assert.Empty(pc.Runs);
        var vault = removed.Vault!;
        Assert.Equal(new StartupVault(StartupKind.RunKey, StartupScope.User, "RunOnce", "Setup", @"C:\Apps\setup.exe /finish", true), vault);

        var restored = changer.Change(Restore(id, vault));
        Assert.True(restored.Ok);
        var back = Assert.Single(pc.Runs);
        Assert.True(back.Expandable);
        Assert.Equal(@"C:\Apps\setup.exe /finish", back.Command);
        Assert.Equal(["remove-run User RunOnce Setup", "restore-run User RunOnce Setup True"], pc.Writes);
    }

    [Fact]
    public void AStartupShortcutAndAScheduledTaskAreAlsoKeptAndPutBack()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Apps\a.exe");
        pc.FolderBytes["A.lnk"] = StartupGuardRuleTests.Shortcut;
        pc.Folder.Add(new(StartupScope.User, "A.lnk", @"C:\Startup\A.lnk", @"C:\Apps\a.exe", ""));
        pc.TaskList.Add(new(@"\Sync", "Sync", "", "Acme", true, @"C:\Apps\a.exe", "", true, false, "me"));
        pc.TaskXml[@"\Sync"] = StartupGuardRuleTests.UserTask;
        var changer = new LocalStartupChanger(pc, pc);

        foreach (var entry in StartupScan.Run(pc).Entries.ToList())
        {
            var removed = changer.Change(Remove(entry.Id));
            Assert.True(removed.Ok, removed.Error);
            Assert.Null(StartupScan.Find(pc, entry.Id));
            // The command the item was listed with travels with the copy, so a put-back can tell the item from a newcomer with its name.
            Assert.Equal(@"C:\Apps\a.exe", removed.Vault!.Command);
            Assert.True(changer.Change(Restore(entry.Id, removed.Vault!)).Ok);
            Assert.NotNull(StartupScan.Find(pc, entry.Id));
        }

        Assert.Contains($"restore-folder User A.lnk {StartupGuardRuleTests.Shortcut.Length}", pc.Writes);
        Assert.Contains(@"restore-task \Sync " + StartupGuardRuleTests.UserTask, pc.Writes);

    }

    [Fact]
    public void AnItemIsNeverRestoredUnderAnotherItemsName()
    {
        var pc = new FakeStartupPc();
        var vault = new StartupVault(StartupKind.RunKey, StartupScope.User, "Run", "Evil", @"C:\evil.exe");
        Assert.False(new LocalStartupChanger(pc, pc).Change(Restore("run|user|Run|Innocent", vault)).Ok);
        Assert.False(new LocalStartupChanger(pc, pc).Change(new("run|user|Run|Evil", true, Action: StartupAction.Restore)).Ok);
        Assert.False(new LocalStartupChanger(pc, pc).Change(Restore("service|machine|X", vault with { Kind = StartupKind.Service })).Ok);
        Assert.Empty(pc.Writes);
    }

    [Fact]
    public void ProtectedItemsAndServicesCannotBeRemoved()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Windows\System32\SecurityHealthSystray.exe", "Microsoft Corporation"); pc.Add(@"C:\Apps\svc.exe");
        pc.Runs.Add(new(StartupScope.Machine, false, "SecurityHealth", @"C:\Windows\System32\SecurityHealthSystray.exe"));
        pc.ServiceList.Add(new("Acme", "Acme", "", "\"C:\\Apps\\svc.exe\"", 0x10, 2, false, "LocalSystem", []));
        foreach (var entry in StartupScan.Run(pc).Entries)
            Assert.False(new LocalStartupChanger(pc, pc).Change(Remove(entry.Id)).Ok);
        Assert.Empty(pc.Writes);
    }

    [Fact]
    public void ARemovalWindowsDoesNotAcceptIsReportedNotAssumed()
    {
        var (pc, entry) = Pc();
        var outcome = new LocalStartupChanger(pc, new IgnoringWriter()).Change(Remove(entry.Id));
        Assert.False(outcome.Ok);
        Assert.Contains("did not accept", outcome.Error);
    }

    [Fact]
    public void AChangeThatNeedsAdministratorRightsSaysSo()
    {
        var (pc, entry) = Pc();
        pc.DenyWrites = true;
        Assert.Throws<UnauthorizedAccessException>(() => new LocalStartupChanger(pc, pc).Change(new(entry.Id, false)));
    }

    [Fact]
    public void AChangeWindowsDoesNotAcceptIsReportedNotAssumed()
    {
        var (pc, entry) = Pc();
        var ignoring = new IgnoringWriter();
        var outcome = new LocalStartupChanger(pc, ignoring).Change(new(entry.Id, false));
        Assert.False(outcome.Ok);
        Assert.Contains("did not accept", outcome.Error);
    }

    private sealed class IgnoringWriter : IStartupWriter
    {
        public void SetApproved(StartupScope scope, string subKey, string name, bool enabled) { }
        public void SetTask(string path, bool enabled) { }
        public void SetService(string name, int start, bool delayed) { }
        public void SetStoreTask(string family, string taskId, bool enabled) { }
        public void RemoveRun(StartupScope scope, string source, string name) { }
        public void RestoreRun(StartupScope scope, string source, string name, string command, bool expandable) { }
        public byte[] RemoveFolderItem(StartupScope scope, string fileName) => [];
        public void RestoreFolderItem(StartupScope scope, string fileName, byte[] contents) { }
        public string RemoveTask(string path) => "";
        public void RestoreTask(string path, string xml) { }
    }
}

public sealed class StartupServiceTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "tw-startup-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(folder, true); } catch (IOException) { } }

    private sealed class Counter : IStartupChanger
    {
        public int Calls; public Func<StartupRequest, StartupOutcome> Run = _ => new(true);
        public StartupOutcome Change(StartupRequest request) { Calls++; return Run(request); }
    }

    private static (FakeStartupPc Pc, StartupEntry Entry) Machine()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Apps\svc.exe");
        pc.ServiceList.Add(new("Acme", "Acme", "", "\"C:\\Apps\\svc.exe\"", 0x10, 2, true, "LocalSystem", []));
        return (pc, StartupScan.Run(pc).Entries[0]);
    }

    [Fact]
    public async Task ALocalChangeNeverAsksForPermissionAndIsRemembered()
    {
        var (pc, entry) = Machine();
        var unlocks = 0;
        var service = new StartupService(new LocalStartupChanger(pc, pc), new Counter(), () => { unlocks++; return Task.CompletedTask; }, new(folder));

        var outcome = await service.SetAsync(entry, false);

        Assert.True(outcome.Ok); Assert.Equal(0, unlocks);
        var remembered = Assert.Single(service.History.All());
        Assert.Equal("Acme", remembered.Name); Assert.True(remembered.ServiceDelayed);
    }

    [Fact]
    public async Task WhenAdministratorRightsAreNeededThePromptComesOnceAndTheHelperDoesTheWork()
    {
        var (pc, entry) = Machine();
        pc.DenyWrites = true;
        var unlocks = 0;
        var helper = new Counter { Run = r => new LocalStartupChanger(pc, new Allowing(pc)).Change(r) };
        var service = new StartupService(new LocalStartupChanger(pc, pc), helper, () => { unlocks++; return Task.CompletedTask; }, new(folder));

        Assert.True((await service.SetAsync(entry, false)).Ok);
        Assert.Equal(1, unlocks); Assert.Equal(1, helper.Calls);
    }

    [Fact]
    public async Task AHelperThatLeftWhileIdleIsReplacedAndTheChangeIsMadeInTheSameClick()
    {
        var (pc, entry) = Machine();
        pc.DenyWrites = true;
        var unlocks = 0;
        var helper = new Counter();
        helper.Run = r => helper.Calls == 1 ? throw new IOException("The administrator helper stopped. Try again.") : new LocalStartupChanger(pc, new Allowing(pc)).Change(r);
        var service = new StartupService(new LocalStartupChanger(pc, pc), helper, () => { unlocks++; return Task.CompletedTask; }, new(folder));

        Assert.True((await service.SetAsync(entry, false)).Ok);
        Assert.Equal(2, unlocks); Assert.Equal(2, helper.Calls);
    }

    private sealed class Allowing(FakeStartupPc pc) : IStartupWriter
    {
        public void SetApproved(StartupScope scope, string subKey, string name, bool enabled) { pc.DenyWrites = false; pc.SetApproved(scope, subKey, name, enabled); pc.DenyWrites = true; }
        public void SetTask(string path, bool enabled) { pc.DenyWrites = false; pc.SetTask(path, enabled); pc.DenyWrites = true; }
        public void SetService(string name, int start, bool delayed) { pc.DenyWrites = false; pc.SetService(name, start, delayed); pc.DenyWrites = true; }
        public void SetStoreTask(string family, string taskId, bool enabled) { pc.DenyWrites = false; pc.SetStoreTask(family, taskId, enabled); pc.DenyWrites = true; }
        public void RemoveRun(StartupScope scope, string source, string name) { pc.DenyWrites = false; pc.RemoveRun(scope, source, name); pc.DenyWrites = true; }
        public void RestoreRun(StartupScope scope, string source, string name, string command, bool expandable) { pc.DenyWrites = false; pc.RestoreRun(scope, source, name, command, expandable); pc.DenyWrites = true; }
        public byte[] RemoveFolderItem(StartupScope scope, string fileName) { pc.DenyWrites = false; var bytes = pc.RemoveFolderItem(scope, fileName); pc.DenyWrites = true; return bytes; }
        public void RestoreFolderItem(StartupScope scope, string fileName, byte[] contents) { pc.DenyWrites = false; pc.RestoreFolderItem(scope, fileName, contents); pc.DenyWrites = true; }
        public string RemoveTask(string path) { pc.DenyWrites = false; var xml = pc.RemoveTask(path); pc.DenyWrites = true; return xml; }
        public void RestoreTask(string path, string xml) { pc.DenyWrites = false; pc.RestoreTask(path, xml); pc.DenyWrites = true; }
    }

    [Fact]
    public async Task ADeclinedPromptChangesNothingAndRemembersNothing()
    {
        var (pc, entry) = Machine();
        pc.DenyWrites = true;
        var service = new StartupService(new LocalStartupChanger(pc, pc), new Counter(), () => throw new OperationCanceledException(), new(folder));

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.SetAsync(entry, false));
        Assert.Empty(pc.Writes); Assert.Empty(service.History.All());
    }

    [Fact]
    public async Task ProtectedEntriesNeverReachAnyChanger()
    {
        var pc = new FakeStartupPc();
        pc.Settings.Add(new("Winlogon.Shell", "Shell", "explorer.exe", "explorer.exe", "Winlogon"));
        var entry = StartupScan.Run(pc).Entries[0];
        var user = new Counter(); var machine = new Counter();
        var service = new StartupService(user, machine, () => Task.CompletedTask, new(folder));

        Assert.False((await service.SetAsync(entry, false)).Ok);
        Assert.Equal(0, user.Calls + machine.Calls);
    }

    [Fact]
    public async Task TurningItBackOnForgetsItAndRestoreAllTurnsEverythingBackOn()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Apps\a.exe"); pc.Add(@"C:\Apps\b.exe");
        pc.Runs.Add(new(StartupScope.User, false, "A", @"C:\Apps\a.exe"));
        pc.Runs.Add(new(StartupScope.User, false, "B", @"C:\Apps\b.exe"));
        var service = new StartupService(new LocalStartupChanger(pc, pc), new Counter(), () => Task.CompletedTask, new(folder));
        var entries = StartupScan.Run(pc).Entries;

        await service.SetAsync(entries[0], false); await service.SetAsync(entries[1], false);
        Assert.Equal(2, service.History.All().Count);

        var results = await service.RestoreAllAsync(pc);

        Assert.Equal(2, results.Count); Assert.All(results, x => Assert.True(x.Outcome.Ok));
        Assert.Empty(service.History.All());
        Assert.All(StartupScan.Run(pc).Entries, x => Assert.True(x.Enabled));
    }

    [Fact]
    public async Task ARemovedItemIsKeptWithItsCopyAndRestoreAllPutsItBack()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Apps\setup.exe");
        pc.Runs.Add(new(StartupScope.User, false, "Setup", @"C:\Apps\setup.exe", "RunOnce"));
        var service = new StartupService(new LocalStartupChanger(pc, pc), new Counter(), () => Task.CompletedTask, new(folder));
        var entry = StartupScan.Run(pc).Entries[0];

        Assert.True((await service.RemoveAsync(entry)).Ok);

        var kept = Assert.Single(service.History.All());
        Assert.True(kept.Removed);
        Assert.Equal(@"C:\Apps\setup.exe", kept.Vault!.Command);
        Assert.Empty(pc.Runs);
        // Reloaded from disk, the copy is still whole.
        Assert.Equal(kept.Vault, Assert.Single(new StartupHistory(folder).All()).Vault);

        var results = await service.RestoreAllAsync(pc);

        Assert.True(Assert.Single(results).Outcome.Ok);
        Assert.Empty(service.History.All());
        Assert.Equal(@"C:\Apps\setup.exe", Assert.Single(pc.Runs).Command);
    }

    [Fact]
    public async Task RemovingAMachineItemAsksForPermissionOnceAndTheHelperDoesIt()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Apps\a.exe");
        pc.Runs.Add(new(StartupScope.Machine, false, "A", @"C:\Apps\a.exe"));
        pc.DenyWrites = true;
        var unlocks = 0;
        var helper = new Counter { Run = r => new LocalStartupChanger(pc, new Allowing(pc)).Change(r) };
        var service = new StartupService(new LocalStartupChanger(pc, pc), helper, () => { unlocks++; return Task.CompletedTask; }, new(folder));

        Assert.True((await service.RemoveAsync(StartupScan.Run(pc).Entries[0])).Ok);

        Assert.Equal(1, unlocks);
        Assert.True(Assert.Single(service.History.All()).Removed);
    }

    [Fact]
    public async Task AFailedRemovalRemembersNothingAndProtectedItemsNeverReachAChanger()
    {
        var (pc, entry) = Machine();
        var user = new Counter { Run = _ => new(false, "no") };
        var service = new StartupService(user, new Counter(), () => Task.CompletedTask, new(folder));
        Assert.False((await service.RemoveAsync(entry with { Kind = StartupKind.Service })).Ok);
        Assert.Equal(0, user.Calls);
        var run = entry with { Kind = StartupKind.RunKey };
        Assert.False((await service.RemoveAsync(run)).Ok);
        Assert.Empty(service.History.All());
    }

    [Fact]
    public async Task ServicesComeBackWithTheirOriginalDelayedStart()
    {
        var (pc, entry) = Machine();
        var service = new StartupService(new LocalStartupChanger(pc, pc), new Counter(), () => Task.CompletedTask, new(folder));
        await service.SetAsync(entry, false);
        Assert.Equal(3, pc.ServiceList[0].Start);

        await service.RestoreAllAsync(pc);

        Assert.Equal(2, pc.ServiceList[0].Start); Assert.True(pc.ServiceList[0].Delayed);
    }

    [Fact]
    public async Task ItemsThatAreGoneOrAlreadyOnAreDroppedFromTheHistory()
    {
        var pc = new FakeStartupPc();
        var history = new StartupHistory(folder);
        history.Add(new("run|user|Run|Gone", "Gone", StartupKind.RunKey, StartupScope.User, DateTimeOffset.Now));
        var service = new StartupService(new LocalStartupChanger(pc, pc), new Counter(), () => Task.CompletedTask, history);

        Assert.Empty(await service.RestoreAllAsync(pc));
        Assert.Empty(history.All());
    }

    [Fact]
    public void TheHistorySurvivesRestartsAndABrokenFile()
    {
        new StartupHistory(folder).Add(new("a", "A", StartupKind.RunKey, StartupScope.User, DateTimeOffset.Now));
        Assert.Equal("A", Assert.Single(new StartupHistory(folder).All()).Name);
        File.WriteAllText(Path.Combine(folder, "turned-off.json"), "{ not json");
        Assert.Empty(new StartupHistory(folder).All());
    }
}

public sealed class StartupHostTests
{
    private static StartupSession Session(List<StartupRequest> seen, bool admin = true, Func<StartupRequest, StartupOutcome>? answer = null) =>
        new(pipe => StartupHost.RunAsync([StartupHost.Command, pipe], new Recorder(seen, answer ?? (_ => new(true))), () => admin, TimeSpan.FromSeconds(20)));

    private sealed class Recorder(List<StartupRequest> seen, Func<StartupRequest, StartupOutcome> answer) : IStartupChanger
    {
        public StartupOutcome Change(StartupRequest request) { seen.Add(request); return answer(request); }
    }

    [Fact]
    public async Task TheHelperPassesAChangeToTheChangerAndReturnsTheOutcome()
    {
        var seen = new List<StartupRequest>();
        using var session = Session(seen);
        await session.OpenAsync();

        var outcome = session.Change(new("service|machine|Acme", false, 2, true));

        Assert.True(outcome.Ok);
        Assert.Equal(new StartupRequest("service|machine|Acme", false, 2, true), Assert.Single(seen));
    }

    [Fact]
    public async Task AnErrorFromTheHelperComesBackAsAFailedOutcome()
    {
        using var session = Session([], answer: _ => new(false, "Windows did not accept the change."));
        await session.OpenAsync();
        var outcome = session.Change(new("x", false));
        Assert.False(outcome.Ok);
        Assert.Equal("Windows did not accept the change.", outcome.Error);
    }

    [Fact]
    public async Task AHelperWithoutAdministratorRightsIsNotAccepted()
    {
        using var session = Session([], admin: false);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.OpenAsync());
        Assert.False(session.IsOpen);
    }

    [Fact]
    public async Task ADeclinedPromptStopsEverything()
    {
        using var session = new StartupSession(_ => throw new OperationCanceledException("declined"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.OpenAsync());
        Assert.False(session.IsOpen);
    }

    [Fact]
    public async Task ClosingTheSessionStopsTheHelper()
    {
        var finished = new TaskCompletionSource();
        using var session = new StartupSession(async pipe =>
        {
            await StartupHost.RunAsync([StartupHost.Command, pipe], new Recorder([], _ => new(true)), () => true, TimeSpan.FromSeconds(20));
            finished.SetResult();
        });
        await session.OpenAsync();

        session.Close();

        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(session.IsOpen);
    }
}
