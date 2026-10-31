using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

internal sealed class Collector : IProgress<CommandOutput>
{
    public readonly List<CommandOutput> Items = [];
    public void Report(CommandOutput value) { lock (Items) Items.Add(value); }
    public string Text { get { lock (Items) return string.Concat(Items.Select(x => x.Text)); } }
    public string[] Lines(CommandOutputKind kind) { lock (Items) return Items.Where(x => x.Kind == kind).Select(x => x.Text.TrimEnd()).ToArray(); }
}

/// <summary>Runs steps without starting anything: each step title maps to an exit code, and may print lines first or throw.</summary>
internal sealed class ScriptedExecutor : IFixStepExecutor
{
    public readonly List<string> Ran = [];
    public readonly Dictionary<string, int> Codes = [];
    public readonly Dictionary<string, Exception> Throws = [];
    public readonly Dictionary<string, string> Prints = [];

    public Task<int> ExecuteAsync(FixStepContext context, IProgress<CommandOutput> progress, CancellationToken cancellationToken)
    {
        Ran.Add(context.Step.Title);
        if (Prints.TryGetValue(context.Step.Title, out var text)) progress.Report(new(text));
        if (Throws.TryGetValue(context.Step.Title, out var error)) throw error;
        return Task.FromResult(Codes.GetValueOrDefault(context.Step.Title, 0));
    }
}

public sealed class FixCatalogTests
{
    private static FixStep Run(string file, params string[] args) => new() { Title = "step", File = file, Args = args };
    private static FixItem Item(FixScope scope, params FixStep[] steps) => new() { Id = "item", Name = "Item", Group = FixGroup.Windows, Scope = scope, Summary = "s", Effect = "e", Steps = steps };
    private static List<string> Check(FixScope scope, params FixStep[] steps) => FixGuard.Validate([Item(scope, steps)]);

    [Fact]
    public void TheShippedCatalogIsValidAndReadyToUse()
    {
        var catalog = FixCatalog.Load();
        Assert.Equal(16, catalog.Count);
        Assert.Empty(FixGuard.Validate(catalog));
        foreach (var group in Enum.GetValues<FixGroup>()) Assert.Contains(catalog, x => x.Group == group);
        Assert.All(catalog, x => { Assert.NotEmpty(x.Summary); Assert.NotEmpty(x.Effect); Assert.NotEmpty(x.Sources); Assert.NotEmpty(x.Duration); });
    }

    [Fact]
    public void EveryServiceAFixStopsIsStartedAgainEvenIfAnEarlierStepFails()
    {
        foreach (var item in FixCatalog.Load())
        {
            var stops = item.Steps.Where(x => x.File == "net.exe" && x.Args[0] == "stop").Select(x => x.Args[1]).ToList();
            foreach (var service in stops)
            {
                var start = item.Steps.SingleOrDefault(x => x.File == "net.exe" && x.Args[0] == "start" && x.Args[1].Equals(service, StringComparison.OrdinalIgnoreCase));
                Assert.True(start is { Always: true }, $"{item.Id} stops {service} but does not always start it again");
            }
        }
        // Closing File Explorer is the same kind of thing.
        foreach (var item in FixCatalog.Load().Where(x => x.Steps.Any(s => s.Kind == FixStepKind.StopExplorer)))
            Assert.Contains(item.Steps, x => x.Kind == FixStepKind.StartExplorer && x.Always);
    }

