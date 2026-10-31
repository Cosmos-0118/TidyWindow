using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

internal sealed class FakeBackgroundPc : IBackgroundSystem, IBackgroundWriter
{
    public string WindowsFolder => @"C:\Windows";
    public string Edition { get; set; } = "Professional";
    public string Expand(string text) => text.Replace("%ProgramFiles%", @"C:\Program Files", StringComparison.OrdinalIgnoreCase);
    public Dictionary<string, ServiceFact> ServiceList = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, TaskFact> TaskList = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> Policies = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Writes = [];
    public bool DenyWrites, IgnoreWrites;
    /// <summary>The write that fails, such as "task", to test undoing a half-finished change.</summary>
    public string? FailOn;
    public HashSet<string> WillNotStop = new(StringComparer.OrdinalIgnoreCase);

    public void Refresh() { }
    public ServiceFact? Service(string name) => ServiceList.GetValueOrDefault(name);
    public IReadOnlyList<ServiceFact> Dependents(string name) => ServiceList.Values.Where(x => x.Start != 4 && x.DependsOn.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
    public TaskFact? Task(string path) => TaskList.GetValueOrDefault(path);
    public int? Policy(string key, string name) => Policies.TryGetValue(key + "|" + name, out var value) ? value : null;

    public FakeBackgroundPc Svc(string name, int start = 3, bool running = false, bool delayed = false, string[]? depends = null, string image = @"%SystemRoot%\System32\svchost.exe -k netsvcs -p", int type = 32, string display = "")
    {
        ServiceList[name] = new(name, display.Length > 0 ? display : name, start, delayed, running, false, image, type, depends ?? []);
        return this;
    }

    public FakeBackgroundPc Job(string path, bool enabled = true, bool running = false)
    {
        TaskList[path] = new(path, enabled, running);
        return this;
    }

    private void Guard(string what)
    {
        if (DenyWrites) throw new UnauthorizedAccessException("denied");
        if (FailOn == what) throw new IOException("the write failed");
    }

    public void SetServiceStart(string name, int start, bool delayed)
    {
        Guard("service"); Writes.Add($"start {name} {start} {delayed}");
        if (!IgnoreWrites) ServiceList[name] = ServiceList[name] with { Start = start, Delayed = delayed };
    }

    public bool StopService(string name)
    {
        Guard("stop"); Writes.Add($"stop {name}");
        if (WillNotStop.Contains(name)) return false;
        ServiceList[name] = ServiceList[name] with { Running = false };
        return true;
    }

    public void StartService(string name)
    {
        Guard("begin"); Writes.Add($"begin {name}");
        ServiceList[name] = ServiceList[name] with { Running = true };
    }

    public void SetTask(string path, bool enabled)
    {
        Guard("task"); Writes.Add($"task {path} {enabled}");
        if (!IgnoreWrites) TaskList[path] = TaskList[path] with { Enabled = enabled, Running = enabled && TaskList[path].Running };
    }

    public void SetPolicy(string key, string name, int? value)
    {
        Guard("policy"); Writes.Add($"policy {name} {(value is null ? "removed" : value.ToString())}");
        if (IgnoreWrites) return;
        if (value is null) Policies.Remove(key + "|" + name); else Policies[key + "|" + name] = value.Value;
    }
}

public sealed class BackgroundCatalogTests
{
    [Fact]
    public void TheShippedCatalogIsValidAndReadyToUse()
    {
        var catalog = BackgroundCatalog.Load();
        Assert.True(catalog.Count >= 20);
        Assert.Empty(BackgroundGuard.Validate(catalog));
        Assert.Contains(catalog, x => x.Group == BackgroundGroup.Telemetry);
        Assert.Contains(catalog, x => x.Group == BackgroundGroup.Ai);
        Assert.Contains(catalog, x => x.Group == BackgroundGroup.Optional);
        Assert.All(catalog, x => { Assert.NotEmpty(x.Summary); Assert.NotEmpty(x.Effect); });
    }

