using System.Diagnostics;
using System.Runtime.Versioning;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Tests;

/// <summary>A pretend powercfg that keeps a list of plans, so nothing on this PC is switched.</summary>
internal sealed class FakePowerCfg : IPowerCfg
{
    public List<(string Id, string Name)> Plans { get; } =
    [
        (PowerGuard.Balanced, "Balanced"),
        ("a1841308-3541-4fab-bc81-f71556f20b4a", "Power saver")
    ];
    public string ActiveId { get; set; } = PowerGuard.Balanced;
    public List<string> Calls { get; } = [];
    public bool IgnoreSetActive { get; set; }
    public bool UltimateMissing { get; set; }

    public string Run(IReadOnlyList<string> args)
    {
        if (!PowerGuard.Allows(args)) throw new InvalidOperationException("not allowed: " + string.Join(' ', args));
        Calls.Add(string.Join(' ', args));
        switch (args[0])
        {
            case "/list":
                return "Existing Power Schemes (* Active)\n-----------------------------------\n" + string.Concat(Plans.Select(p => $"Power Scheme GUID: {p.Id}  ({p.Name}){(p.Id == ActiveId ? " *" : "")}\n"));
            case "/getactivescheme":
                var active = Plans.First(p => p.Id == ActiveId);
                return $"Power Scheme GUID: {active.Id}  ({active.Name})";
            case "/setactive":
                if (Plans.All(p => p.Id != args[1])) throw new InvalidOperationException("The power scheme does not exist.");
                if (!IgnoreSetActive) ActiveId = args[1];
                return "";
            case "/duplicatescheme":
                if (args[1] == PowerGuard.UltimateTemplate && UltimateMissing) throw new InvalidOperationException("The power scheme cannot be duplicated.");
                var made = (Guid.NewGuid().ToString(), args[1] == PowerGuard.UltimateTemplate ? "Ultimate Performance" : "High performance");
                Plans.Add(made);
                return $"Power Scheme GUID: {made.Item1}  ({made.Item2})";
            case "/changename":
                var index = Plans.FindIndex(p => p.Id == args[1]);
                Plans[index] = (Plans[index].Id, args[2]);
                return "";
            default: throw new InvalidOperationException();
        }
    }
}

public sealed class PowerGuardTests
{
    [Theory]
    [InlineData("/list")]
    [InlineData("/getactivescheme")]
    public void ReadingCommandsAreAllowed(string command) => Assert.True(PowerGuard.Allows([command]));

    [Fact]
    public void SwitchingAndCopyingAreAllowedOnlyForRealIdsAndOurOwnTemplates()
    {
        Assert.True(PowerGuard.Allows(["/setactive", PowerGuard.Balanced]));
        Assert.True(PowerGuard.Allows(["/duplicatescheme", PowerGuard.UltimateTemplate]));
        Assert.True(PowerGuard.Allows(["/duplicatescheme", PowerGuard.HighPerformance]));
        Assert.True(PowerGuard.Allows(["/changename", PowerGuard.Balanced, PowerGuard.CopyName]));
        Assert.False(PowerGuard.Allows(["/setactive", "balanced"]));
        Assert.False(PowerGuard.Allows(["/setactive", PowerGuard.Balanced + " & calc"]));
        Assert.False(PowerGuard.Allows(["/duplicatescheme", PowerGuard.Balanced]));
        Assert.False(PowerGuard.Allows(["/changename", PowerGuard.Balanced, "anything else"]));
    }

    [Theory]
    [InlineData("/delete")]
    [InlineData("/import")]
    [InlineData("/hibernate")]
    [InlineData("/setacvalueindex")]
    [InlineData("/export")]
    public void EverythingElseIsRefused(string command) => Assert.False(PowerGuard.Allows([command, PowerGuard.Balanced]));

    [Fact]
    public void NothingIsAllowedWithoutArgumentsOrWithTooMany()
    {
        Assert.False(PowerGuard.Allows([]));
        Assert.False(PowerGuard.Allows(["/list", "extra"]));
        Assert.False(PowerGuard.Allows(["/setactive"]));
    }
}

