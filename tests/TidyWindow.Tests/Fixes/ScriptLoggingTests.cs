using TidyWindow.Next.Core;
using TidyWindow.Tests;
using Xunit;

namespace TidyWindow.Next.Tests;

/// <summary>What Logs is told when a fix or an Unstick action runs: the exact command, how it ended, and what never ran.</summary>
public sealed class ScriptLoggingTests
{
    private static FixStep Step(string title, bool always = false) =>
        new() { Title = title, File = "ipconfig.exe", Args = ["/flushdns"], Always = always };

    private static FixItem Item(params FixStep[] steps) =>
        new() { Id = "item", Name = "The fix", Group = FixGroup.Windows, Scope = FixScope.Machine, Summary = "s", Effect = "e", Steps = steps };

    [Fact]
    public async Task EachStepNamesTheCommandThatRanItsExitCodeAndWhatItSaid()
    {
        var executor = new ScriptedExecutor { Prints = { ["one"] = "Windows IP Configuration\nSuccessfully flushed the DNS Resolver Cache.\n" } };
        var progress = new Collector();
        var result = await new FixRunner(executor).RunAsync(Item(Step("one")), [], progress, CancellationToken.None);

        Assert.Contains("ran: ipconfig.exe /flushdns", result.Detail);
        Assert.Contains("exit code 0", result.Detail);
        Assert.Contains("“Successfully flushed the DNS Resolver Cache.”", result.Detail);
        // The live output says the same before the step starts, so the person sees it as it happens.
        Assert.Contains("Running: ipconfig.exe /flushdns", progress.Text);
    }

    [Fact]
    public async Task StepsThatNeverRanAreListedWithTheReason()
    {
        var executor = new ScriptedExecutor { Codes = { ["stop"] = 5 } };
        var result = await new FixRunner(executor).RunAsync(Item(Step("stop"), Step("skipped"), Step("start", always: true)), [], new Collector(), CancellationToken.None);

        Assert.Contains("FAILED   stop", result.Detail);
        Assert.Contains("NOT RUN  skipped (an earlier step failed)", result.Detail);
        Assert.Contains("would have run: ipconfig.exe /flushdns", result.Detail);
        Assert.Contains("OK       start", result.Detail);
    }

    [Fact]
    public async Task AFixSaysInLogsWhatItIsAboutToRunBeforeItRuns()
    {
        var log = new ActivityLog();
        var service = new FixService([Item(Step("flush"), Step("again"))], new ScriptedExecutor(), null, () => true, null, log: log);

        await service.RunAsync(new("item", []), new Collector(), CancellationToken.None);

        var entry = Assert.Single(log.Snapshot());
        Assert.Equal("Started: The fix", entry.Message);
        Assert.Equal(ActivityLevel.Info, entry.Level);
        Assert.Contains("administrator rights", entry.Detail);
        Assert.Contains("2 steps", entry.Detail);
        Assert.Contains("1. flush", entry.Detail); Assert.Contains("ipconfig.exe /flushdns", entry.Detail);
    }

    [Fact]
    public async Task AnUnstickActionListsEachThingItDidToWindows()
    {
        var fake = new FakeUnstickSystem(); fake.Running_.Add("Widgets");
        var result = await new UnstickRunner(fake).RunAsync("restart-widgets");

        Assert.True(result.Ok);
        Assert.Contains("1. Closing Widgets.exe, WidgetService.exe for your account", result.Detail);
        Assert.Contains("1 process closed", result.Detail);
    }

    [Fact]
    public async Task AnUnstickActionThatFoundNothingRunningSaysSo()
    {
        var result = await new UnstickRunner(new FakeUnstickSystem()).RunAsync("unstick-snipping");

        Assert.True(result.Ok);
        Assert.Contains("None of it was running, so nothing was closed.", result.Detail);
    }

    [Fact]
    public async Task ARefusalByWindowsIsInTheRecord()
    {
        var fake = new FakeUnstickSystem(); fake.Running_.Add("Widgets"); fake.Denied.Add("Widgets");
        var result = await new UnstickRunner(fake).RunAsync("restart-widgets");

        Assert.False(result.Ok);
        Assert.Contains("1 refused by Windows", result.Detail);
    }

    [Fact]
    public async Task AWslRestartRecordsTheCommandAndItsExitCode()
    {
        var fake = new FakeUnstickSystem { WslExit = 1 };
        var result = await new UnstickRunner(fake).RunAsync("restart-wsl");

        Assert.False(result.Ok);
        Assert.Contains("Running wsl.exe --shutdown", result.Detail);
        Assert.Contains("finished with exit code 1", result.Detail);
    }

    [Fact]
    public async Task AFixBackedActionCarriesTheFixsOwnStepSummary()
    {
        var fixes = new FakeFixes { Result = new(ActionOutcome.Completed, "Restart audio finished", "OK       Restart the audio service\n         ran: net.exe start audiosrv\n         exit code 0, took 1 s") };
        var result = await new UnstickRunner(new FakeUnstickSystem(), fixes.Run).RunAsync("restart-audio");

        Assert.True(result.Ok);
        Assert.Contains("Runs the “restart-audio” fix", result.Detail);
        Assert.Contains("ran: net.exe start audiosrv", result.Detail);
    }

    [Fact]
    public async Task ADeclinedPermissionIsInTheRecord()
    {
        var fixes = new FakeFixes { Throw = new OperationCanceledException() };
        var result = await new UnstickRunner(new FakeUnstickSystem(), fixes.Run).RunAsync("restart-audio");

        Assert.False(result.Ok);
        Assert.Contains("Administrator permission was declined.", result.Detail);
    }
}