    [Fact]
    public void TheDangerousThingsTheOldPageDidAreNotThere()
    {
        var text = string.Join('\n', FixCatalog.Load().SelectMany(x => x.Steps).SelectMany(x => x.Args.Append(x.File).Append(x.Path)));
        foreach (var gone in new[] { "bootrec", "bcdedit", "slmgr", "rearm", "Clear-Tpm", "manage-bde", "EnableLUA", "Enable-PSRemoting", "Set-ExecutionPolicy", "advfirewall", "resetrepository", "salvagerepository", "EmptyStandbyList", "SysMain", "pagefile", "oem", "regsvr32" })
            Assert.DoesNotContain(gone, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowsFilesAreRepairedWithDismFirstAndThenSfcAsMicrosoftSays()
    {
        var steps = FixCatalog.Load().Single(x => x.Id == "repair-windows-files").Steps;
        Assert.True(Array.FindIndex(steps, x => x.File == "dism.exe" && x.Args.Contains("/RestoreHealth")) < Array.FindIndex(steps, x => x.File == "sfc.exe"));
        // sfc writes UTF-16 to a pipe; reading it as anything else would show unreadable text.
        Assert.Equal(FixEncoding.Utf16, steps.Single(x => x.File == "sfc.exe").Encoding);
        Assert.Contains(3010, steps.First(x => x.File == "dism.exe").Restart);
    }

    [Fact]
    public void WindowsUpdateIsResetTheGentleWayFirstAndRenamesInsteadOfDeleting()
    {
        var item = FixCatalog.Load().Single(x => x.Id == "reset-windows-update");
        Assert.Empty(FixPlan.DefaultOptions(item));
        var normal = FixPlan.Build(item, []);
        Assert.DoesNotContain(normal, x => x.Path.Contains("catroot2") || x.Path.Contains("DataStore"));
        Assert.Contains(FixPlan.Build(item, ["deep"]), x => x.Path.Contains("catroot2"));
        Assert.All(item.Steps.Where(x => x.Kind == FixStepKind.RenameFolder), x => Assert.True(x.Optional));
    }

    [Fact]
    public void FixesForTheCurrentUserNeverNeedTheAdministratorHelper()
    {
        foreach (var item in FixCatalog.Load().Where(x => x.Scope == FixScope.User))
            Assert.DoesNotContain(item.Steps, x => x.File.Equals("net.exe", StringComparison.OrdinalIgnoreCase) || x.File.Equals("netsh.exe", StringComparison.OrdinalIgnoreCase));
        Assert.All(FixCatalog.Load().Where(x => x.Steps.Any(s => s.Kind is FixStepKind.StopExplorer or FixStepKind.StartExplorer or FixStepKind.RestartExplorer)), x => Assert.Equal(FixScope.User, x.Scope));
    }

    [Theory]
    [InlineData("cmd.exe", "/c", "del")]
    [InlineData("reg.exe", "delete", "HKLM")]
    [InlineData("bootrec.exe", "/fixmbr")]
    [InlineData("curl.exe", "http://example.com")]
    [InlineData("net.exe", "stop", "WinDefend", "/y")]
    [InlineData("net.exe", "stop", "spooler")]
    [InlineData("net.exe", "user", "x", "/add")]
    [InlineData("sfc.exe", "/verifyonly")]
    [InlineData("dism.exe", "/Online", "/Cleanup-Image", "/ResetBase")]
    [InlineData("netsh.exe", "advfirewall", "reset")]
    [InlineData("ipconfig.exe", "/all")]
    [InlineData("chkdsk.exe", "%SystemDrive%", "/f")]
    [InlineData("pnputil.exe", "/delete-driver", "oem1.inf")]
    public void ProgramsAndArgumentsOutsideTheAllowListAreRefused(string file, params string[] args) =>
        Assert.NotEmpty(Check(FixScope.Machine, Run(file, args)));

    [Theory]
    [InlineData("Invoke-WebRequest http://example.com")]
    [InlineData("iex (New-Object Net.WebClient).DownloadString('x')")]
    [InlineData("Remove-Item C:\\* -Recurse")]
    [InlineData("Clear-Tpm")]
    [InlineData("Set-ExecutionPolicy Unrestricted")]
    public void PowerShellThatDownloadsOrDeletesIsRefused(string script) =>
        Assert.NotEmpty(Check(FixScope.Machine, Run("powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script)));

    [Fact]
    public void PowerShellMustRunExactlyOneFixedCommand()
    {
        Assert.Empty(Check(FixScope.Machine, Run("powershell.exe", "-NoProfile", "-NonInteractive", "-Command", "Get-Date")));
        Assert.NotEmpty(Check(FixScope.Machine, Run("powershell.exe", "-Command", "Get-Date")));
        Assert.NotEmpty(Check(FixScope.Machine, Run("powershell.exe", "-NoProfile", "-NonInteractive", "-EncodedCommand", "AAAA")));
    }

    [Fact]
    public void AnAdministratorFixCannotReachIntoAUsersProfileAndAUserFixCannotControlServices()
    {
        Assert.NotEmpty(Check(FixScope.Machine, Run("powershell.exe", "-NoProfile", "-NonInteractive", "-Command", "Get-ChildItem $env:LOCALAPPDATA")));
        Assert.NotEmpty(Check(FixScope.User, Run("net.exe", "start", "spooler")));
    }

    [Fact]
    public void FilesAndFoldersMayOnlyBeRemovedWhereTheGuardSays()
    {
        Assert.Empty(Check(FixScope.Machine, new FixStep { Title = "t", Kind = FixStepKind.DeleteFiles, Path = @"%SystemRoot%\System32\spool\PRINTERS", Patterns = ["*"] }));
        Assert.NotEmpty(Check(FixScope.Machine, new FixStep { Title = "t", Kind = FixStepKind.DeleteFiles, Path = @"%SystemRoot%\System32", Patterns = ["*"] }));
        Assert.NotEmpty(Check(FixScope.Machine, new FixStep { Title = "t", Kind = FixStepKind.DeleteFiles, Path = @"%SystemRoot%\System32\spool\PRINTERS", Patterns = ["*.exe"] }));
        Assert.NotEmpty(Check(FixScope.User, new FixStep { Title = "t", Kind = FixStepKind.DeleteFiles, Path = @"%SystemRoot%\System32\spool\PRINTERS", Patterns = ["*"] }));
        Assert.NotEmpty(Check(FixScope.Machine, new FixStep { Title = "t", Kind = FixStepKind.DeleteFiles, Path = @"%SystemRoot%\System32\spool\PRINTERS", Patterns = [] }));
        Assert.Empty(Check(FixScope.Machine, new FixStep { Title = "t", Kind = FixStepKind.RenameFolder, Path = @"%SystemRoot%\SoftwareDistribution\Download" }));
        Assert.NotEmpty(Check(FixScope.Machine, new FixStep { Title = "t", Kind = FixStepKind.RenameFolder, Path = @"%SystemRoot%\System32" }));
        Assert.NotEmpty(Check(FixScope.Machine, new FixStep { Title = "t", Kind = FixStepKind.StopExplorer }));
    }

    [Fact]
    public void BadCatalogEntriesAreExplained()
    {
        Assert.NotEmpty(FixGuard.Validate([Item(FixScope.Machine), ]));
        Assert.NotEmpty(FixGuard.Validate([Item(FixScope.Machine, Run("ipconfig.exe", "/flushdns")), Item(FixScope.Machine, Run("ipconfig.exe", "/flushdns"))]));
        Assert.NotEmpty(Check(FixScope.Machine, new FixStep { Title = "t", File = "ipconfig.exe", Args = ["/flushdns"], When = "missing" }));
        Assert.NotEmpty(Check(FixScope.Machine, new FixStep { Title = "t", File = "ipconfig.exe", Args = ["/flushdns"], Minutes = 0 }));
        Assert.NotEmpty(Check(FixScope.Machine, new FixStep { Title = "t", File = "ipconfig.exe", Args = ["/flushdns"], Ok = [] }));
        Assert.NotEmpty(FixGuard.Validate([new FixItem { Id = "Bad Id", Name = "n", Group = FixGroup.Apps, Scope = FixScope.User, Summary = "s", Effect = "e", Steps = [new() { Title = "t", File = "wsreset.exe" }] }]));
    }

    [Fact]
    public void OptionsDecideWhichStepsRun()
    {
        var item = new FixItem
        {
            Id = "item", Name = "Item", Group = FixGroup.Windows, Scope = FixScope.Machine, Summary = "s", Effect = "e",
            Options = [new() { Id = "extra", Label = "Extra", Default = true }, new() { Id = "other", Label = "Other" }],
            Steps = [new() { Title = "always", File = "ipconfig.exe", Args = ["/flushdns"] }, new() { Title = "with extra", File = "ipconfig.exe", Args = ["/flushdns"], When = "extra" }, new() { Title = "without other", File = "ipconfig.exe", Args = ["/flushdns"], When = "!other" }]
        };
        Assert.Equal(["extra"], FixPlan.DefaultOptions(item));
        Assert.Equal(["always", "with extra", "without other"], FixPlan.Build(item, ["extra"]).Select(x => x.Title));
        Assert.Equal(["always", "without other"], FixPlan.Build(item, []).Select(x => x.Title));
        Assert.Equal(["always", "with extra"], FixPlan.Build(item, ["extra", "other"]).Select(x => x.Title));
        // An option that does not exist is ignored.
        Assert.Equal(["always", "without other"], FixPlan.Build(item, ["made-up"]).Select(x => x.Title));
    }
}

public sealed class FixRunnerTests
{
    private static FixStep Step(string title, int[]? ok = null, int[]? warn = null, int[]? restart = null, bool optional = false, bool always = false) =>
        new() { Title = title, File = "ipconfig.exe", Args = ["/flushdns"], Ok = ok ?? [0], Warn = warn ?? [], Restart = restart ?? [], Optional = optional, Always = always };

    private static FixItem Item(FixRestart restart = FixRestart.Never, params FixStep[] steps) =>
        new() { Id = "item", Name = "The fix", Group = FixGroup.Windows, Scope = FixScope.Machine, Summary = "s", Effect = "e", Restart = restart, Steps = steps };

    private static Task<PackageActionResult> Run(ScriptedExecutor executor, FixItem item, Collector? progress = null) =>
        new FixRunner(executor).RunAsync(item, [], progress ?? new Collector(), CancellationToken.None);

    [Fact]
    public async Task EveryStepRunsInOrderAndTheResultSaysItWorked()
    {
        var executor = new ScriptedExecutor();
        var progress = new Collector();
        var result = await Run(executor, Item(FixRestart.Never, Step("one"), Step("two"), Step("three")), progress);

        Assert.Equal(ActionOutcome.Completed, result.Outcome); Assert.Equal("The fix finished", result.Message);
        Assert.Equal(["one", "two", "three"], executor.Ran);
        Assert.Equal(["Step 1 of 3: one", "Step 2 of 3: two", "Step 3 of 3: three"], progress.Lines(CommandOutputKind.Progress));
        Assert.Contains("OK       two", result.Detail);
    }

    [Fact]
    public async Task OutputFromAStepReachesTheCallerWhileItRuns()
    {
        var executor = new ScriptedExecutor { Prints = { ["one"] = "Windows IP Configuration\n" } };
        var progress = new Collector();
        await Run(executor, Item(FixRestart.Never, Step("one")), progress);
        Assert.Contains("Windows IP Configuration", progress.Text);
    }

    [Fact]
    public async Task AFailedStepStopsTheFixButTheStepsThatPutThingsBackStillRun()
    {
        var executor = new ScriptedExecutor { Codes = { ["stop"] = 0, ["delete"] = 5 } };
        var result = await Run(executor, Item(FixRestart.Never, Step("stop"), Step("delete"), Step("skipped"), Step("start", always: true)));

        Assert.Equal(ActionOutcome.Failed, result.Outcome); Assert.Contains("Couldn’t finish The fix", result.Message);
        Assert.Equal(["stop", "delete", "start"], executor.Ran);
        Assert.Contains("FAILED   delete", result.Detail); Assert.Contains("Stopped at “delete”", result.Detail);
    }

    [Fact]
    public async Task AFailedStepKeepsTheLastThingItSaidInTheSummary()
    {
        var executor = new ScriptedExecutor { Codes = { ["scan"] = 5 }, Prints = { ["scan"] = "Checking...\nAccess Denied as you do not have sufficient privileges.\n" } };
        var result = await Run(executor, Item(FixRestart.Never, Step("scan")));

        Assert.Contains("“Access Denied as you do not have sufficient privileges.”", result.Detail);
    }

    [Fact]
    public async Task ACrashedProgramIsNamedInTheSummary()
    {
        var executor = new ScriptedExecutor { Codes = { ["crash"] = unchecked((int)0xC0000005) } };
        var result = await Run(executor, Item(FixRestart.Never, Step("crash")));

        Assert.Contains("0xC0000005, the program crashed", result.Detail);
    }

    [Fact]
    public async Task AnOptionalStepThatFailsIsAWarningAndTheFixGoesOn()
    {
        var executor = new ScriptedExecutor { Codes = { ["maybe"] = 9 } };
        var result = await Run(executor, Item(FixRestart.Never, Step("maybe", optional: true), Step("after")));

        Assert.Equal(ActionOutcome.Unverified, result.Outcome); Assert.Contains("finished with warnings", result.Message);
        Assert.Equal(["maybe", "after"], executor.Ran);
    }

    [Fact]
    public async Task AWarningCodeIsNotAFailure()
    {
        var executor = new ScriptedExecutor { Codes = { ["scan"] = 1 } };
        var result = await Run(executor, Item(FixRestart.Never, Step("scan", warn: [1])));
        Assert.Equal(ActionOutcome.Unverified, result.Outcome);
        Assert.Contains("WARNING  scan", result.Detail);
    }

    [Fact]
    public async Task ARestartCodeMeansRestartRequiredNotFailure()
    {
        var executor = new ScriptedExecutor { Codes = { ["dism"] = 3010 } };
        var result = await Run(executor, Item(FixRestart.Maybe, Step("dism", restart: [3010]), Step("sfc")));
        Assert.Equal(ActionOutcome.RestartRequired, result.Outcome); Assert.Equal(["dism", "sfc"], executor.Ran);
    }

    [Fact]
    public async Task AFixThatAlwaysNeedsARestartSaysSoOnlyWhenItWorked()
    {
        Assert.Equal(ActionOutcome.RestartRequired, (await Run(new ScriptedExecutor(), Item(FixRestart.Always, Step("one")))).Outcome);
        Assert.Equal(ActionOutcome.Failed, (await Run(new ScriptedExecutor { Codes = { ["one"] = 7 } }, Item(FixRestart.Always, Step("one")))).Outcome);
    }

    [Fact]
    public async Task AStepThatRunsTooLongOrCannotStartIsAFailureWithTheReasonShown()
    {
        var slow = new ScriptedExecutor { Throws = { ["one"] = new TimeoutException() } };
        var progress = new Collector();
        var result = await Run(slow, Item(FixRestart.Never, Step("one")), progress);
        Assert.Equal(ActionOutcome.Failed, result.Outcome); Assert.Contains("took longer than", progress.Text);

        var missing = new ScriptedExecutor { Throws = { ["one"] = new InvalidOperationException("OneDrive is not installed on this PC, so there is nothing to run.") } };
        progress = new Collector();
        Assert.Equal(ActionOutcome.Failed, (await Run(missing, Item(FixRestart.Never, Step("one")), progress)).Outcome);
        Assert.Contains("OneDrive is not installed", progress.Text);
    }

    [Fact]
    public async Task CancellingStopsTheFixInsteadOfCallingItAFailure()
    {
        var executor = new ScriptedExecutor { Throws = { ["one"] = new OperationCanceledException() } };
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FixRunner(executor).RunAsync(Item(FixRestart.Never, Step("one")), [], new Collector(), source.Token));
    }

    [Fact]
    public async Task OnlyTheStepsTheOptionsSwitchOnAreRun()
    {
        var item = new FixItem
        {
            Id = "item", Name = "The fix", Group = FixGroup.Windows, Scope = FixScope.Machine, Summary = "s", Effect = "e",
            Options = [new() { Id = "extra", Label = "Extra" }],
            Steps = [new() { Title = "always", File = "ipconfig.exe", Args = ["/flushdns"] }, new() { Title = "extra", File = "ipconfig.exe", Args = ["/flushdns"], When = "extra" }]
        };
        var executor = new ScriptedExecutor();
        await new FixRunner(executor).RunAsync(item, [], new Collector(), CancellationToken.None);
        Assert.Equal(["always"], executor.Ran);
        executor.Ran.Clear();
        var progress = new Collector();
        await new FixRunner(executor).RunAsync(item, ["extra"], progress, CancellationToken.None);
        Assert.Equal(["always", "extra"], executor.Ran);
        // The position reported to the executor is the step's place in the whole catalog entry, which is what the helper uses.
        Assert.Contains("Step 2 of 2: extra", progress.Text);
    }
}

public sealed class FixServiceTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "tidywindow-fix-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(folder, true); } catch (IOException) { } }

    private static FixItem Item(string id, FixScope scope) =>
        new() { Id = id, Name = id, Group = FixGroup.Windows, Scope = scope, Summary = "s", Effect = "e", Steps = [new() { Title = "run", File = "ipconfig.exe", Args = ["/flushdns"] }] };

    [Fact]
    public async Task AFixForTheCurrentUserRunsHereWithoutAnyPrompt()
    {
        var local = new ScriptedExecutor(); var elevated = new ScriptedExecutor(); var unlocks = 0;
        var service = new FixService([Item("a", FixScope.User)], local, elevated, () => false, () => { unlocks++; return Task.CompletedTask; });

        var result = await service.RunAsync(new("a", []), new Collector(), CancellationToken.None);

        Assert.Equal(ActionOutcome.Completed, result.Outcome); Assert.Equal(0, unlocks);
        Assert.Single(local.Ran); Assert.Empty(elevated.Ran);
    }

    [Fact]
    public async Task AMachineFixGetsOnePromptAndThenRunsInTheHelper()
    {
        var local = new ScriptedExecutor(); var elevated = new ScriptedExecutor(); var unlocks = 0;
        var service = new FixService([Item("a", FixScope.Machine), Item("b", FixScope.Machine)], local, elevated, () => false, () => { unlocks++; return Task.CompletedTask; });
        var progress = new Collector();

        await service.RunAsync(new("a", []), progress, CancellationToken.None);
        await service.RunAsync(new("b", []), new Collector(), CancellationToken.None);

        Assert.Equal(2, unlocks); // the helper keeps its own connection; asking again is a no-op there
        Assert.Empty(local.Ran); Assert.Equal(2, elevated.Ran.Count);
        Assert.Contains("administrator rights", progress.Text);
    }

    [Fact]
    public async Task WhenTheAppIsAlreadyAnAdministratorNoHelperIsNeeded()
    {
        var local = new ScriptedExecutor(); var elevated = new ScriptedExecutor(); var unlocks = 0;
        var service = new FixService([Item("a", FixScope.Machine)], local, elevated, () => true, () => { unlocks++; return Task.CompletedTask; });

        await service.RunAsync(new("a", []), new Collector(), CancellationToken.None);

        Assert.Equal(0, unlocks); Assert.Single(local.Ran); Assert.Empty(elevated.Ran);
    }

    [Fact]
    public async Task ADeclinedPromptRunsNothingAndSaysSo()
    {
        var local = new ScriptedExecutor(); var elevated = new ScriptedExecutor();
        var service = new FixService([Item("a", FixScope.Machine)], local, elevated, () => false, () => throw new OperationCanceledException());

        var result = await service.RunAsync(new("a", []), new Collector(), CancellationToken.None);

        Assert.Equal(ActionOutcome.Failed, result.Outcome); Assert.Contains("declined", result.Detail);
        Assert.Empty(local.Ran); Assert.Empty(elevated.Ran);
    }

    [Fact]
    public async Task AFixThatIsNotInTheCatalogIsRefused()
    {
        var local = new ScriptedExecutor();
        var result = await new FixService([Item("a", FixScope.User)], local, null, () => false, null).RunAsync(new("made-up", []), new Collector(), CancellationToken.None);
        Assert.Equal(ActionOutcome.Failed, result.Outcome); Assert.Empty(local.Ran);
    }

    [Fact]
    public async Task TheLastRunOfEachFixIsRemembered()
    {
        var store = new FixRunStore(folder);
        var when = new DateTimeOffset(2026, 10, 30, 19, 0, 0, TimeSpan.Zero);
        var service = new FixService([Item("a", FixScope.User)], new ScriptedExecutor(), null, () => false, null, store, () => when);

        await service.RunAsync(new("a", []), new Collector(), CancellationToken.None);

        var note = store.Get("a")!;
        Assert.Equal(when, note.When); Assert.Equal("Completed", note.Outcome);
        Assert.Equal("Completed", new FixRunStore(folder).Get("a")!.Outcome);
        File.WriteAllText(Path.Combine(folder, "last-runs.json"), "{ not json");
        Assert.Empty(new FixRunStore(folder).All());
    }
}

public sealed class FixHostTests
{
    private static FixItem Machine(string id = "demo") => new()
    {
        Id = id, Name = "Demo", Group = FixGroup.Windows, Scope = FixScope.Machine, Summary = "s", Effect = "e",
        Options = [new() { Id = "extra", Label = "Extra" }],
        Steps = [new() { Title = "first", File = "ipconfig.exe", Args = ["/flushdns"] }, new() { Title = "extra", File = "ipconfig.exe", Args = ["/flushdns"], When = "extra" }]
    };
    private static FixItem User() => new() { Id = "mine", Name = "Mine", Group = FixGroup.Apps, Scope = FixScope.User, Summary = "s", Effect = "e", Steps = [new() { Title = "go", File = "wsreset.exe" }] };

    private static FixSession Session(IFixStepExecutor executor, bool admin = true, params FixItem[] catalog) =>
        new(pipe => FixHost.RunAsync([FixHost.Command, pipe], executor, () => admin, TimeSpan.FromSeconds(20), catalog.Length > 0 ? catalog : [Machine(), User()]));

    private static FixStepContext Context(FixItem item, int index = 0, params string[] options) => new(item, options, index, item.Steps[index]);

    [Fact]
    public async Task OutputArrivesLineByLineAsTheHelperProducesItAndTheExitCodeComesBack()
    {
        var helper = new LiveExecutor(async (_, progress, _) =>
        {
            progress.Report(new("line one\n")); await Task.Delay(50);
            progress.Report(new("careful\n", CommandOutputKind.Warning));
            return 3010;
        });
        using var session = Session(helper);
        await session.OpenAsync();
        var progress = new Collector();

        var code = await session.ExecuteAsync(Context(Machine()), progress, CancellationToken.None);

        Assert.Equal(3010, code);
        Assert.Equal(["line one"], progress.Lines(CommandOutputKind.Output)); Assert.Equal(["careful"], progress.Lines(CommandOutputKind.Warning));
    }

    [Fact]
    public async Task TheHelperRebuildsTheStepFromTheCatalogAndNeverRunsWhatItIsToldDirectly()
    {
        var seen = new List<FixStepContext>();
        var helper = new LiveExecutor((context, _, _) => { seen.Add(context); return Task.FromResult(0); });
        using var session = Session(helper);
        await session.OpenAsync();

        await session.ExecuteAsync(Context(Machine(), 1, "extra"), new Collector(), CancellationToken.None);

        var ran = Assert.Single(seen);
        Assert.Equal("extra", ran.Step.Title); Assert.Equal(["extra"], ran.Options); Assert.Equal(1, ran.Index);
    }

    [Fact]
    public async Task AStepThatIsNotInThePlanIsRefused()
    {
        var helper = new LiveExecutor((_, _, _) => Task.FromResult(0));
        using var session = Session(helper);
        await session.OpenAsync();
        // Step 1 only runs with the "extra" option; asking for it without that option is not a request the helper honours.
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(Context(Machine(), 1), new Collector(), CancellationToken.None));
        Assert.Equal(0, helper.Calls);
    }

    [Fact]
    public async Task OnlyMachineFixesInTheCatalogAreEverRunByTheHelper()
    {
        var helper = new LiveExecutor((_, _, _) => Task.FromResult(0));
        using var session = Session(helper);
        await session.OpenAsync();
        // A fix for the current user must never run in a helper that may belong to another account.
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(Context(User()), new Collector(), CancellationToken.None));
        // Neither can an id the helper's catalog does not have.
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(Context(Machine("made-up")), new Collector(), CancellationToken.None));
        Assert.Equal(0, helper.Calls);
    }