public sealed class PowerModeTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "tidywindow-lab-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(folder, true); } catch (IOException) { } }

    [Fact]
    public void PlansAreReadFromRealPowercfgOutputInAnyLanguage()
    {
        var plans = PowerMode.Parse("Existing Power Schemes (* Active)\r\n-----------------------------------\r\nPower Scheme GUID: 0c54ec3e-a3d7-4df1-826a-f08af5c6c45b  (Ultimate Performance)\r\nPower Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced) *\r\n");
        Assert.Equal(2, plans.Count);
        Assert.False(plans[0].Active); Assert.True(plans[1].Active);
        Assert.Equal("Ultimate Performance", plans[0].Name);

        var german = PowerMode.Parse("GUID des Energieschemas: 381b4222-f694-41f0-9685-ff5bb260df2e  (Ausbalanciert) *");
        Assert.Equal("Ausbalanciert", Assert.Single(german).Name); Assert.True(german[0].Active);
    }

    [Fact]
    public void AnExistingUltimatePlanIsUsedAndNothingIsCreated()
    {
        var pc = new FakePowerCfg(); pc.Plans.Add(("0c54ec3e-a3d7-4df1-826a-f08af5c6c45b", "Ultimate Performance"));
        var mode = new PowerMode(pc, new(folder));

        var result = mode.Switch();

        Assert.True(result.Ok);
        Assert.Equal("0c54ec3e-a3d7-4df1-826a-f08af5c6c45b", pc.ActiveId);
        Assert.DoesNotContain(pc.Calls, x => x.StartsWith("/duplicatescheme"));
        Assert.Equal("Balanced", new LabStore(folder).GetPower()!.PreviousName);
    }

    [Fact]
    public void WithoutOneAnUltimateCopyIsMadeAndNamedAsOurs()
    {
        var pc = new FakePowerCfg();
        var result = new PowerMode(pc, new(folder)).Switch();

        Assert.True(result.Ok);
        Assert.Equal(PowerGuard.CopyName, pc.Plans.Single(p => p.Id == pc.ActiveId).Name);
        Assert.Contains(pc.Calls, x => x == "/duplicatescheme " + PowerGuard.UltimateTemplate);
    }

    [Fact]
    public void ACopyMadeEarlierIsFoundAgainInsteadOfMakingAnother()
    {
        var pc = new FakePowerCfg(); var mode = new PowerMode(pc, new(folder));
        mode.Switch(); mode.Restore();
        pc.Calls.Clear();

        mode.Switch();

        Assert.DoesNotContain(pc.Calls, x => x.StartsWith("/duplicatescheme"));
        Assert.Equal(1, pc.Plans.Count(p => p.Name == PowerGuard.CopyName));
    }

    [Fact]
    public void WhenUltimateIsNotOfferedHighPerformanceIsCopiedInstead()
    {
        var pc = new FakePowerCfg { UltimateMissing = true };
        var result = new PowerMode(pc, new(folder)).Switch();

        Assert.True(result.Ok);
        Assert.Equal("High performance", pc.Plans.Single(p => p.Id == pc.ActiveId).Name);
        Assert.DoesNotContain(pc.Calls, x => x.StartsWith("/changename"));
    }

    [Fact]
    public void SwitchingWhenAlreadyFastDoesNothingAndRemembersNothing()
    {
        var pc = new FakePowerCfg(); pc.Plans.Add((PowerGuard.HighPerformance, "High performance")); pc.ActiveId = PowerGuard.HighPerformance;
        var result = new PowerMode(pc, new(folder)).Switch();

        Assert.True(result.Ok); Assert.Contains("already active", result.Message);
        Assert.Null(new LabStore(folder).GetPower());
        Assert.DoesNotContain(pc.Calls, x => x.StartsWith("/setactive"));
    }

    [Fact]
    public void GoingBackReturnsToTheRememberedPlanAndForgetsIt()
    {
        var pc = new FakePowerCfg(); pc.Plans.Add(("a0000000-0000-0000-0000-000000000001", "My own plan")); pc.ActiveId = "a0000000-0000-0000-0000-000000000001";
        var mode = new PowerMode(pc, new(folder));
        mode.Switch();
        Assert.NotEqual("a0000000-0000-0000-0000-000000000001", pc.ActiveId);

        var result = mode.Restore();

        Assert.True(result.Ok); Assert.Equal("a0000000-0000-0000-0000-000000000001", pc.ActiveId);
        Assert.Null(new LabStore(folder).GetPower());
    }

    [Fact]
    public void GoingBackWorksFromANewInstanceBecauseTheNoteIsOnDisk()
    {
        var pc = new FakePowerCfg();
        new PowerMode(pc, new(folder)).Switch();

        var result = new PowerMode(pc, new(folder)).Restore();

        Assert.True(result.Ok); Assert.Equal(PowerGuard.Balanced, pc.ActiveId);
    }

    [Fact]
    public void IfTheOldPlanIsGoneTheNormalOneIsUsed()
    {
        var pc = new FakePowerCfg(); var own = "a0000000-0000-0000-0000-000000000001"; pc.Plans.Add((own, "Gone soon")); pc.ActiveId = own;
        var mode = new PowerMode(pc, new(folder));
        mode.Switch();
        pc.Plans.RemoveAll(p => p.Id == own);

        Assert.True(mode.Restore().Ok);
        Assert.Equal(PowerGuard.Balanced, pc.ActiveId);
    }

    [Fact]
    public void WhenWindowsDoesNotSwitchItSaysSoAndForgetsTheNote()
    {
        var pc = new FakePowerCfg { IgnoreSetActive = true };
        var mode = new PowerMode(pc, new(folder));

        Assert.Throws<InvalidOperationException>(() => mode.Switch());
        Assert.Null(new LabStore(folder).GetPower());
        Assert.Equal(PowerGuard.Balanced, pc.ActiveId);
    }

    [Fact]
    public void ReadingTellsWhichPlanIsActiveAndWhichWouldBeUsed()
    {
        var pc = new FakePowerCfg(); pc.Plans.Add((PowerGuard.HighPerformance, "High performance"));
        var state = new PowerMode(pc, new(folder)).Read();

        Assert.Equal("Balanced", state.Active.Name); Assert.False(state.ActiveIsFast);
        Assert.Equal("High performance", state.Fast!.Name);
        Assert.Single(state.Plans, p => p.Active);
    }
}