    [Fact]
    public void NothingTheOldAppBrokeWhatsAppWithCanBeInTheCatalog()
    {
        var names = BackgroundCatalog.Load().SelectMany(x => x.Targets).Where(x => x.Kind == BackgroundTargetKind.Service).Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var protectedName in new[] { "WpnService", "AppXSvc", "StateRepository", "ClipSVC", "InstallService", "BITS", "wuauserv", "WSearch", "Audiosrv", "Spooler", "DoSvc", "camsvc", "StorSvc", "CryptSvc" })
            Assert.DoesNotContain(protectedName, names);
        Assert.All(names, name => Assert.DoesNotMatch(@"_[0-9a-f]{4,}$|\*", name));
    }

    [Fact]
    public void EveryServiceInTheCatalogUsesTheGentlestWayThatStillWorks()
    {
        foreach (var target in BackgroundCatalog.Load().SelectMany(x => x.Targets).Where(x => x.Kind == BackgroundTargetKind.Service))
        {
            Assert.Contains(target.Off, new[] { "disabled", "manual" });
            // A service that normally starts by itself is only ever made manual; "disabled" is for ones nothing starts on its own.
            if (target.DefaultStart == 2 && target.Off == "disabled") Assert.Contains(target.Name, new[] { "DiagTrack", "SysMain" });
        }
    }

    private static BackgroundItem Item(params BackgroundTarget[] targets) => new() { Id = "item", Name = "Item", Group = BackgroundGroup.Optional, Summary = "s", Effect = "e", Targets = targets };
    private static BackgroundTarget Service(string name, string off = "disabled", int start = 3) => new() { Kind = BackgroundTargetKind.Service, Name = name, Off = off, DefaultStart = start };

    [Theory]
    [InlineData("WpnService")]
    [InlineData("wpnservice")]
    [InlineData("AppXSvc")]
    [InlineData("WSearch")]
    [InlineData("WpnUserService_1b934f7")]
    [InlineData("CDPUserSvc_1b934f7")]
    [InlineData("Some*")]
    public void ServicesTheSystemLeansOnAreRefused(string name) =>
        Assert.NotEmpty(BackgroundGuard.Validate([Item(Service(name))]));

    [Fact]
    public void BadCatalogEntriesAreExplained()
    {
        Assert.NotEmpty(BackgroundGuard.Validate([Item(new BackgroundTarget { Kind = BackgroundTargetKind.Service, Name = "Fine", Off = "kill" })]));
        Assert.NotEmpty(BackgroundGuard.Validate([Item(new BackgroundTarget { Kind = BackgroundTargetKind.Service, Name = "Fine", DefaultStart = 4 })]));
        Assert.NotEmpty(BackgroundGuard.Validate([Item(new BackgroundTarget { Kind = BackgroundTargetKind.Task, Name = @"\Microsoft\Windows\Windows Defender\Windows Defender Scheduled Scan" })]));
        Assert.NotEmpty(BackgroundGuard.Validate([Item(new BackgroundTarget { Kind = BackgroundTargetKind.Task, Name = @"\Microsoft\Windows\Maps\*" })]));
        Assert.NotEmpty(BackgroundGuard.Validate([Item(new BackgroundTarget { Kind = BackgroundTargetKind.Task, Name = "NoSlash" })]));
        Assert.NotEmpty(BackgroundGuard.Validate([Item(new BackgroundTarget { Kind = BackgroundTargetKind.Policy, Key = @"SOFTWARE\Microsoft\Windows Defender", Name = "DisableAntiSpyware" })]));
        Assert.NotEmpty(BackgroundGuard.Validate([Item(Service("Fine")), Item(Service("Other"))]));
        Assert.NotEmpty(BackgroundGuard.Validate([new BackgroundItem { Id = "x", Name = "X", Group = BackgroundGroup.Ai, Summary = "s", Effect = "e", Targets = [] }]));
    }

    [Fact]
    public void TheCatalogThatShipsNeverNamesAnythingOutsideItsAllowedPlaces()
    {
        foreach (var target in BackgroundCatalog.Load().SelectMany(x => x.Targets).Where(x => x.Kind == BackgroundTargetKind.Policy))
            Assert.Matches(@"^SOFTWARE\\Policies\\Microsoft\\(Windows\\WindowsAI|Dsh|Windows\\GameDVR)$", target.Key);
    }
}

