using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

/// <summary>The Windows tab of Fixes: services that must really start, the new reports, and the rules that keep the new steps small.</summary>
public sealed class WindowsTabTests
{
    private static FixItem Item(FixScope scope, params FixStep[] steps) => new() { Id = "item", Name = "Item", Group = FixGroup.Windows, Scope = scope, Summary = "s", Effect = "e", Steps = steps };
    private static List<string> Check(FixScope scope, params FixStep[] steps) => FixGuard.Validate([Item(scope, steps)]);
    private static FixItem Fix(string id) => FixCatalog.Load().Single(x => x.Id == id);

    [Fact]
    public void TheWindowsTabNoLongerHidesAServiceThatCouldNotBeStartedBehindNetsExitCodeTwo()
    {
        // net.exe ends with 2 for "already started" and also for "turned off" or "broken", so accepting 2 made a dead service look like a step that worked.
        foreach (var id in new[] { "reset-windows-update", "sync-clock", "rebuild-search-index", "rebuild-font-cache" })
            Assert.DoesNotContain(Fix(id).Steps, x => x.File.Equals("net.exe", StringComparison.OrdinalIgnoreCase));
        foreach (var step in FixCatalog.Load().SelectMany(x => x.Steps).Where(x => x.Kind is FixStepKind.StartService or FixStepKind.StopService))
            Assert.DoesNotContain(2, step.Ok);
    }

    [Fact]
    public void ServicesAreOnlyControlledByNameOnTheGuardsListAndOnlyByWholePcFixes()
    {
        Assert.Empty(Check(FixScope.Machine, new FixStep { Title = "t", Kind = FixStepKind.StartService, Service = "wuauserv" }));
        Assert.Empty(Check(FixScope.Machine, new FixStep { Title = "t", Kind = FixStepKind.StopService, Service = "FontCache3.0.0.0" }));
        Assert.NotEmpty(Check(FixScope.Machine, new FixStep { Title = "t", Kind = FixStepKind.StopService, Service = "WinDefend" }));
        Assert.NotEmpty(Check(FixScope.Machine, new FixStep { Title = "t", Kind = FixStepKind.StartService, Service = "" }));
        Assert.NotEmpty(Check(FixScope.User, new FixStep { Title = "t", Kind = FixStepKind.StartService, Service = "wuauserv" }));
    }

    [Fact]
    public void AServiceStepIsAPowerShellRunThatNamesTheServiceAndReportsWhyItDidNotStart()
    {
        var context = new FixStepContext(Fix("reset-windows-update"), [], 0, new FixStep { Title = "Start Windows Update", Kind = FixStepKind.StartService, Service = "wuauserv", Minutes = 3 });
        var run = FixSupport.ServiceStep(context).Step;
        Assert.Equal("powershell.exe", run.File);
        Assert.Equal(FixEncoding.Utf8, run.Encoding);
        var script = run.Args[^1];
        Assert.Contains("Start-Service -Name 'wuauserv'", script);
        Assert.Contains("'Disabled'", script);
        Assert.Contains($"exit {FixSupport.ServiceDisabled}", script);
        // The same rules as every other PowerShell step: each cmdlet names its module, nothing forbidden.
        Assert.Empty(Check(FixScope.Machine, new FixStep { Title = "t", File = "powershell.exe", Args = run.Args }));

        var stop = FixSupport.ServiceStep(context with { Step = new FixStep { Title = "Stop", Kind = FixStepKind.StopService, Service = "bits" } }).Step.Args[^1];
        Assert.Contains("Stop-Service -Name 'bits' -Force", stop);
        Assert.Empty(Check(FixScope.Machine, new FixStep { Title = "t", File = "powershell.exe", Args = ["-NoProfile", "-NonInteractive", "-Command", stop] }));
    }

    [Fact]
    public void AServiceStepForAnUnlistedServiceIsRefusedBeforeAnythingRuns()
    {
        var context = new FixStepContext(Fix("reset-windows-update"), [], 0, new FixStep { Title = "t", Kind = FixStepKind.StopService, Service = "x'; Remove-Item C:\\ #" });
        Assert.Throws<InvalidOperationException>(() => FixSupport.ServiceStep(context));
    }