public sealed class ProbeTests
{
    [Fact]
    public void MedianAndSpreadAreWhatTheySay()
    {
        Assert.Equal(3, ProbeMath.Median([5, 1, 3]));
        Assert.Equal(2.5, ProbeMath.Median([1, 2, 3, 4]));
        Assert.Equal(0, ProbeMath.Median([]));
        Assert.Equal(20, ProbeMath.Spread([9, 10, 11]), 6);
        Assert.Equal(0, ProbeMath.Spread([7]));
    }

    private static ProbeRun Run(double wake, double wakeSpread = 1, double copy = 40, double random = 80) =>
        new(DateTimeOffset.Now, "p", "on", [new("wake", wake, wakeSpread), new("throughput", 1000, 1), new("copy", copy, 1), new("random", random, 1)]);

    [Fact]
    public void AClearImprovementIsReportedInTheRightDirectionForEachTest()
    {
        var changes = ProbeCompare.Between(Run(wake: 2.0, copy: 40, random: 80), Run(wake: 1.5, copy: 48, random: 64));

        var wake = changes.Single(x => x.Kind.Id == "wake"); var copy = changes.Single(x => x.Kind.Id == "copy"); var random = changes.Single(x => x.Kind.Id == "random");
        Assert.Equal(ProbeVerdict.Better, wake.Verdict); Assert.Equal(25, wake.Percent, 6);
        Assert.Equal(ProbeVerdict.Better, copy.Verdict); Assert.Equal(20, copy.Percent, 6);
        Assert.Equal(ProbeVerdict.Better, random.Verdict); Assert.Equal(20, random.Percent, 6);
    }

    [Fact]
    public void ASlowerResultIsWorseWhetherLowerOrHigherIsBetter()
    {
        var changes = ProbeCompare.Between(Run(wake: 2.0, copy: 40), Run(wake: 2.6, copy: 30));
        Assert.Equal(ProbeVerdict.Worse, changes.Single(x => x.Kind.Id == "wake").Verdict);
        Assert.Equal(ProbeVerdict.Worse, changes.Single(x => x.Kind.Id == "copy").Verdict);
        Assert.True(changes.Single(x => x.Kind.Id == "wake").Percent < 0);
    }