public sealed class BackgroundScanTests
{
    private static BackgroundItem Item(string id, params BackgroundTarget[] targets) => new() { Id = id, Name = id, Group = BackgroundGroup.Optional, Summary = "s", Effect = "e", Targets = targets };
    private static BackgroundTarget Svc(string name, string off = "disabled", int start = 3) => new() { Kind = BackgroundTargetKind.Service, Name = name, Off = off, DefaultStart = start };
    private static BackgroundTarget Job(string path) => new() { Kind = BackgroundTargetKind.Task, Name = path };
    private static BackgroundTarget Policy(bool pro = false) => new() { Kind = BackgroundTargetKind.Policy, Key = @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", Name = "DisableAIDataAnalysis", NeedsPro = pro };
    private const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI|DisableAIDataAnalysis";

    private static BackgroundEntry One(FakeBackgroundPc pc, params BackgroundTarget[] targets) => Assert.Single(BackgroundScan.Run(pc, [Item("item", targets)]).Entries);

    [Fact]
    public void WhatIsNotOnThisPcIsLeftOutAndCounted()
    {
        var pc = new FakeBackgroundPc().Svc("Here");
        var result = BackgroundScan.Run(pc, [Item("a", Svc("Here")), Item("b", Svc("Gone")), Item("c", Job(@"\x\y"))]);
        Assert.Equal(["a"], result.Entries.Select(x => x.Id));
        Assert.Equal(2, result.NotOnThisPc);
    }

    [Fact]
    public void AServiceIsOnUntilItIsDisabledAndRunningIsSeparate()
    {
        var pc = new FakeBackgroundPc().Svc("S", start: 3);
        var idle = One(pc, Svc("S"));
        Assert.True(idle.On); Assert.False(idle.Running);

        pc.Svc("S", start: 2, running: true);
        Assert.True(One(pc, Svc("S")).Running);

        pc.Svc("S", start: 4);
        Assert.False(One(pc, Svc("S")).On);
    }

    [Fact]
    public void AServiceThatIsMadeManualCountsAsOffOnlyForItems_ThatTurnOffThatWay()
    {
        var pc = new FakeBackgroundPc().Svc("S", start: 3);
        Assert.False(One(pc, Svc("S", "manual", 2)).On);
        Assert.True(One(pc, Svc("S", "disabled", 2)).On);
        pc.Svc("S", start: 2);
        Assert.True(One(pc, Svc("S", "manual", 2)).On);
    }

    [Fact]
    public void ATaskAndAPolicyReportTheirOwnState()
    {
        var pc = new FakeBackgroundPc().Job(@"\Microsoft\a\b", enabled: true, running: true);
        var task = One(pc, Job(@"\Microsoft\a\b"));
        Assert.True(task.On); Assert.True(task.Running);
        pc.Job(@"\Microsoft\a\b", enabled: false);
        Assert.False(One(pc, Job(@"\Microsoft\a\b")).On);

        Assert.True(One(pc, Policy()).On);
        pc.Policies[PolicyKey] = 1;
        Assert.False(One(pc, Policy()).On);
        pc.Policies[PolicyKey] = 0;
        Assert.True(One(pc, Policy()).On);
    }

    [Fact]
    public void ARunningServiceThatOthersNeedIsLockedButCanStillBeTurnedBackOn()
    {
        var pc = new FakeBackgroundPc().Svc("Base", start: 2, running: true).Svc("Needy", start: 2, running: true, depends: ["base"], display: "Needy Service");
        var entry = One(pc, Svc("Base", "manual", 2));
        Assert.Contains("Needy Service", entry.Lock); Assert.False(entry.CanChange);

        pc.Svc("Needy", start: 3, running: false);
        Assert.True(One(pc, Svc("Base", "manual", 2)).CanChange);
    }

    [Fact]
    public void WhenAServiceWouldBeDisabledAnythingThatCouldStartAndNeedsItBlocksIt()
    {
        var pc = new FakeBackgroundPc().Svc("Base").Svc("Needy", start: 3, running: false, depends: ["Base"]);
        Assert.False(One(pc, Svc("Base")).CanChange);
        pc.Svc("Needy", start: 4);
        Assert.True(One(pc, Svc("Base")).CanChange);
    }

    [Fact]
    public void ServicesFromTheSameItemDoNotBlockEachOther()
    {
        var pc = new FakeBackgroundPc().Svc("Auth").Svc("Save", depends: ["Auth"]);
        Assert.True(One(pc, Svc("Auth"), Svc("Save")).CanChange);
    }

    [Fact]
    public void ADisabledServiceWithDependentsCanStillBeTurnedBackOn()
    {
        var pc = new FakeBackgroundPc().Svc("Base", start: 4).Svc("Needy", start: 3, depends: ["Base"]);
        var entry = One(pc, Svc("Base"));
        Assert.False(entry.On); Assert.True(entry.CanChange);
    }

    [Fact]
    public void AServiceThatWindowsDidNotShipOrThatIsPerUserIsLeftAlone()
    {
        var pc = new FakeBackgroundPc().Svc("Vendor", image: @"C:\Program Files\Acme\svc.exe").Svc("Template", type: 0xE0);
        Assert.False(One(pc, Svc("Vendor")).CanChange);
        Assert.False(One(pc, Svc("Template")).CanChange);
        pc.Svc("InWindows", image: @"""C:\Windows\System32\thing.exe"" -k x");
        Assert.True(One(pc, Svc("InWindows")).CanChange);
    }

    [Fact]
    public void AMicrosoftServiceOutsideWindowsIsAcceptedOnlyWhereTheCatalogSaysItLives()
    {
        var pc = new FakeBackgroundPc().Svc("Media", image: @"""%PROGRAMFILES%\Windows Media Player\wmpnetwk.exe""");
        var target = new BackgroundTarget { Kind = BackgroundTargetKind.Service, Name = "Media", Image = @"%ProgramFiles%\Windows Media Player\" };
        Assert.True(One(pc, target).CanChange);
        pc.Svc("Media", image: @"C:\Program Files\Evil\wmpnetwk.exe");
        Assert.False(One(pc, target).CanChange);
        Assert.False(One(pc, Svc("Media")).CanChange);
    }

    [Theory]
    [InlineData(@"%SystemRoot%\System32\svchost.exe -k netsvcs -p", true)]
    [InlineData(@"\SystemRoot\System32\drivers\x.sys", true)]
    [InlineData(@"C:\Windows\System32\svchost.exe", true)]
    [InlineData(@"C:\WindowsApps\evil.exe", false)]
    [InlineData(@"C:\Windows.old\x.exe", false)]
    [InlineData("", false)]
    public void WindowsOwnedProgramsAreTellableFromLookalikes(string image, bool expected) =>
        Assert.Equal(expected, BackgroundGuard.IsInWindows(image, @"C:\Windows"));

    [Fact]
    public void WindowsHomeCannotUseThePolicyThatNeedsPro()
    {
        var pc = new FakeBackgroundPc { Edition = "Core" };
        var entry = One(pc, Policy(pro: true));
        Assert.False(entry.CanChange);
        Assert.Contains("Home", entry.Lock);
        pc.Edition = "Professional";
        Assert.True(One(pc, Policy(pro: true)).CanChange);
    }

    [Fact]
    public void AnItemIsFullyOnOnlyWhenEveryPartIs()
    {
        var pc = new FakeBackgroundPc().Svc("A").Svc("B", start: 4);
        var entry = One(pc, Svc("A"), Svc("B"));
        Assert.True(entry.On); Assert.False(entry.FullyOn);
    }
}

public sealed class BackgroundChangeTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "tw-bg-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(folder, true); } catch (IOException) { } }

    private static BackgroundItem Item(params BackgroundTarget[] targets) => new() { Id = "item", Name = "The item", Group = BackgroundGroup.Optional, Summary = "s", Effect = "e", Targets = targets };
    private static BackgroundTarget Svc(string name, string off = "disabled", int start = 3, bool delayed = false) => new() { Kind = BackgroundTargetKind.Service, Name = name, Off = off, DefaultStart = start, DefaultDelayed = delayed };
    private static BackgroundTarget Job(string path) => new() { Kind = BackgroundTargetKind.Task, Name = path };
    private static BackgroundTarget Block(int value = 1) => new() { Kind = BackgroundTargetKind.Policy, Key = @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", Name = "DisableAIDataAnalysis", Block = value };
    private const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI|DisableAIDataAnalysis";

    private static LocalBackgroundChanger Changer(FakeBackgroundPc pc, BackgroundItem item) => new(pc, pc, [item]);
    private static BackgroundRequest Off => new("item", false);

    [Fact]
    public void ARunningServiceIsDisabledAndStoppedAndItsOldStateIsReturned()
    {
        var pc = new FakeBackgroundPc().Svc("Diag", start: 2, running: true, delayed: true);
        var outcome = Changer(pc, Item(Svc("Diag", start: 2))).Change(Off);

        Assert.True(outcome.Ok);
        Assert.Equal(["start Diag 4 False", "stop Diag"], pc.Writes);
        var saved = Assert.Single(outcome.Before!);
        Assert.True(saved.WasOn); Assert.Equal(2, saved.Start); Assert.True(saved.Delayed);
    }

    [Fact]
    public void ManualIsUsedInsteadOfDisabledWhereTheItemSaysSo()
    {
        var pc = new FakeBackgroundPc().Svc("Maps", start: 2, running: false);
        Assert.True(Changer(pc, Item(Svc("Maps", "manual", 2))).Change(Off).Ok);
        Assert.Equal(["start Maps 3 False"], pc.Writes);
    }

    [Fact]
    public void AServiceThatWillNotStopIsStillTurnedOffAndTheOutcomeSaysWhatToDo()
    {
        var pc = new FakeBackgroundPc().Svc("Stuck", start: 2, running: true);
        pc.WillNotStop.Add("Stuck");
        var outcome = Changer(pc, Item(Svc("Stuck", start: 2))).Change(Off);
        Assert.True(outcome.Ok);
        Assert.Contains("restart", outcome.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TurningItBackOnPutsTheSavedStateBack()
    {
        var pc = new FakeBackgroundPc().Svc("Diag", start: 2, running: true, delayed: true);
        var changer = Changer(pc, Item(Svc("Diag", start: 3)));
        var off = changer.Change(Off);
        pc.Writes.Clear();

        var on = changer.Change(new("item", true, off.Before));

        Assert.True(on.Ok);
        Assert.Equal(["start Diag 2 True", "begin Diag"], pc.Writes);
        Assert.Equal(2, pc.ServiceList["Diag"].Start); Assert.True(pc.ServiceList["Diag"].Delayed);
    }

    [Fact]
    public void WithoutASavedRecordTheCatalogDefaultIsUsed()
    {
        var pc = new FakeBackgroundPc().Svc("X", start: 4);
        Assert.True(Changer(pc, Item(Svc("X", start: 3))).Change(new("item", true)).Ok);
        Assert.Equal(3, pc.ServiceList["X"].Start);
        Assert.DoesNotContain("begin X", pc.Writes);
    }

    [Fact]
    public void AnItemWithATaskAndAPolicyIsFullySwitchedAndRestored()
    {
        var pc = new FakeBackgroundPc().Job(@"\Microsoft\a\b", running: true);
        pc.Policies[PolicyKey] = 0;
        var changer = Changer(pc, Item(Job(@"\Microsoft\a\b"), Block()));

        var off = changer.Change(Off);
        Assert.True(off.Ok);
        Assert.False(pc.TaskList[@"\Microsoft\a\b"].Enabled); Assert.Equal(1, pc.Policies[PolicyKey]);

        Assert.True(changer.Change(new("item", true, off.Before)).Ok);
        Assert.True(pc.TaskList[@"\Microsoft\a\b"].Enabled); Assert.Equal(0, pc.Policies[PolicyKey]);
    }

    [Fact]
    public void APolicyThatDidNotExistIsRemovedAgainNotLeftBehindAsAZero()
    {
        var pc = new FakeBackgroundPc();
        var changer = Changer(pc, Item(Block()));
        var off = changer.Change(Off);
        Assert.Equal(1, pc.Policies[PolicyKey]);

        Assert.True(changer.Change(new("item", true, off.Before)).Ok);
        Assert.False(pc.Policies.ContainsKey(PolicyKey));
    }

    [Fact]
    public void APolicyThatBlocksByZeroIsHandledToo()
    {
        var pc = new FakeBackgroundPc();
        var changer = Changer(pc, Item(new BackgroundTarget { Kind = BackgroundTargetKind.Policy, Key = @"SOFTWARE\Policies\Microsoft\Dsh", Name = "AllowNewsAndInterests", Block = 0 }));
        var off = changer.Change(Off);
        Assert.True(off.Ok);
        Assert.Equal(0, pc.Policies[@"SOFTWARE\Policies\Microsoft\Dsh|AllowNewsAndInterests"]);
        Assert.True(changer.Change(new("item", true, off.Before)).Ok);
        Assert.Empty(pc.Policies);
    }

    [Fact]
    public void APartThatWasAlreadyOffStaysOffWhenTheRestComeBack()
    {
        var pc = new FakeBackgroundPc().Job(@"\Microsoft\a\on").Job(@"\Microsoft\a\off", enabled: false);
        var changer = Changer(pc, Item(Job(@"\Microsoft\a\on"), Job(@"\Microsoft\a\off")));
        var off = changer.Change(Off);

        Assert.True(changer.Change(new("item", true, off.Before)).Ok);
        Assert.True(pc.TaskList[@"\Microsoft\a\on"].Enabled); Assert.False(pc.TaskList[@"\Microsoft\a\off"].Enabled);
    }

    [Fact]
    public void AChangeThatFailsHalfWayIsUndone()
    {
        var pc = new FakeBackgroundPc().Svc("S", start: 2, running: false).Job(@"\Microsoft\a\b");
        pc.FailOn = "task";
        var outcome = Changer(pc, Item(Svc("S", start: 2), Job(@"\Microsoft\a\b"))).Change(Off);

        Assert.False(outcome.Ok);
        Assert.Equal(2, pc.ServiceList["S"].Start);
        Assert.True(pc.TaskList[@"\Microsoft\a\b"].Enabled);
    }

    [Fact]
    public void ALyingWindowsIsNotBelieved()
    {
        var pc = new FakeBackgroundPc { IgnoreWrites = true }.Svc("S", start: 3);
        var outcome = Changer(pc, Item(Svc("S"))).Change(Off);
        Assert.False(outcome.Ok);
        Assert.Contains("did not accept", outcome.Error);
    }

    [Fact]
    public void MissingRightsAreReportedSoTheHelperCanBeAsked()
    {
        var pc = new FakeBackgroundPc { DenyWrites = true }.Svc("S");
        Assert.Throws<UnauthorizedAccessException>(() => Changer(pc, Item(Svc("S"))).Change(Off));
    }

    [Fact]
    public void LockedAndMissingItemsNeverReachTheWriter()
    {
        var pc = new FakeBackgroundPc().Svc("Base").Svc("Needy", depends: ["Base"]);
        var changer = Changer(pc, Item(Svc("Base")));
        var outcome = changer.Change(Off);
        Assert.False(outcome.Ok); Assert.Contains("relies on it", outcome.Error);
        Assert.Empty(pc.Writes);

        Assert.False(new LocalBackgroundChanger(pc, pc, []).Change(Off).Ok);
        Assert.Empty(pc.Writes);
    }

    [Fact]
    public void TurningOffSomethingAlreadyOffDoesNothing()
    {
        var pc = new FakeBackgroundPc().Svc("S", start: 4);
        Assert.True(Changer(pc, Item(Svc("S"))).Change(Off).Ok);
        Assert.Empty(pc.Writes);
    }

    // ---- the page's service: who makes the change, and the history that makes it reversible ----

    private sealed class Counter : IBackgroundChanger
    {
        public int Calls; public Func<BackgroundRequest, BackgroundOutcome> Run = _ => new(true);
        public BackgroundOutcome Change(BackgroundRequest request) { Calls++; return Run(request); }
    }

    private static (FakeBackgroundPc Pc, BackgroundItem Item, BackgroundEntry Entry) Machine()
    {
        var pc = new FakeBackgroundPc().Svc("Diag", start: 2, running: true, delayed: true);
        var item = Item(Svc("Diag", start: 2));
        return (pc, item, Assert.Single(BackgroundScan.Run(pc, [item]).Entries));
    }

    [Fact]
    public async Task ALocalChangeNeverAsksForPermissionAndIsRemembered()
    {
        var (pc, item, entry) = Machine();
        var unlocks = 0;
        var manager = new BackgroundManager(Changer(pc, item), new Counter(), () => { unlocks++; return Task.CompletedTask; }, new(folder));

        Assert.True((await manager.SetAsync(entry, false)).Ok);

        Assert.Equal(0, unlocks);
        var remembered = Assert.Single(manager.History.All());
        Assert.Equal("The item", remembered.Name); Assert.True(Assert.Single(remembered.Saved).Delayed);
    }

    [Fact]
    public async Task WhenAdministratorRightsAreNeededThePromptComesOnceAndTheHelperDoesTheWork()
    {
        var (pc, item, entry) = Machine();
        pc.DenyWrites = true;
        var unlocks = 0;
        var helper = new Counter { Run = r => { pc.DenyWrites = false; try { return Changer(pc, item).Change(r); } finally { pc.DenyWrites = true; } } };
        var manager = new BackgroundManager(Changer(pc, item), helper, () => { unlocks++; return Task.CompletedTask; }, new(folder));

        Assert.True((await manager.SetAsync(entry, false)).Ok);
        Assert.Equal(1, unlocks); Assert.Equal(1, helper.Calls);
        Assert.Single(manager.History.All());
    }

    [Fact]
    public async Task ADeclinedPromptChangesNothingAndRemembersNothing()
    {
        var (pc, item, entry) = Machine();
        pc.DenyWrites = true;
        var manager = new BackgroundManager(Changer(pc, item), new Counter(), () => throw new OperationCanceledException(), new(folder));

        await Assert.ThrowsAsync<OperationCanceledException>(() => manager.SetAsync(entry, false));
        Assert.Empty(pc.Writes); Assert.Empty(manager.History.All());
    }

    [Fact]
    public async Task ALockedEntryNeverReachesAnyChanger()
    {
        var pc = new FakeBackgroundPc().Svc("Base").Svc("Needy", depends: ["Base"]);
        var entry = Assert.Single(BackgroundScan.Run(pc, [Item(Svc("Base"))]).Entries);
        var user = new Counter(); var machine = new Counter();
        var manager = new BackgroundManager(user, machine, () => Task.CompletedTask, new(folder));

        Assert.False((await manager.SetAsync(entry, false)).Ok);
        Assert.Equal(0, user.Calls + machine.Calls);
    }

    [Fact]
    public async Task RestoreAllBringsEverythingBackExactlyAsItWasAndForgetsIt()
    {
        var (pc, item, entry) = Machine();
        var manager = new BackgroundManager(Changer(pc, item), new Counter(), () => Task.CompletedTask, new(folder));
        await manager.SetAsync(entry, false);
        Assert.Equal(4, pc.ServiceList["Diag"].Start);

        var results = await manager.RestoreAllAsync(pc, [item]);

        Assert.True(Assert.Single(results).Outcome.Ok);
        Assert.Empty(manager.History.All());
        Assert.Equal(2, pc.ServiceList["Diag"].Start); Assert.True(pc.ServiceList["Diag"].Delayed); Assert.True(pc.ServiceList["Diag"].Running);
    }

    [Fact]
    public async Task ItemsThatAreGoneOrWereTurnedBackOnElsewhereAreDroppedFromTheHistory()
    {
        var pc = new FakeBackgroundPc().Svc("Back", start: 3);
        var history = new BackgroundHistory(folder);
        history.Add(new("gone", "Gone", DateTimeOffset.Now, [new(BackgroundTargetKind.Service, "Nope", true, 3)]));
        history.Add(new("item", "Back", DateTimeOffset.Now, [new(BackgroundTargetKind.Service, "Back", true, 3)]));
        var manager = new BackgroundManager(new Counter(), new Counter(), () => Task.CompletedTask, history);

        Assert.Empty(await manager.RestoreAllAsync(pc, [Item(Svc("Back"))]));
        Assert.Empty(history.All());
    }

    [Fact]
    public void TheHistorySurvivesRestartsAndABrokenFile()
    {
        new BackgroundHistory(folder).Add(new("a", "A", DateTimeOffset.Now, [new(BackgroundTargetKind.Task, @"\x\y", true)]));
        Assert.Equal("A", Assert.Single(new BackgroundHistory(folder).All()).Name);
        File.WriteAllText(Path.Combine(folder, "turned-off.json"), "{ not json");
        Assert.Empty(new BackgroundHistory(folder).All());
    }

    [Fact]
    public void TurningOffTheRestOfAnItemKeepsWhatWasSavedForTheParts_AlreadyChanged()
    {
        var history = new BackgroundHistory(folder);
        history.Add(new("item", "I", DateTimeOffset.Now, [new(BackgroundTargetKind.Service, "A", true, 2, true)]));
        history.Add(new("item", "I", DateTimeOffset.Now, [new(BackgroundTargetKind.Service, "A", false, 4), new(BackgroundTargetKind.Service, "B", true, 3)]));

        var saved = history.Get("item")!.Saved;
        Assert.True(saved.Single(x => x.Name == "A").WasOn); Assert.Equal(2, saved.Single(x => x.Name == "A").Start);
        Assert.True(saved.Single(x => x.Name == "B").WasOn);
    }
}

public sealed class BackgroundHostTests
{
    private static BackgroundSession Session(List<BackgroundRequest> seen, bool admin = true, Func<BackgroundRequest, BackgroundOutcome>? answer = null) =>
        new(pipe => BackgroundHost.RunAsync([BackgroundHost.Command, pipe], new Recorder(seen, answer ?? (_ => new(true))), () => admin, TimeSpan.FromSeconds(20)));

    private sealed class Recorder(List<BackgroundRequest> seen, Func<BackgroundRequest, BackgroundOutcome> answer) : IBackgroundChanger
    {
        public BackgroundOutcome Change(BackgroundRequest request) { seen.Add(request); return answer(request); }
    }

    [Fact]
    public async Task TheHelperPassesAChangeToTheChangerAndReturnsWhatWasSaved()
    {
        var seen = new List<BackgroundRequest>();
        using var session = Session(seen, answer: _ => new(true, null, "note", [new(BackgroundTargetKind.Service, "Diag", true, 2, true)]));
        await session.OpenAsync();

        var outcome = session.Change(new("windows-telemetry", false));

        Assert.True(outcome.Ok); Assert.Equal("note", outcome.Note);
        Assert.Equal(new BackgroundSaved(BackgroundTargetKind.Service, "Diag", true, 2, true), Assert.Single(outcome.Before!));
        Assert.Equal("windows-telemetry", Assert.Single(seen).Id);
    }

    [Fact]
    public async Task SavedStateTravelsToTheHelperWhenTurningBackOn()
    {
        var seen = new List<BackgroundRequest>();
        using var session = Session(seen);
        await session.OpenAsync();

        session.Change(new("x", true, [new(BackgroundTargetKind.Policy, "P", true, Value: 3)]));

        Assert.Equal(3, Assert.Single(Assert.Single(seen).Saved!).Value);
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
        using var session = new BackgroundSession(_ => throw new OperationCanceledException("declined"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.OpenAsync());
        Assert.False(session.IsOpen);
    }

    [Fact]
    public async Task ClosingTheSessionStopsTheHelper()
    {
        var finished = new TaskCompletionSource();
        using var session = new BackgroundSession(async pipe =>
        {
            await BackgroundHost.RunAsync([BackgroundHost.Command, pipe], new Recorder([], _ => new(true)), () => true, TimeSpan.FromSeconds(20));
            finished.SetResult();
        });
        await session.OpenAsync();

        session.Close();

        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(session.IsOpen);
    }
}

public sealed class BackgroundLiveTests
{
    // Read-only: scans the real PC with the shipped catalog. Opt in like the other live checks.
    [Fact]
    public void TheShippedCatalogCanBeReadOnThisPc()
    {
        if (Environment.GetEnvironmentVariable("TIDYWINDOW_LIVE_TESTS") != "1" || !OperatingSystem.IsWindows()) return;
        var result = BackgroundScan.Run(new WindowsBackgroundSystem(), BackgroundCatalog.Load());
        Assert.NotEmpty(result.Entries);
        Assert.Equal(BackgroundCatalog.Load().Count, result.Entries.Count + result.NotOnThisPc);
        foreach (var entry in result.Entries) Console.WriteLine($"{entry.Id,-26} on={entry.On,-5} running={entry.Running,-5} lock={entry.Lock}");
    }
}