    [Fact]
    public async Task ADisabledServiceMakesTheStartFailInsteadOfPassing()
    {
        var item = Item(FixScope.Machine,
            new FixStep { Title = "Start Windows Update", Kind = FixStepKind.StartService, Service = "wuauserv", Always = true });
        var executor = new ScriptedExecutor { Codes = { ["Start Windows Update"] = FixSupport.ServiceDisabled }, Prints = { ["Start Windows Update"] = "Windows Update is turned off (its startup type is Disabled)." } };
        var result = await new FixRunner(executor).RunAsync(item, [], new Collector(), CancellationToken.None);
        Assert.Equal(ActionOutcome.Failed, result.Outcome);
        Assert.Contains("turned off", result.Detail);
    }

    [Fact]
    public void TheDeliveryOptimizationCacheIsClearedBeforeTheUpdateServicesAreStopped()
    {
        // Windows restarts the update services by itself within seconds, so the folders are renamed right after they stop, with nothing slow in between.
        var steps = Fix("reset-windows-update").Steps;
        var cache = Array.FindIndex(steps, x => x.Args.LastOrDefault()?.Contains("Delete-DeliveryOptimizationCache") == true);
        Assert.Equal(0, cache);
        Assert.True(cache < Array.FindIndex(steps, x => x.Kind == FixStepKind.StopService));
        Assert.True(Array.FindLastIndex(steps, x => x.Kind == FixStepKind.StopService) < Array.FindIndex(steps, x => x.Kind == FixStepKind.RenameFolder));
    }