    [Fact]
    public void ASmallChangeIsNoDifference()
    {
        var changes = ProbeCompare.Between(Run(wake: 2.00), Run(wake: 2.04));
        Assert.All(changes, x => Assert.Equal(ProbeVerdict.NoDifference, x.Verdict));
    }

    [Fact]
    public void AChangeInsideTheNoiseOfTheRoundsIsNoDifferenceEvenWhenBig()
    {
        // The rounds of this check differed by 12%, so an 8% move cannot be told apart from noise.
        var changes = ProbeCompare.Between(Run(wake: 2.0, wakeSpread: 12), Run(wake: 1.84, wakeSpread: 1));
        Assert.Equal(ProbeVerdict.NoDifference, changes.Single(x => x.Kind.Id == "wake").Verdict);
    }

    [Fact]
    public void TheWobbleSeenBetweenTwoRealChecksOfTheSamePcIsNoDifference()
    {
        // Measured on a real PC: two checks with the same settings differed by 3.4% in memory access.
        var changes = ProbeCompare.Between(Run(wake: 2.42, random: 98.8), Run(wake: 2.42, random: 102.2));
        Assert.All(changes, x => Assert.Equal(ProbeVerdict.NoDifference, x.Verdict));
    }

    [Fact]
    public void ATestMissingFromEitherCheckIsLeftOut()
    {
        var thin = new ProbeRun(DateTimeOffset.Now, "p", "on", [new("wake", 2, 1)]);
        Assert.Single(ProbeCompare.Between(thin, Run(wake: 1)));
    }

    [Fact]
    public async Task AQuickCheckMeasuresEveryTestAndReportsProgress()
    {
        var lines = new List<string>();
        var metrics = await new ProbeRunner(ProbeProfile.Quick).RunAsync(new Inline<string>(lines.Add), CancellationToken.None);

        Assert.Equal(ProbeKinds.All.Select(x => x.Id), metrics.Select(x => x.Id));
        Assert.All(metrics, x => { Assert.True(x.Value > 0, x.Id); Assert.True(x.Spread >= 0); });
        Assert.Contains(lines, x => x.StartsWith("Round 1 of 2")); Assert.Contains(lines, x => x.Contains("Memory copy:"));
    }

