using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class SystemCleanTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
    private readonly string root = Path.Combine(Path.GetTempPath(), "tw-system-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeTools tools = new();

    public SystemCleanTests() => Directory.CreateDirectory(root);
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private CleanupContext Context(bool admin = true) => new()
    {
        Variable = name => name switch { "WINDIR" => Path.Combine(root, "Windows"), "SYSTEMDRIVE" => root, "USERPROFILE" => Path.Combine(root, "Users", "me"), "LOCALAPPDATA" => Path.Combine(root, "Users", "me", "AppData", "Local"), _ => null },
        IsAdministrator = admin, Now = () => Now, RunningProcesses = () => new HashSet<string>(), Parallelism = 4,
        HandlerFolders = name => name == "Missing" ? null : []
    };

    private static CleanupRule Rule(string id, CleanupAction action, string[]? paths = null, bool admin = true) =>
        new() { Id = id, Group = "Windows", Name = id, Detail = "x", Admin = admin, Action = action, Paths = paths ?? [] };

    private string Make(string relative, int bytes, int ageDays = 10)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, Now.AddDays(-ageDays));
        return path;
    }

    private sealed class FakeTools : ISystemTools
    {
        public readonly List<string> Commands = [];
        public readonly List<(string Handler, bool On)> Handlers = [];
        public int ExitCode;
        public long Free = 1_000;
        public long FreeAfter = 1_000;
        public Action? OnRun;
        private bool ran;
        public string Output = "";
        public Task<int> RunAsync(string fileName, string[] arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Commands.Add(Path.GetFileName(fileName) + " " + string.Join(' ', arguments));
            ran = true; OnRun?.Invoke();
            return Task.FromResult(ExitCode);
        }
        public async Task<(int ExitCode, string Output)> RunForOutputAsync(string fileName, string[] arguments, TimeSpan timeout, CancellationToken cancellationToken) => (await RunAsync(fileName, arguments, timeout, cancellationToken), Output);
        public void SetCleanupHandler(string handler, int slot, bool enabled) => Handlers.Add((handler, enabled));
        public long FreeBytes(string drive) => ran ? FreeAfter : Free;
    }

    [Fact]
    public async Task DiskCleanupHandlersAreSwitchedOnRunTogetherAndAlwaysSwitchedBackOff()
    {
        tools.Free = 1_000; tools.FreeAfter = 6_000;
        var rule = new CleanupRule { Id = "drivers", Group = "Windows", Name = "d", Admin = true, Action = CleanupAction.DiskCleanup, Handlers = ["Device Driver Packages", "Missing", "Language Pack"] };
        var report = await new SystemCleanJob(Context(), tools).RunAsync([rule], ["drivers"]);
        Assert.Equal(["cleanmgr.exe /sagerun:5099"], tools.Commands);
        Assert.Equal([("Device Driver Packages", true), ("Language Pack", true), ("Device Driver Packages", false), ("Language Pack", false)], tools.Handlers);
        Assert.Equal(5_000, report.Result.FreedBytes);
    }

    [Fact]
    public async Task ADiskCleanupFailureStillSwitchesTheHandlersOffAndIsReported()
    {
        tools.ExitCode = 7;
        var rule = new CleanupRule { Id = "drivers", Group = "Windows", Name = "d", Admin = true, Action = CleanupAction.DiskCleanup, Handlers = ["Device Driver Packages"] };
        var report = await new SystemCleanJob(Context(), tools).RunAsync([rule], ["drivers"]);
        Assert.Equal(1, report.Result.Failed);
        Assert.Equal(("Device Driver Packages", false), tools.Handlers.Last());
        Assert.Contains(report.Notes, x => x.Contains("error code 7"));
    }

    [Fact]
    public async Task OnlyRulesMarkedForAdministratorsCanRunInTheHelper()
    {
        var rule = new CleanupRule { Id = "drivers", Group = "Windows", Name = "d", Admin = false, Action = CleanupAction.DiskCleanup, Handlers = ["Device Driver Packages"] };
        var report = await new SystemCleanJob(Context(), tools).RunAsync([rule], ["drivers"]);
        Assert.Empty(tools.Commands);
        Assert.Equal(0, report.Result.FreedBytes);
    }

    [Fact]
    public async Task WithoutAdministratorRightsNothingIsTouched()
    {
        var file = Make("Windows/Temp/a.tmp", 100);
        var rules = new[] { new CleanupRule { Id = "windows-temp", Group = "Windows", Name = "t", Admin = true, Paths = ["%WINDIR%\\Temp"] } };
        var report = await new SystemCleanJob(Context(admin: false), tools).RunAsync(rules, ["windows-temp"]);
        Assert.True(File.Exists(file));
        Assert.Equal(0, report.Result.FreedBytes);
        Assert.NotEmpty(report.Notes);
        Assert.Empty(tools.Commands);
    }

    [Fact]
    public async Task OnlyRulesMarkedForAdministratorsCanRunInTheElevatedJob()
    {
        var file = Make("Users/me/AppData/Local/Temp/a.tmp", 100);
        var rules = new[] { new CleanupRule { Id = "user-temp", Group = "Windows", Name = "t", Paths = ["%LOCALAPPDATA%\\Temp"] } };
        var report = await new SystemCleanJob(Context(), tools).RunAsync(rules, ["user-temp"]);
        Assert.True(File.Exists(file));
        Assert.Equal(0, report.Result.Removed);
    }

    [Fact]
    public async Task FileRulesInProtectedWindowsFoldersAreCleanedThroughTheNormalCleaner()
    {
        Make("Windows/SoftwareDistribution/Download/pkg/a.cab", 400);
        var rules = new[] { new CleanupRule { Id = "update-downloads", Group = "Windows", Name = "u", Admin = true, Paths = ["%WINDIR%\\SoftwareDistribution\\Download"] } };
        var report = await new SystemCleanJob(Context(), tools).RunAsync(rules, ["update-downloads"]);
        Assert.Equal(400, report.Result.FreedBytes);
        Assert.False(File.Exists(Path.Combine(root, "Windows", "SoftwareDistribution", "Download", "pkg", "a.cab")));
        Assert.True(Directory.Exists(Path.Combine(root, "Windows", "SoftwareDistribution", "Download")));
    }

    [Fact]
    public async Task ComponentStoreCleanupUsesDismAndMeasuresTheFreeSpaceGained()
    {
        tools.FreeAfter = 5_000;
        var report = await new SystemCleanJob(Context(), tools).RunAsync([Rule("component-store", CleanupAction.ComponentStore)], ["component-store"]);
        Assert.Equal("Dism.exe /Online /Cleanup-Image /StartComponentCleanup", Assert.Single(tools.Commands));
        Assert.Equal(4_000, report.Result.FreedBytes);
    }

    [Fact]
    public async Task ADismFailureIsReportedNotHidden()
    {
        tools.ExitCode = 5;
        var report = await new SystemCleanJob(Context(), tools).RunAsync([Rule("component-store", CleanupAction.ComponentStore)], ["component-store"]);
        Assert.Equal(1, report.Result.Failed);
        Assert.Contains(report.Notes, x => x.Contains("error code 5"));
    }

    [Fact]
    public async Task PreviousWindowsUsesTheDiskCleanupHandlerAndAlwaysSwitchesItBackOff()
    {
        var old = Make("Windows.old/Windows/x.dll", 900);
        tools.OnRun = () => Directory.Delete(Path.Combine(root, "Windows.old"), true);
        var rule = Rule("previous-windows", CleanupAction.PreviousWindows, ["%SYSTEMDRIVE%\\Windows.old"]);
        var report = await new SystemCleanJob(Context(), tools).RunAsync([rule], ["previous-windows"]);
        Assert.Contains("cleanmgr.exe /sagerun:5099", tools.Commands);
        Assert.Equal([("Previous Installations", true), ("Previous Installations", false)], tools.Handlers);
        Assert.Equal(900, report.Result.FreedBytes);
        Assert.False(File.Exists(old));
    }

    [Fact]
    public async Task APreviousWindowsFolderThatSurvivesIsNotCountedAsFreed()
    {
        Make("Windows.old/Windows/x.dll", 900);
        var rule = Rule("previous-windows", CleanupAction.PreviousWindows, ["%SYSTEMDRIVE%\\Windows.old"]);
        var report = await new SystemCleanJob(Context(), tools).RunAsync([rule], ["previous-windows"]);
        Assert.Equal(0, report.Result.FreedBytes);
        Assert.Contains(report.Notes, x => x.Contains("did not remove"));
        Assert.Equal(("Previous Installations", false), tools.Handlers.Last());
    }

    [Fact]
    public async Task HibernationIsTurnedOffWithPowercfgAndItsFileSizeIsReported()
    {
        Make("hiberfil.sys", 2048);
        var report = await new SystemCleanJob(Context(), tools).RunAsync([Rule("hibernation", CleanupAction.Hibernation)], ["hibernation"]);
        Assert.Equal("powercfg.exe /hibernate off", Assert.Single(tools.Commands));
        Assert.Equal(2048, report.Result.FreedBytes);
    }

    [Fact]
    public async Task SystemActionsAreMeasuredButNotRunByTheNormalCleaner()
    {
        Make("hiberfil.sys", 2048);
        var rules = new[] { Rule("hibernation", CleanupAction.Hibernation), Rule("component-store", CleanupAction.ComponentStore) };
        var findings = await new CleanupScanner(Context(admin: false)).ScanAsync(rules);
        Assert.Equal(2048, findings[0].Bytes);
        Assert.True(findings[1].Unmeasured && findings[1].HasContent);
        Assert.All(findings, x => Assert.True(x.NeedsAdmin));
        var result = await new CleanupCleaner(Context(admin: false)).CleanAsync(findings);
        Assert.Equal(0, result.Removed + result.Failed);
        Assert.Empty(tools.Commands);
    }

    [Fact]
    public async Task TheHelperAndTheAppTalkOverThePipeEndToEnd()
    {
        Make("Windows/SoftwareDistribution/Download/a.cab", 300);
        Make("Windows/SoftwareDistribution/Download/b.cab", 200);
        CleanupRule[] catalog = [new CleanupRule { Id = "update-downloads", Group = "Windows", Name = "Windows Update downloads", Admin = true, Paths = ["%WINDIR%\\SoftwareDistribution\\Download"] }];
        var seen = new List<CleanupProgress>();
        var launcher = new SystemCleanLauncher((pipe, ids) => Task.Run(async () =>
            Assert.Equal(0, await SystemCleanHost.RunAsync([SystemCleanHost.Command, pipe, string.Join(",", ids)], catalog, Context(), tools))));
        var report = await launcher.RunAsync(["update-downloads"], new Collect(seen));
        Assert.True(report.Result.FreedBytes > 0, string.Join(" / ", report.Notes));
        Assert.Equal(500, report.Result.FreedBytes);
        Assert.Equal(2, report.Result.Removed);
        Assert.Contains(seen, x => x.Name == "Windows Update downloads");
    }

    [Fact]
    public async Task ALongSystemStepIsAnnouncedBeforeItRuns()
    {
        Make("hiberfil.sys", 800);
        CleanupRule[] catalog = [new CleanupRule { Id = "hibernation", Group = "Windows", Name = "Hibernation file", Admin = true, Action = CleanupAction.Hibernation, Paths = ["%SYSTEMDRIVE%\\hiberfil.sys"] }];
        var seen = new List<CleanupProgress>();
        var launcher = new SystemCleanLauncher((pipe, ids) => Task.Run(async () =>
            Assert.Equal(0, await SystemCleanHost.RunAsync([SystemCleanHost.Command, pipe, string.Join(",", ids)], catalog, Context(), tools))));
        var report = await launcher.RunAsync(["hibernation"], new Collect(seen));
        Assert.Contains(seen, x => x is { Starting: true, Name: "Hibernation file" });
        Assert.NotEmpty(tools.Commands);
        Assert.DoesNotContain(report.Notes, x => x.Contains("could not"));
    }

    [Fact]
    public async Task ASlowPermissionPromptIsNotTreatedAsAFailure()
    {
        var launcher = new SystemCleanLauncher(async (pipe, ids) =>
        {
            await Task.Delay(1500);
            Assert.Equal(0, await SystemCleanHost.RunAsync([SystemCleanHost.Command, pipe, string.Join(",", ids)], [], Context(), tools));
        });
        var report = await launcher.RunAsync(["hibernation"]);
        Assert.DoesNotContain(report.Notes, x => x.Contains("start"));
    }

    [Fact]
    public async Task ADeclinedPermissionPromptIsAnAnswerNotAnError()
    {
        var launcher = new SystemCleanLauncher((_, _) => throw new OperationCanceledException());
        var report = await launcher.RunAsync(["hibernation"]);
        Assert.Contains("declined", report.Notes.Single());
        Assert.Equal(0, report.Result.FreedBytes);
        Assert.Equal(0, report.Result.Failed);
    }

    [Fact]
    public async Task AHelperThatExitsWithoutConnectingIsReportedAsFailed()
    {
        var launcher = new SystemCleanLauncher((_, _) => Task.CompletedTask);
        var report = await launcher.RunAsync(["hibernation"]);
        Assert.Equal(1, report.Result.Failed);
        Assert.NotEmpty(report.Notes);
    }

    [Fact]
    public async Task AHelperThatCannotBeStartedExplainsWhy()
    {
        var launcher = new SystemCleanLauncher((_, _) => throw new FileNotFoundException("The cleanup helper is missing from this installation."));
        var report = await launcher.RunAsync(["hibernation"]);
        Assert.Contains("missing", report.Notes.Single());
    }

    [Fact]
    public async Task TheHelperRefusesRequestsThatAreNotItsCommand()
    {
        Assert.Equal(64, await SystemCleanHost.RunAsync(["--format-disk", "x", "y"], [], Context(), tools));
        Assert.Equal(64, await SystemCleanHost.RunAsync([], [], Context(), tools));
    }

    private const string Analysis = """
        Deployment Image Servicing and Management tool
        Component Store (WinSxS) information:

        Windows Explorer Reported Size of Component Store : 12.37 GB

        Actual Size of Component Store : 11.89 GB

            Shared with Windows : 8.52 GB
            Backups and Disabled Features : 3.05 GB
            Cache and Temporary Data : 322.50 MB

        Date of Last Cleanup : 2026-10-01 10:00:00

        Number of Reclaimable Packages : 3
        Component Store Cleanup Recommended : Yes

        The operation completed successfully.
        """;

    [Fact]
    public void TheComponentStoreReportIsReadByPositionSoTranslatedLabelsStillWork()
    {
        Assert.Equal((long)(3.05 * 1073741824) + (long)(322.5 * 1048576), ComponentStoreAnalysis.Reclaimable(Analysis));
        Assert.Equal((long)(1.5 * 1073741824) + (long)(0.5 * 1073741824), ComponentStoreAnalysis.Reclaimable("a : 4 GB\nb : 3 GB\nc : 2 GB\nd : 1,5 GB\ne : 512 MB\n"));
        Assert.Null(ComponentStoreAnalysis.Reclaimable("The operation completed successfully."));
        Assert.Null(ComponentStoreAnalysis.Reclaimable(""));
    }

    private SystemCleanLauncher Helper(CleanupRule[] catalog, bool admin = true) => new((pipe, ids) => Task.Run(async () =>
        Assert.Equal(0, await SystemCleanHost.RunAsync([SystemCleanHost.Command, pipe, string.Join(",", ids)], catalog, Context(admin), tools))));

    [Fact]
    public async Task MeasuringReportsExactSizesAndCleansNothing()
    {
        var a = Make("Windows/SoftwareDistribution/Download/a.cab", 300);
        Make("Windows/SoftwareDistribution/Download/b.cab", 200);
        Make("hiberfil.sys", 800);
        tools.Output = Analysis;
        CleanupRule[] catalog =
        [
            new() { Id = "update-downloads", Group = "Windows", Name = "u", Admin = true, Paths = ["%WINDIR%\\SoftwareDistribution\\Download"] },
            new() { Id = "hibernation", Group = "Windows", Name = "h", Admin = true, Action = CleanupAction.Hibernation },
            new() { Id = "component-store", Group = "Windows", Name = "c", Admin = true, Action = CleanupAction.ComponentStore },
            new() { Id = "drivers", Group = "Windows", Name = "d", Admin = true, Action = CleanupAction.DiskCleanup, Handlers = ["Device Driver Packages"] }
        ];
        var report = await Helper(catalog).MeasureAsync(["update-downloads", "hibernation", "component-store", "drivers"]);
        Assert.Null(report.Error);
        Assert.Equal(500, report.Items.Single(x => x.Id == "update-downloads").Bytes);
        Assert.Equal(800, report.Items.Single(x => x.Id == "hibernation").Bytes);
        var store = report.Items.Single(x => x.Id == "component-store");
        Assert.False(store.Unmeasured);
        Assert.True(store.Bytes > 3L * 1073741824);
        Assert.True(report.Items.Single(x => x.Id == "drivers").Unmeasured);
        // Looking is not cleaning: nothing was deleted, no handler was armed, and only the read-only analysis ran.
        Assert.True(File.Exists(a));
        Assert.Equal(["Dism.exe /Online /Cleanup-Image /AnalyzeComponentStore"], tools.Commands);
        Assert.Empty(tools.Handlers);
    }

    [Fact]
    public async Task MeasuringOnlyLooksAtRulesMarkedForAdministrators()
    {
        Make("Users/me/AppData/Local/Temp/a.tmp", 100);
        CleanupRule[] catalog = [new() { Id = "user-temp", Group = "Windows", Name = "t", Admin = false, Paths = ["%LOCALAPPDATA%\\Temp"] }];
        var report = await Helper(catalog).MeasureAsync(["user-temp", "not-a-rule"]);
        Assert.Empty(report.Items);
        Assert.Null(report.Error);
    }

    [Fact]
    public async Task AComponentStoreThatCannotBeAnalysedIsLeftUnmeasuredNotZero()
    {
        tools.ExitCode = 740;
        CleanupRule[] catalog = [new() { Id = "component-store", Group = "Windows", Name = "c", Admin = true, Action = CleanupAction.ComponentStore }];
        var item = (await Helper(catalog).MeasureAsync(["component-store"])).Items.Single();
        Assert.True(item.Unmeasured);
        Assert.Contains("740", item.Note);
    }

    [Fact]
    public async Task MeasuringWithoutAdministratorRightsFailsInsteadOfGuessing()
    {
        CleanupRule[] catalog = [new() { Id = "hibernation", Group = "Windows", Name = "h", Admin = true, Action = CleanupAction.Hibernation }];
        var report = await Helper(catalog, admin: false).MeasureAsync(["hibernation"]);
        Assert.Empty(report.Items);
        Assert.Contains("Administrator rights", report.Error);
    }

    [Fact]
    public async Task ADeclinedPromptWhileMeasuringIsReportedAsDeclined()
    {
        var report = await new SystemCleanLauncher((_, _) => throw new OperationCanceledException()).MeasureAsync(["hibernation"]);
        Assert.True(report.Declined);
        Assert.Empty(report.Items);
    }

    [Fact]
    public async Task TheMeasureMarkerCanNeverSelectAnythingToClean()
    {
        var a = Make("Windows/SoftwareDistribution/Download/a.cab", 300);
        CleanupRule[] catalog = [new() { Id = "update-downloads", Group = "Windows", Name = "u", Admin = true, Paths = ["%WINDIR%\\SoftwareDistribution\\Download"] }];
        // Cleaning asks for the rule by id; the marker only ever switches the helper into its read-only mode.
        var report = await Helper(catalog).MeasureAsync(["update-downloads"]);
        Assert.Single(report.Items);
        Assert.True(File.Exists(a));
        Assert.DoesNotContain(catalog, rule => rule.Id == SystemCleanHost.MeasureMarker);
    }

    private sealed class Collect(List<CleanupProgress> list) : IProgress<CleanupProgress> { public void Report(CleanupProgress value) { lock (list) list.Add(value); } }
}