    [Fact]
    public void AReadOnlyFolderLeftFromAnEarlierResetCanStillBeRemoved()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tidywindow-readonly-" + Guid.NewGuid().ToString("N"));
        var inner = Directory.CreateDirectory(Path.Combine(folder, "sub"));
        var file = Path.Combine(inner.FullName, "a.txt");
        File.WriteAllText(file, "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        inner.Attributes |= FileAttributes.System;
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => Directory.Delete(folder, recursive: true));
            FixSupport.ClearReadOnly(folder);
            Directory.Delete(folder, recursive: true);
            Assert.False(Directory.Exists(folder));
        }
        finally
        {
            if (Directory.Exists(folder)) { FixSupport.ClearReadOnly(folder); Directory.Delete(folder, recursive: true); }
        }
    }

    [Fact]
    public void SystemFileCheckerEndsByNamingTheFilesItRepairedOrCouldNotRepair()
    {
        var steps = Fix("repair-windows-files").Steps;
        var sfc = Array.FindIndex(steps, x => x.File == "sfc.exe");
        var list = steps[sfc + 1];
        Assert.Contains("CBS.log", list.Args[^1]);
        Assert.Contains("Cannot repair", list.Args[^1]);
        Assert.Contains("Repairing corrupted", list.Args[^1]);
        // Opened for reading with the share the servicing stack writes with, so a log that is in use can still be read; it only ever reads.
        Assert.Contains("'ReadWrite'", list.Args[^1]);
        Assert.Contains(1, list.Warn);
        Assert.True(list.Optional);
    }

    [Fact]
    public void TheDriveCheckReadsWearErrorsAndVolumeHealthBeforeTheScan()
    {
        var steps = Fix("check-disk").Steps;
        Assert.Contains(steps, x => x.Args.LastOrDefault()?.Contains("Storage\\Get-StorageReliabilityCounter") == true);
        Assert.Contains(steps, x => x.Args.LastOrDefault()?.Contains("Storage\\Get-Volume") == true);
        Assert.Contains("Repair-Volume", steps[^1].Args[^1]);
        // Wear and errors are information: a drive that does not report them must not stop the scan.
        Assert.All(steps.Take(steps.Length - 1), x => Assert.True(x.Optional, x.Title));
    }

    [Fact]
    public void TheClockCanResetItsServersButOnlyWhenAskedAndNeverOnADomain()
    {
        var item = Fix("sync-clock");
        Assert.Empty(FixPlan.DefaultOptions(item));
        Assert.DoesNotContain(FixPlan.Build(item, []), x => x.Args.LastOrDefault()?.Contains("manualpeerlist") == true);
        var reset = FixPlan.Build(item, ["servers"]).Single(x => x.Args.LastOrDefault()?.Contains("manualpeerlist") == true);
        Assert.Contains("PartOfDomain", reset.Args[^1]);
        Assert.Contains("time.windows.com", reset.Args[^1]);
        Assert.Equal(["/query", "/status"], item.Steps[^1].Args);
        Assert.Empty(Check(FixScope.Machine, new FixStep { Title = "t", File = "w32tm.exe", Args = ["/query", "/status"] }));
        Assert.NotEmpty(Check(FixScope.Machine, new FixStep { Title = "t", File = "w32tm.exe", Args = ["/config", "/manualpeerlist:evil.example"] }));
    }

    [Fact]
    public void SearchCanResetItsIndexedFoldersOnlyWhenAskedAndOnlyThroughTheOneAllowedValue()
    {
        var item = Fix("rebuild-search-index");
        Assert.Empty(FixPlan.DefaultOptions(item));
        Assert.DoesNotContain(FixPlan.Build(item, []), x => x.Kind == FixStepKind.SetRegistry);
        var plan = FixPlan.Build(item, ["locations"]);
        var set = Array.FindIndex(plan.ToArray(), x => x.Kind == FixStepKind.SetRegistry);
        Assert.InRange(set, plan.ToList().FindIndex(x => x.Kind == FixStepKind.StopService) + 1, plan.ToList().FindIndex(x => x.Kind == FixStepKind.DeleteFiles) - 1);
        Assert.Equal(@"Set HKLM\SOFTWARE\Microsoft\Windows Search\SetupCompletedSuccessfully to 0", FixPlan.Describe(plan[set]));

        Assert.True(FixGuard.AllowsRegistry(FixScope.Machine, @"HKLM\SOFTWARE\Microsoft\Windows Search", "SetupCompletedSuccessfully", 0));
        Assert.False(FixGuard.AllowsRegistry(FixScope.Machine, @"HKLM\SOFTWARE\Microsoft\Windows Search", "SetupCompletedSuccessfully", 1));
        Assert.False(FixGuard.AllowsRegistry(FixScope.Machine, @"HKLM\SOFTWARE\Microsoft\Windows Search", "Other", 0));
        Assert.False(FixGuard.AllowsRegistry(FixScope.User, @"HKLM\SOFTWARE\Microsoft\Windows Search", "SetupCompletedSuccessfully", 0));
        Assert.NotEmpty(Check(FixScope.Machine, new FixStep { Title = "t", Kind = FixStepKind.SetRegistry, Path = @"HKLM\SOFTWARE\Microsoft\Windows NT", Name = "x", Data = 0 }));
    }

    [Fact]
    public void DefenderSaysSoWhenAnotherAntivirusHasTakenOverAndWhenNothingWasFound()
    {
        var item = Fix("defender-quick-scan");
        Assert.Contains("Get-MpComputerStatus", item.Steps[0].Args[^1]);
        Assert.False(item.Steps[0].Optional);
        Assert.Contains("No threats were found.", item.Steps[^1].Args[^1]);
        Assert.Contains("Get-MpThreatDetection", item.Steps[^1].Args[^1]);
        // A quick scan unless the person asks for the full one, and never both.
        Assert.Contains("QuickScan", FixPlan.Build(item, []).Single(x => x.Title.Contains("scan", StringComparison.OrdinalIgnoreCase) && x.Args[^1].Contains("Start-MpScan")).Args[^1]);
        var full = FixPlan.Build(item, ["full"]);
        Assert.Single(full, x => x.Args[^1].Contains("Start-MpScan"));
        Assert.Contains("FullScan", full.Single(x => x.Args[^1].Contains("Start-MpScan")).Args[^1]);
    }

    [Fact]
    public void NewProgramsAreHeldToTheirOwnShortLists()
    {
        Assert.Empty(Check(FixScope.Machine, new FixStep { Title = "t", File = "lodctr.exe", Args = ["/R"] }));
        Assert.Empty(Check(FixScope.Machine, new FixStep { Title = "t", File = "lodctr32.exe", Args = ["/R"] }));
        Assert.NotEmpty(Check(FixScope.Machine, new FixStep { Title = "t", File = "lodctr.exe", Args = ["/U:Anything"] }));
        Assert.Empty(Check(FixScope.Machine, new FixStep { Title = "t", File = "winmgmt.exe", Args = ["/resyncperf"] }));
        Assert.Empty(Check(FixScope.Machine, new FixStep { Title = "t", File = "wmiadap.exe", Args = ["/f"] }));
        Assert.NotEmpty(Check(FixScope.Machine, new FixStep { Title = "t", File = "wmiadap.exe", Args = ["/D"] }));
        Assert.Empty(Check(FixScope.Machine, new FixStep { Title = "t", File = "powercfg.exe", Args = ["/requests"] }));
        Assert.Empty(Check(FixScope.Machine, new FixStep { Title = "t", File = "powercfg.exe", Args = ["/devicequery", "wake_armed"] }));
        Assert.NotEmpty(Check(FixScope.Machine, new FixStep { Title = "t", File = "powercfg.exe", Args = ["/delete", "381b4222-f694-41f0-9685-ff5bb260df2e"] }));
        Assert.NotEmpty(Check(FixScope.Machine, new FixStep { Title = "t", File = "powercfg.exe", Args = ["/devicedisablewake", "Mouse"] }));
    }

    [Fact]
    public void TheNewFixesAreSafeByDefault()
    {
        var counters = Fix("rebuild-performance-counters");
        Assert.Equal(FixGroup.Windows, counters.Group);
        Assert.Equal(["lodctr.exe", "lodctr32.exe", "winmgmt.exe", "wmiadap.exe", "powershell.exe"], counters.Steps.Select(x => x.File));

        // Nothing that deletes anything runs unless it is switched on.
        var sleep = Fix("fix-sleep");
        Assert.Empty(FixPlan.DefaultOptions(sleep));
        Assert.All(FixPlan.Build(sleep, []), x => Assert.DoesNotContain("-restoredefaultschemes", x.Args));
        Assert.Contains(FixPlan.Build(sleep, ["plans"]), x => x.Args.Contains("-restoredefaultschemes"));
        Assert.Equal(FixRisk.Careful, sleep.Risk);
        // The hibernation file is only rebuilt where hibernation is on, and is switched back on whatever happens to the first command.
        var hibernate = FixPlan.Build(sleep, ["hibernate"]).Single(x => x.Args.LastOrDefault()?.Contains("/hibernate off") == true).Args[^1];
        Assert.Contains("HibernateEnabled", hibernate);
        Assert.True(hibernate.IndexOf("/hibernate on", StringComparison.Ordinal) > hibernate.IndexOf("/hibernate off", StringComparison.Ordinal));
    }

    [Fact]
    public void NoPowerShellStepContainsATypographicQuote()
    {
        // PowerShell treats ‘ ’ “ ” as quotes, so an apostrophe typed as ’ in a sentence ends the string early. The first live run of the drive check hit exactly that.
        foreach (var (item, step) in FixCatalog.Load().SelectMany(x => x.Steps.Select(s => (x, s))).Where(x => x.s.File == "powershell.exe"))
            Assert.False(step.Args[^1].IndexOfAny(['‘', '’', '‚', '‛', '“', '”', '„']) >= 0, $"{item.Id}: {step.Title}");
    }

    [Fact]
    public async Task EveryPowerShellStepIsValidPowerShell()
    {
        if (!OperatingSystem.IsWindows()) return;
        var powershell = FixGuard.Resolve("powershell.exe");
        if (powershell is null || !File.Exists(powershell)) return;
        var context = new FixStepContext(Fix("reset-windows-update"), [], 0, new FixStep { Title = "t", Kind = FixStepKind.StartService, Service = "bits" });
        var scripts = FixCatalog.Load().SelectMany(x => x.Steps.Select(s => (Name: $"{x.Id}: {s.Title}", Script: s.File == "powershell.exe" ? s.Args[^1] : null)))
            .Where(x => x.Script is not null).Select(x => (x.Name, Script: x.Script!))
            .Append((Name: "start a service", Script: FixSupport.ServiceStep(context).Step.Args[^1]))
            .Append((Name: "stop a service", Script: FixSupport.ServiceStep(context with { Step = new FixStep { Title = "t", Kind = FixStepKind.StopService, Service = "bits" } }).Step.Args[^1])).ToList();
        var folder = Directory.CreateTempSubdirectory("tidywindow-parse-");
        try
        {
            for (var i = 0; i < scripts.Count; i++) File.WriteAllText(Path.Combine(folder.FullName, $"{i:000}.ps1"), scripts[i].Script, new System.Text.UTF8Encoding(true));
            var start = new System.Diagnostics.ProcessStartInfo(powershell) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("Get-ChildItem -LiteralPath '" + folder.FullName + "' -Filter *.ps1 | ForEach-Object { $t = $null; $e = $null; [void][System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$t, [ref]$e); foreach ($x in $e) { $_.BaseName + ': ' + $x.Message } }");
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            var problems = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => int.TryParse(x.AsSpan(0, Math.Min(3, x.Length)), out var n) && n < scripts.Count ? $"{scripts[n].Name} {x[3..]}" : x).ToList();
            Assert.Empty(problems);
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    public void ThePerformanceCounterToolsResolveToTheRightFolders()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "lodctr.exe"), FixGuard.Resolve("lodctr.exe"));
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", "lodctr.exe"), FixGuard.Resolve("lodctr32.exe"));
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wbem", "wmiadap.exe"), FixGuard.Resolve("wmiadap.exe"));
    }
}