    [Fact]
    public async Task ACheckCanBeCancelled()
    {
        using var source = new CancellationTokenSource();
        var runner = new ProbeRunner(ProbeProfile.Full);
        var task = runner.RunAsync(new Inline<string>(line => { if (line.StartsWith("Round 1")) source.Cancel(); }), source.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    private sealed class Inline<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
}

public sealed class ProcessBoostTests
{
    private sealed class FakeProcesses : IProcessSource
    {
        public List<AppProcess> Apps { get; } = [];
        public Dictionary<int, ProcessPriorityClass> Now { get; } = [];
        public bool Deny { get; set; }

        public IReadOnlyList<AppProcess> Windowed() => Apps.Select(a => a with { Priority = Now.GetValueOrDefault(a.Id) }).ToList();
        public ProcessPriorityClass? Priority(int id, long startTicks) => Apps.Any(a => a.Id == id && a.StartTicks == startTicks) && Now.TryGetValue(id, out var p) ? p : null;
        public void SetPriority(int id, long startTicks, ProcessPriorityClass priority)
        {
            if (Deny) throw new System.ComponentModel.Win32Exception(5, "Access is denied");
            if (!Apps.Any(a => a.Id == id && a.StartTicks == startTicks)) throw new InvalidOperationException("That app has closed.");
            Now[id] = priority;
        }
    }

    private static (FakeProcesses Pc, ProcessBoost Boost, AppProcess Game) Setup()
    {
        var pc = new FakeProcesses(); var game = new AppProcess(10, "game", "A game", 111, ProcessPriorityClass.Normal, 500);
        pc.Apps.Add(game); pc.Now[10] = ProcessPriorityClass.Normal;
        return (pc, new ProcessBoost(pc), game);
    }

    [Fact]
    public void BoostingChangesThePriorityAndPuttingBackRestoresWhatItWas()
    {
        var (pc, boost, game) = Setup(); pc.Now[10] = ProcessPriorityClass.AboveNormal;

        Assert.True(boost.Set(game, BoostLevel.Boost).Ok);
        Assert.Equal(ProcessPriorityClass.High, pc.Now[10]); Assert.True(boost.IsChanged(game));

        Assert.True(boost.Reset(game).Ok);
        Assert.Equal(ProcessPriorityClass.AboveNormal, pc.Now[10]); Assert.False(boost.IsChanged(game));
    }

    [Fact]
    public void ChangingTwiceKeepsTheFirstOriginal()
    {
        var (pc, boost, game) = Setup();
        boost.Set(game, BoostLevel.Boost); boost.Set(game, BoostLevel.Ease);
        Assert.Equal(ProcessPriorityClass.BelowNormal, pc.Now[10]);

        boost.Reset(game);
        Assert.Equal(ProcessPriorityClass.Normal, pc.Now[10]);
    }

    [Fact]
    public void ChoosingNormalWhenItWasNormalLeavesNothingToUndo()
    {
        var (_, boost, game) = Setup();
        boost.Set(game, BoostLevel.Boost);
        Assert.True(boost.Set(game, BoostLevel.Normal).Ok);
        Assert.False(boost.IsChanged(game));
    }

    [Fact]
    public void RealTimeIsNeverOffered() =>
        Assert.DoesNotContain(Enum.GetValues<BoostLevel>(), x => BoostGuard.Class(x) == ProcessPriorityClass.RealTime);

    [Theory]
    [InlineData("csrss")]
    [InlineData("LSASS")]
    [InlineData("explorer")]
    [InlineData("MsMpEng")]
    [InlineData("TextInputHost")]
    [InlineData("ApplicationFrameHost")]
    public void PartsOfWindowsAndSecuritySoftwareAreRefused(string name)
    {
        var (pc, boost, _) = Setup();
        var system = new AppProcess(20, name, "x", 5, ProcessPriorityClass.Normal, 1); pc.Apps.Add(system); pc.Now[20] = ProcessPriorityClass.Normal;

        Assert.False(boost.Set(system, BoostLevel.Boost).Ok);
        Assert.Equal(ProcessPriorityClass.Normal, pc.Now[20]);
        Assert.DoesNotContain(boost.List(), x => x.Id == 20);
    }

    [Fact]
    public void AnAppThatClosedOrWasReplacedByAnotherWithTheSameNumberIsNotTouched()
    {
        var (pc, boost, game) = Setup();
        var impostor = game with { StartTicks = 999 };

        var result = boost.Set(impostor, BoostLevel.Boost);

        Assert.False(result.Ok); Assert.Equal(ProcessPriorityClass.Normal, pc.Now[10]);
    }

    [Fact]
    public void WhenWindowsRefusesTheReasonIsShownAndNothingIsRemembered()
    {
        var (pc, boost, game) = Setup(); pc.Deny = true;

        var result = boost.Set(game, BoostLevel.Boost);

        Assert.False(result.Ok); Assert.Contains("Access is denied", result.Message); Assert.False(boost.IsChanged(game));
    }

    [Fact]
    public void ResetAllOnlyTouchesWhatWasChanged()
    {
        var (pc, boost, game) = Setup();
        var other = new AppProcess(11, "other", "Other", 222, ProcessPriorityClass.Normal, 10); pc.Apps.Add(other); pc.Now[11] = ProcessPriorityClass.Idle;
        boost.Set(game, BoostLevel.Boost);

        Assert.Equal(1, boost.ResetAll(boost.List()));
        Assert.Equal(ProcessPriorityClass.Normal, pc.Now[10]); Assert.Equal(ProcessPriorityClass.Idle, pc.Now[11]);
    }

    [Fact]
    public void TheListIsSortedByNameWithoutProtectedApps()
    {
        var pc = new FakeProcesses();
        foreach (var (id, name) in new[] { (1, "zed"), (2, "Alpha"), (3, "dwm"), (4, "mid") }) { pc.Apps.Add(new(id, name, "t" + id, id, ProcessPriorityClass.Normal, 1)); pc.Now[id] = ProcessPriorityClass.Normal; }

        Assert.Equal(["Alpha", "mid", "zed"], new ProcessBoost(pc).List().Select(x => x.Name));
    }
}

public sealed class LabStoreTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "tidywindow-labstore-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(folder, true); } catch (IOException) { } }

    private static ProbeRun Run(DateTimeOffset when) => new(when, "Balanced", "on", [new("wake", 2, 1)]);

    [Fact]
    public void ChecksComeBackNewestFirstAndOnlyTheNewestAreKept()
    {
        var store = new LabStore(folder); var start = DateTimeOffset.Now.AddDays(-40);
        for (var i = 0; i < 35; i++) store.AddRun(Run(start.AddDays(i)));

        var runs = store.Runs();
        Assert.Equal(30, runs.Count);
        Assert.Equal(start.AddDays(34), runs[0].When); Assert.Equal(start.AddDays(5), runs[^1].When);
    }

    [Fact]
    public void NothingSavedMeansEmptyNotAnError()
    {
        var store = new LabStore(folder);
        Assert.Empty(store.Runs()); Assert.Null(store.GetPower());
    }

    [Fact]
    public void ADamagedFileIsTreatedAsEmpty()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "checks.json"), "{ not json");
        File.WriteAllText(Path.Combine(folder, "power.json"), "");
        var store = new LabStore(folder);

        Assert.Empty(store.Runs()); Assert.Null(store.GetPower());
        store.AddRun(Run(DateTimeOffset.Now));
        Assert.Single(store.Runs());
    }

    [Fact]
    public void ThePowerNoteSurvivesANewStoreAndCanBeCleared()
    {
        var note = new PowerNote("a", "Balanced", "b", "Ultimate", DateTimeOffset.Now);
        new LabStore(folder).SetPower(note);
        Assert.Equal("Balanced", new LabStore(folder).GetPower()!.PreviousName);

        new LabStore(folder).ClearPower();
        Assert.Null(new LabStore(folder).GetPower());
    }

    [Fact]
    public void ClearingChecksLeavesThePowerNote()
    {
        var store = new LabStore(folder);
        store.SetPower(new("a", "Balanced", "b", "Ultimate", DateTimeOffset.Now)); store.AddRun(Run(DateTimeOffset.Now));
        store.ClearRuns();
        Assert.Empty(store.Runs()); Assert.NotNull(store.GetPower());
    }
}

