using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Tests;

/// <summary>A pretend Windows that records what it was asked to do, so no program on this PC is closed.</summary>
internal sealed class FakeUnstickSystem : IUnstickSystem
{
    public HashSet<string> Running_ { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Denied { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Calls { get; } = [];
    public bool WslInstalled { get; set; } = true;
    public string? ClipboardHolder { get; set; }
    public bool DnsWorks { get; set; } = true;
    public int WslExit { get; set; }
    public UserServiceOutcome Service { get; set; } = UserServiceOutcome.Restarted;
    public bool Elevated { get; set; }

    public EndResult End(IReadOnlyList<string> programs, string? commandLine = null)
    {
        int ended = 0, denied = 0;
        foreach (var name in programs)
        {
            Calls.Add("end " + name + (commandLine is null ? "" : " " + commandLine));
            if (!Running_.Contains(name)) continue;
            if (Denied.Contains(name)) { denied++; continue; }
            Running_.Remove(name); ended++;
        }
        return new(ended, denied);
    }

    public bool Running(IReadOnlyList<string> programs) => programs.Any(Running_.Contains);
    public bool Start(string file) { Calls.Add("start " + file); Running_.Add(Path.GetFileNameWithoutExtension(file)); return true; }
    public bool HasProgram(string file) => file != "wsl.exe" || WslInstalled;
    public Task RestartExplorerAsync() { Calls.Add("explorer"); return Task.CompletedTask; }
    public void RefreshDisplay() => Calls.Add("display");
    public void RefreshAssociations() => Calls.Add("associations");
    public string? CheckClipboard() { Calls.Add("clipboard"); return ClipboardHolder; }
    public bool FlushDns() { Calls.Add("dns"); return DnsWorks; }
    public UserServiceOutcome RestartUserService(string name) { Calls.Add("service " + name); return Service; }
    public UserServiceOutcome RestartServiceHost(string name) { Calls.Add("host " + name); return Service; }
    public Task<int> RunAsync(string file, string arguments, TimeSpan timeout, CancellationToken token) { Calls.Add($"run {file} {arguments}"); return Task.FromResult(WslExit); }
}

/// <summary>A pretend Fixes service that records what it was asked to run, so no service on this PC is restarted and no permission prompt appears.</summary>
internal sealed class FakeFixes
{
    public List<string> Ran { get; } = [];
    public PackageActionResult Result { get; set; } = new(ActionOutcome.Completed, "done", "");
    public Exception? Throw { get; set; }

    public Task<PackageActionResult> Run(FixRun run, IProgress<CommandOutput> progress, CancellationToken token)
    {
        Ran.Add(run.Id);
        if (Throw is not null) throw Throw;
        return Task.FromResult(Result);
    }
}

public sealed class UnstickCatalogTests
{
    [Fact]
    public void EveryActionIsDescribedAndHasAUniqueId()
    {
        Assert.Equal(UnstickCatalog.All.Count, UnstickCatalog.All.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var action in UnstickCatalog.All)
        {
            Assert.All(new[] { action.Name, action.Button, action.Glyph, action.When, action.Does, action.Notice, action.Speed }, text => Assert.False(string.IsNullOrWhiteSpace(text), action.Id));
            Assert.True(int.TryParse(action.Glyph, System.Globalization.NumberStyles.HexNumber, null, out _), action.Id);
        }
    }

    [Fact]
    public async Task EveryActionInTheCatalogIsHandledByTheRunner()
    {
        var runner = new UnstickRunner(new FakeUnstickSystem(), new FakeFixes().Run);
        foreach (var action in UnstickCatalog.All)
            Assert.True((await runner.RunAsync(action.Id)).Ok, action.Id);
    }

    [Fact]
    public void EveryAdministratorActionPointsAtARealMachineWideFix()
    {
        var fixes = FixCatalog.Load();
        var admin = UnstickCatalog.All.Where(x => x.Admin).ToList();
        Assert.Equal(5, admin.Count);
        foreach (var action in admin)
        {
            var fix = fixes.Single(x => x.Id == action.FixId);
            Assert.Equal(FixScope.Machine, fix.Scope);
            Assert.Empty(action.Targets);
        }
        // Everything that does not restart a service stays free of permission prompts.
        Assert.All(UnstickCatalog.All.Where(x => !x.Admin), x => Assert.Null(x.FixId));
    }

    [Fact]
    public async Task AnIdThatIsNotOnThePageIsRefused()
    {
        var fake = new FakeUnstickSystem();
        var result = await new UnstickRunner(fake).RunAsync("format-c-drive");
        Assert.False(result.Ok);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void ProtectedWindowsProcessesAreNeverTargets()
    {
        // Closing these takes the session down or closes every Store app, so no action may name them.
        var banned = new[] { "dwm", "sihost", "winlogon", "csrss", "lsass", "svchost", "ApplicationFrameHost", "RuntimeBroker" };
        var named = UnstickCatalog.All.SelectMany(x => x.Targets).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Empty(named.Intersect(banned, StringComparer.OrdinalIgnoreCase));
    }
}

public sealed class UnstickRunnerTests
{
    [Fact]
    public async Task ClosingAStuckProgramIsReported()
    {
        var fake = new FakeUnstickSystem { Running_ = { "ScreenClippingHost" } };
        var result = await new UnstickRunner(fake).RunAsync("unstick-snipping");
        Assert.True(result.Ok);
        Assert.Contains("unstuck", result.Message);
        Assert.Equal(["end ScreenClippingHost", "end SnippingTool"], fake.Calls);
    }

    [Fact]
    public async Task NothingRunningIsNotAFailure()
    {
        var result = await new UnstickRunner(new FakeUnstickSystem()).RunAsync("restart-widgets");
        Assert.True(result.Ok);
        Assert.Contains("not running", result.Message);
    }

    [Fact]
    public async Task AProgramWindowsWillNotLetGoIsAFailure()
    {
        var fake = new FakeUnstickSystem { Running_ = { "SearchHost" }, Denied = { "SearchHost" } };
        var result = await new UnstickRunner(fake).RunAsync("restart-search-ui");
        Assert.False(result.Ok);
        Assert.Contains("did not let", result.Message);
    }

    [Fact]
    public async Task OnlyTheThumbnailHostOfDllhostIsClosed()
    {
        var fake = new FakeUnstickSystem { Running_ = { "prevhost", "dllhost" } };
        await new UnstickRunner(fake).RunAsync("unstick-previews");
        Assert.Contains("end prevhost", fake.Calls);
        Assert.Contains("end dllhost " + UnstickRunner.ThumbnailHost, fake.Calls);
        Assert.DoesNotContain("end dllhost", fake.Calls);
    }

    [Fact]
    public async Task TypingIsRestoredByStartingTheKeyboardHelperWhenItIsMissing()
    {
        var fake = new FakeUnstickSystem { Running_ = { "TextInputHost" } };
        var result = await new UnstickRunner(fake).RunAsync("unstick-input");
        Assert.True(result.Ok);
        Assert.Contains("start ctfmon.exe", fake.Calls);
    }

    [Fact]
    public async Task TheKeyboardHelperIsLeftAloneWhenItIsRunning()
    {
        var fake = new FakeUnstickSystem { Running_ = { "TextInputHost", "ctfmon" } };
        await new UnstickRunner(fake).RunAsync("unstick-input");
        Assert.DoesNotContain("start ctfmon.exe", fake.Calls);
    }

    [Fact]
    public async Task RemoteDesktopClipboardIsRestartedOnlyWhenItIsRunning()
    {
        var without = new FakeUnstickSystem();
        await new UnstickRunner(without).RunAsync("restart-clipboard");
        Assert.DoesNotContain("start rdpclip.exe", without.Calls);
        Assert.Contains("host cbdhsvc", without.Calls);

        var with = new FakeUnstickSystem { Running_ = { "rdpclip" } };
        await new UnstickRunner(with).RunAsync("restart-clipboard");
        Assert.Contains("end rdpclip", with.Calls);
        Assert.Contains("start rdpclip.exe", with.Calls);
    }

    [Fact]
    public async Task ASecondAppHoldingTheClipboardIsNamed()
    {
        var result = await new UnstickRunner(new FakeUnstickSystem { ClipboardHolder = "Greenshot" }).RunAsync("restart-clipboard");
        Assert.True(result.Ok);
        Assert.Contains("Greenshot", result.Message);
    }

    [Fact]
    public async Task TheClipboardServiceBeingRefusedIsAFailureWhenNothingElseWorked()
    {
        var result = await new UnstickRunner(new FakeUnstickSystem { Service = UserServiceOutcome.Denied }).RunAsync("restart-clipboard");
        Assert.False(result.Ok);
    }

    [Theory]
    [InlineData(UserServiceOutcome.Restarted, true)]
    [InlineData(UserServiceOutcome.NotFound, true)]
    [InlineData(UserServiceOutcome.Denied, false)]
    public async Task NotificationsFollowTheServiceOutcome(UserServiceOutcome outcome, bool ok)
    {
        var fake = new FakeUnstickSystem { Service = outcome };
        Assert.Equal(ok, (await new UnstickRunner(fake).RunAsync("restart-notifications")).Ok);
        Assert.Equal(["service WpnUserService"], fake.Calls);
    }

    [Fact]
    public async Task MediaKeysRestartTheirOwnServiceHost()
    {
        var fake = new FakeUnstickSystem();
        await new UnstickRunner(fake).RunAsync("unstick-media");
        Assert.Equal(["host NPSMSvc"], fake.Calls);
    }

    [Fact]
    public async Task FlushingDnsReportsAFailure()
    {
        Assert.True((await new UnstickRunner(new FakeUnstickSystem()).RunAsync("flush-dns")).Ok);
        Assert.False((await new UnstickRunner(new FakeUnstickSystem { DnsWorks = false }).RunAsync("flush-dns")).Ok);
    }

    [Fact]
    public async Task WslIsShutDownAndItsExitCodeIsChecked()
    {
        var fake = new FakeUnstickSystem();
        Assert.True((await new UnstickRunner(fake).RunAsync("restart-wsl")).Ok);
        Assert.Equal(["run wsl.exe --shutdown"], fake.Calls);
        Assert.False((await new UnstickRunner(new FakeUnstickSystem { WslExit = 1 }).RunAsync("restart-wsl")).Ok);
        Assert.False((await new UnstickRunner(new FakeUnstickSystem { WslExit = -1 }).RunAsync("restart-wsl")).Ok);
    }

    [Fact]
    public async Task WslIsOnlyOfferedWhenInstalled()
    {
        var wsl = UnstickCatalog.Find("restart-wsl")!;
        Assert.True(new UnstickRunner(new FakeUnstickSystem()).Available(wsl));
        var missing = new FakeUnstickSystem { WslInstalled = false };
        Assert.False(new UnstickRunner(missing).Available(wsl));
        Assert.False((await new UnstickRunner(missing).RunAsync("restart-wsl")).Ok);
    }

    [Fact]
    public void AnActionIsActiveOnlyWhenItsProgramsAreRunning()
    {
        var snip = UnstickCatalog.Find("unstick-snipping")!;
        var fake = new FakeUnstickSystem();
        var runner = new UnstickRunner(fake);
        Assert.False(runner.IsActive(snip));
        fake.Running_.Add("SnippingTool");
        Assert.True(runner.IsActive(snip));
        Assert.False(runner.IsActive(UnstickCatalog.Find("flush-dns")!));
    }
}

public sealed class UnstickAdminTests
{
    [Fact]
    public async Task AnAdministratorActionRunsItsFixAndNothingElse()
    {
        var system = new FakeUnstickSystem();
        var fixes = new FakeFixes();
        var result = await new UnstickRunner(system, fixes.Run).RunAsync("restart-audio");
        Assert.True(result.Ok);
        Assert.Equal(["restart-audio"], fixes.Ran);
        Assert.Empty(system.Calls);
    }

    [Theory]
    [InlineData("restart-audio", "restart-audio")]
    [InlineData("restart-bluetooth", "restart-bluetooth")]
    [InlineData("restart-camera", "restart-camera")]
    [InlineData("clear-print-queue", "fix-printing")]
    [InlineData("rescan-hardware", "rescan-devices")]
    public async Task EachActionRunsTheFixItNames(string action, string fix)
    {
        var fixes = new FakeFixes();
        await new UnstickRunner(new FakeUnstickSystem(), fixes.Run).RunAsync(action);
        Assert.Equal([fix], fixes.Ran);
    }

    [Fact]
    public async Task AFixThatFailsReportsItsFirstDetailLine()
    {
        var fixes = new FakeFixes { Result = new(ActionOutcome.Failed, "Couldn’t finish Restart sound services", "Stopped at “Stop Windows Audio”.\nFAILED   Stop Windows Audio") };
        var result = await new UnstickRunner(new FakeUnstickSystem(), fixes.Run).RunAsync("restart-audio");
        Assert.False(result.Ok);
        Assert.Equal("Stopped at “Stop Windows Audio”.", result.Message);
    }

    [Fact]
    public async Task DecliningThePermissionPromptChangesNothingAndSaysSo()
    {
        var declined = new FakeFixes { Result = new(ActionOutcome.Failed, "Restart sound services was not run", "Administrator permission was declined. Nothing was changed.") };
        var result = await new UnstickRunner(new FakeUnstickSystem(), declined.Run).RunAsync("restart-audio");
        Assert.False(result.Ok);
        Assert.Contains("declined", result.Message);

        var cancelled = new FakeFixes { Throw = new OperationCanceledException() };
        var second = await new UnstickRunner(new FakeUnstickSystem(), cancelled.Run).RunAsync("restart-audio");
        Assert.False(second.Ok);
        Assert.Contains("declined", second.Message);
    }

    [Fact]
    public async Task AWarningIsStillASuccessButSaysWhereToLook()
    {
        var fixes = new FakeFixes { Result = new(ActionOutcome.Unverified, "finished with warnings", "") };
        var result = await new UnstickRunner(new FakeUnstickSystem(), fixes.Run).RunAsync("restart-camera");
        Assert.True(result.Ok);
        Assert.Contains("warning", result.Message);
    }

    [Fact]
    public async Task WithoutTheHelperTheAdministratorActionsAreHiddenAndRefused()
    {
        var runner = new UnstickRunner(new FakeUnstickSystem());
        Assert.All(UnstickCatalog.All.Where(x => x.Admin), x => Assert.False(runner.Available(x)));
        Assert.All(UnstickCatalog.All.Where(x => !x.Admin && x.Id != "restart-wsl"), x => Assert.True(runner.Available(x)));
        Assert.False((await runner.RunAsync("restart-audio")).Ok);
    }
}