    [Fact]
    public async Task ATimeoutInTheHelperIsATimeoutHere()
    {
        using var session = Session(new LiveExecutor((_, _, _) => throw new TimeoutException()));
        await session.OpenAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => session.ExecuteAsync(Context(Machine()), new Collector(), CancellationToken.None));
    }

    [Fact]
    public async Task AFailureInTheHelperComesBackWithItsReason()
    {
        using var session = Session(new LiveExecutor((_, _, _) => throw new InvalidOperationException("OneDrive is not installed on this PC.")));
        await session.OpenAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(Context(Machine()), new Collector(), CancellationToken.None));
        Assert.Contains("OneDrive", error.Message);
    }

    [Fact]
    public async Task StepsRunOneAfterAnotherOnTheSameConnection()
    {
        var count = 0;
        using var session = Session(new LiveExecutor((_, progress, _) => { progress.Report(new($"run {++count}\n")); return Task.FromResult(count); }));
        await session.OpenAsync();
        var progress = new Collector();

        var first = await session.ExecuteAsync(Context(Machine()), progress, CancellationToken.None);
        var second = await session.ExecuteAsync(Context(Machine()), progress, CancellationToken.None);

        Assert.Equal([1, 2], [first, second]); Assert.Equal(["run 1", "run 2"], progress.Lines(CommandOutputKind.Output));
    }

    [Fact]
    public async Task CancellingStopsTheStepRunningInTheHelper()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var helper = new LiveExecutor(async (_, _, token) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { stopped.SetResult(true); throw; }
            return 0;
        });
        using var session = Session(helper);
        await session.OpenAsync();
        using var source = new CancellationTokenSource();

        var running = session.ExecuteAsync(Context(Machine()), new Collector(), source.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        source.Cancel();

        Assert.True(await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsAnyAsync<Exception>(() => running);
    }

    [Fact]
    public async Task AfterACancelledStepTheNextStepOnTheSameConnectionStillWorks()
    {
        var first = true;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = Session(new LiveExecutor(async (_, _, token) =>
        {
            if (!first) return 42;
            first = false; started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        }));
        await session.OpenAsync();
        using var source = new CancellationTokenSource();

        var cancelled = session.ExecuteAsync(Context(Machine()), new Collector(), source.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        source.Cancel();
        await Assert.ThrowsAnyAsync<Exception>(() => cancelled);

        // The helper must not answer the cancel it already acted on, or this step would read that stray reply and fail.
        Assert.Equal(42, await session.ExecuteAsync(Context(Machine()), new Collector(), CancellationToken.None));
    }

    [Fact]
    public async Task AHelperWithoutAdministratorRightsIsNotAccepted()
    {
        using var session = Session(new LiveExecutor((_, _, _) => Task.FromResult(0)), admin: false);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.OpenAsync());
        Assert.False(session.IsOpen);
    }

    [Fact]
    public async Task ADeclinedPromptStopsEverything()
    {
        using var session = new FixSession(_ => throw new OperationCanceledException("declined"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.OpenAsync());
        Assert.False(session.IsOpen);
    }

    [Fact]
    public async Task ClosingTheSessionStopsTheHelper()
    {
        var finished = new TaskCompletionSource();
        using var session = new FixSession(async pipe =>
        {
            await FixHost.RunAsync([FixHost.Command, pipe], new LiveExecutor((_, _, _) => Task.FromResult(0)), () => true, TimeSpan.FromSeconds(20), [Machine()]);
            finished.SetResult();
        });
        await session.OpenAsync();

        session.Close();

        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(session.IsOpen);
    }

    private sealed class LiveExecutor(Func<FixStepContext, IProgress<CommandOutput>, CancellationToken, Task<int>> run) : IFixStepExecutor
    {
        public int Calls;
        public Task<int> ExecuteAsync(FixStepContext context, IProgress<CommandOutput> progress, CancellationToken cancellationToken) { Calls++; return run(context, progress, cancellationToken); }
    }
}

public sealed class OutputSplitterTests
{
    private static (OutputSplitter Splitter, List<string> Lines) Make()
    {
        var lines = new List<string>();
        return (new OutputSplitter((text, _) => lines.Add(text)), lines);
    }

    [Fact]
    public void WholeLinesAreReportedWhateverTheLineEndingAndSplitAcrossChunks()
    {
        var (splitter, lines) = Make();
        splitter.Feed("first\r\nsec", CommandOutputKind.Output); splitter.Feed("ond\nthird", CommandOutputKind.Output); splitter.Feed("\r\r\n", CommandOutputKind.Output);
        splitter.Finish(CommandOutputKind.Output);
        Assert.Equal(["first", "second", "third"], lines);
    }

    [Fact]
    public void ADismBarThatEndsEveryUpdateWithALineEndingIsStillShownAboutEveryTenPercent()
    {
        // Captured from a real "dism /Online /Cleanup-Image /ScanHealth": each update is \r[bar] \r\n.
        var (splitter, lines) = Make();
        splitter.Feed("\r\nDeployment Image Servicing and Management tool\r\nVersion: 10.0.26100.9549\r\n\r\n", CommandOutputKind.Output);
        for (var tenth = 49; tenth <= 1000; tenth += 10)
        {
            var bar = new string('=', tenth / 40).PadRight(27) + $"{tenth / 10.0:0.0}%".PadRight(27);
            splitter.Feed($"\r[{bar}] \r\n", CommandOutputKind.Output);
        }
        splitter.Feed("The restore operation completed successfully.\r\n", CommandOutputKind.Output); splitter.Finish(CommandOutputKind.Output);

        Assert.InRange(lines.Count(x => x.StartsWith('[')), 9, 12);
        Assert.Contains(lines, x => x.Contains("4.9%")); Assert.Equal("The restore operation completed successfully.", lines[^1]);
        Assert.Equal("Version: 10.0.26100.9549", lines[1]);
    }

    [Fact]
    public void BlankLinesAreDropped()
    {
        var (splitter, lines) = Make();
        splitter.Feed("\r\n\r\nhello\r\n   \r\n", CommandOutputKind.Output); splitter.Finish(CommandOutputKind.Output);
        Assert.Equal(["hello"], lines);
    }

    [Fact]
    public void AProgressBarThatRedrawsOneLineShowsAboutEveryTenPercent()
    {
        var (splitter, lines) = Make();
        splitter.Feed("Deployment Image Servicing\r\n", CommandOutputKind.Output);
        for (var i = 0; i <= 1000; i += 3) splitter.Feed($"[=====   {i / 10.0:0.0}%   ]\r", CommandOutputKind.Output);
        splitter.Feed("[===== 100.0% =====]\r\nThe operation completed successfully.\r\n", CommandOutputKind.Output);
        splitter.Finish(CommandOutputKind.Output);

        Assert.Equal("Deployment Image Servicing", lines[0]); Assert.Equal("The operation completed successfully.", lines[^1]);
        Assert.InRange(lines.Count, 8, 16);
        Assert.Contains(lines, x => x.Contains("100.0%"));
    }

    [Fact]
    public void AProgressLineThatEndsWithANewlineIsAnOrdinaryLine()
    {
        var (splitter, lines) = Make();
        splitter.Feed("Verification 100% complete.\r\n", CommandOutputKind.Output);
        splitter.Finish(CommandOutputKind.Output);
        Assert.Equal(["Verification 100% complete."], lines);
    }

    [Fact]
    public void TheLastLineIsReportedEvenWithoutALineEnding()
    {
        var (splitter, lines) = Make();
        splitter.Feed("done", CommandOutputKind.Output); splitter.Finish(CommandOutputKind.Output);
        Assert.Equal(["done"], lines);
    }
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class FixExecutorLiveTests
{
    // These start real, harmless, read-only Windows programs to check that output really is read as it arrives and in the right text encoding.
    private static bool Applies => OperatingSystem.IsWindows();

    private static FixStepContext Context(FixStep step) =>
        new(new() { Id = "live", Name = "Live", Group = FixGroup.Windows, Scope = FixScope.User, Summary = "s", Effect = "e", Steps = [step] }, [], 0, step);

    [Fact]
    public async Task AProgramOutputIsStreamedAndReadInTheConsoleCodePage()
    {
        if (!Applies) return;
        var progress = new Collector();
        var code = await new FixStepExecutor().ExecuteAsync(Context(new() { Title = "ipconfig", File = "ipconfig.exe", Args = [] }), progress, CancellationToken.None);
        Assert.Equal(0, code);
        Assert.Contains("Windows IP Configuration", progress.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('\0', progress.Text);
    }

    [Fact]
    public async Task SfcSendsUtf16AndItIsReadAsReadableText()
    {
        if (!Applies) return;
        var progress = new Collector();
        // Without administrator rights sfc only says that it needs them, in UTF-16; readable text here proves the decoding.
        var code = await new FixStepExecutor().ExecuteAsync(Context(new() { Title = "sfc", File = "sfc.exe", Args = ["/?"], Encoding = FixEncoding.Utf16, Ok = [0, 1] }), progress, CancellationToken.None);
        Assert.InRange(code, 0, 1);
        // The help wording differs between Windows builds, but every one names the program; UTF-16 read wrongly would be "S F C" with gaps.
        Assert.Contains("sfc", progress.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('\0', progress.Text);
    }

    [Fact]
    public async Task WindowsPowerShellOutputIsReadAsUtf8WhateverTheConsolePageIs()
    {
        if (!Applies) return;
        var progress = new Collector();
        var step = new FixStep { Title = "ps", File = "powershell.exe", Args = ["-NoProfile", "-NonInteractive", "-Command", "'caf' + [char]0x00E9 + ' ' + [char]0x2713; 'second line'"], Encoding = FixEncoding.Utf8 };
        var code = await new FixStepExecutor().ExecuteAsync(Context(step), progress, CancellationToken.None);
        Assert.Equal(0, code);
        Assert.Contains("caf\u00e9 \u2713", progress.Text); Assert.Contains("second line", progress.Text);
    }

    [Fact]
    public async Task APowerShellErrorComesBackAsAnErrorLineNotAsRawMarkup()
    {
        if (!Applies) return;
        var progress = new Collector();
        var step = new FixStep { Title = "ps", File = "powershell.exe", Args = ["-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference = 'Stop'; throw 'something broke'"], Encoding = FixEncoding.Utf8 };
        var code = await new FixStepExecutor().ExecuteAsync(Context(step), progress, CancellationToken.None);
        Assert.NotEqual(0, code);
        Assert.Contains("something broke", progress.Text); Assert.DoesNotContain("CLIXML", progress.Text); Assert.DoesNotContain("<Objs", progress.Text);
    }

    [Fact]
    public async Task OutputIsReportedWhileTheProgramIsStillRunning()
    {
        if (!Applies) return;
        var progress = new Collector();
        var first = new TaskCompletionSource();
        var watching = new Progress(progress, () => first.TrySetResult());
        var step = new FixStep { Title = "ps", File = "powershell.exe", Args = ["-NoProfile", "-NonInteractive", "-Command", "'early'; Start-Sleep -Seconds 4; 'late'"], Encoding = FixEncoding.Utf8 };
        var running = new FixStepExecutor().ExecuteAsync(Context(step), watching, CancellationToken.None);

        await first.Task.WaitAsync(TimeSpan.FromSeconds(3.5));
        Assert.False(running.IsCompleted); Assert.DoesNotContain("late", progress.Text);
        await running;
        Assert.Contains("late", progress.Text);
    }

    [Fact]
    public async Task CancellingKillsTheProgram()
    {
        if (!Applies) return;
        using var source = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
        var step = new FixStep { Title = "ps", File = "powershell.exe", Args = ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60"], Encoding = FixEncoding.Utf8 };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FixStepExecutor().ExecuteAsync(Context(step), new Collector(), source.Token));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task ProgramsAndFoldersOutsideTheListAreRefusedEvenWhenTheStepIsBuiltByHand()
    {
        if (!Applies) return;
        var executor = new FixStepExecutor();
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(Context(new() { Title = "x", File = "cmd.exe", Args = ["/c", "echo no"] }), new Collector(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(Context(new() { Title = "x", Kind = FixStepKind.DeleteFiles, Path = @"%USERPROFILE%\Documents", Patterns = ["*"] }), new Collector(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(Context(new() { Title = "x", Kind = FixStepKind.RenameFolder, Path = @"%SystemRoot%\System32" }), new Collector(), CancellationToken.None));
    }

    [Fact]
    public async Task AProgramThatIsNotInstalledIsSaidPlainly()
    {
        if (!Applies) return;
        var step = new FixStep { Title = "x", File = "onedrive", Args = ["/reset"] };
        if (FixGuard.Resolve("onedrive") is not null) return;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new FixStepExecutor().ExecuteAsync(Context(step), new Collector(), CancellationToken.None));
        Assert.Contains("not installed", error.Message);
    }

    private sealed class Progress(Collector inner, Action onFirst) : IProgress<CommandOutput>
    {
        public void Report(CommandOutput value) { inner.Report(value); if (value.Text.Contains("early")) onFirst(); }
    }

    [Fact]
    public void TheCatalogProgramsAllExistOnThisPcExceptOptionalOnes()
    {
        if (!Applies) return;
        foreach (var step in FixCatalog.Load().SelectMany(x => x.Steps).Where(x => x.Kind == FixStepKind.Run && !x.File.Equals("onedrive", StringComparison.OrdinalIgnoreCase)))
            Assert.True(File.Exists(FixGuard.Resolve(step.File)), step.File);
    }
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class FixQueueLiveTests
{
    [Fact]
    public async Task AFixShowsItsOutputInTheQueueWhileItIsStillRunning()
    {
        if (!OperatingSystem.IsWindows()) return;
        // A real program, run by the real executor, through the real queue the Queue page reads.
        var item = new FixItem
        {
            Id = "live-demo", Name = "Live demo", Group = FixGroup.Windows, Scope = FixScope.User, Summary = "s", Effect = "e",
            Steps =
            [
                new() { Title = "Print, wait, print", File = "powershell.exe", Args = ["-NoProfile", "-NonInteractive", "-Command", "'first line'; Start-Sleep -Seconds 4; 'last line'"], Encoding = FixEncoding.Utf8 },
                new() { Title = "Second step", File = "ipconfig.exe", Args = [] }
            ]
        };
        var service = new FixService([item], new FixStepExecutor(), null, () => false, null);
        var queue = PackageOperationQueue.WithLiveOutput((operation, progress, token) => operation.Fix is { } run ? service.RunAsync(run, progress, token) : throw new InvalidOperationException());

        var added = queue.Enqueue([PackageOperation.ForFix(item, [])]);
        Assert.Single(added);
        // The same fix cannot be queued twice at once.
        Assert.Empty(queue.Enqueue([PackageOperation.ForFix(item, [])]));

        string midway = "";
        for (var i = 0; i < 60 && !midway.Contains("first line"); i++)
        {
            await Task.Delay(100);
            midway = queue.Snapshot().Entries.Single().Output;
        }
        var during = queue.Snapshot().Entries.Single();
        Assert.Equal(QueueItemState.Running, during.State);
        Assert.Contains("Step 1 of 2: Print, wait, print", midway); Assert.Contains("first line", midway);
        Assert.DoesNotContain("last line", midway);

        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var done = queue.Snapshot().Entries.Single();
        Assert.Equal(QueueItemState.Finished, done.State); Assert.Equal(ActionOutcome.Completed, done.Result!.Outcome);
        Assert.Contains("last line", done.Output); Assert.Contains("Step 2 of 2: Second step", done.Output);
        Assert.Equal("Live demo finished", done.Result.Message);
    }

    [Fact]
    public void AFixAndAPackageNeverShareAQueueSlot()
    {
        var item = new FixItem { Id = "winget", Name = "n", Group = FixGroup.Windows, Scope = FixScope.User, Summary = "s", Effect = "e", Steps = [new() { Title = "t", File = "wsreset.exe" }] };
        var fix = PackageOperation.ForFix(item, []);
        Assert.StartsWith(PackageOperation.FixPrefix, fix.ResourceKey);
        Assert.NotEqual(PackageOperation.Resource(ManagerKind.Winget, "winget"), fix.ResourceKey);
        Assert.Equal(PackageOperationKind.Fix, fix.Kind);
    }
}