/// <summary>Reads this PC without changing anything: real powercfg listing and the real list of running apps.</summary>
[SupportedOSPlatform("windows")]
public sealed class LabLiveTests
{
    [Fact]
    public void TheRealPowercfgListsPlansAndNamesTheActiveOne()
    {
        var state = new PowerMode(new PowerCfgProcess(), new(Path.Combine(Path.GetTempPath(), "tidywindow-live-" + Guid.NewGuid().ToString("N")))).Read();

        Assert.NotEmpty(state.Plans); Assert.True(PowerGuard.IsId(state.Active.Id)); Assert.Contains(state.Plans, p => p.Id == state.Active.Id);
    }

    [Fact]
    public void TheRealPowercfgRefusesACommandOutsideTheList() =>
        Assert.Throws<InvalidOperationException>(() => new PowerCfgProcess().Run(["/delete", PowerGuard.Balanced]));

    [Fact]
    public void ListingRunningAppsNeverThrowsAndNeverIncludesProtectedOnes()
    {
        var apps = new ProcessBoost(new LiveProcesses()).List();
        Assert.DoesNotContain(apps, x => BoostGuard.IsProtected(x.Name));
        Assert.DoesNotContain(apps, x => x.Id == Environment.ProcessId);
    }

    [Fact]
    public void ARealProcessCanBeEasedAndPutBack()
    {
        // A throw-away program of our own, so no one else's app is touched.
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe"), "-n 6 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        try
        {
            var live = new LiveProcesses(); var boost = new ProcessBoost(live);
            var app = new AppProcess(process.Id, "ping", "ping", process.StartTime.Ticks, process.PriorityClass, 1);

            Assert.True(boost.Set(app, BoostLevel.Ease).Ok);
            Assert.Equal(ProcessPriorityClass.BelowNormal, live.Priority(app.Id, app.StartTicks));
            Assert.True(boost.Reset(app).Ok);
            Assert.Equal(ProcessPriorityClass.Normal, live.Priority(app.Id, app.StartTicks));
        }
        finally { if (!process.HasExited) process.Kill(); }
    }
}
