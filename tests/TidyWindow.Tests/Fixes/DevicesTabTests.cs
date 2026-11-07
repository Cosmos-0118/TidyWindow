using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

/// <summary>The Devices tab of Fixes: a stuck print service, the Bluetooth and camera services, and the rules that keep device restarts to a short list.</summary>
public sealed class DevicesTabTests
{
    private static FixItem Item(FixScope scope, params FixStep[] steps) => new() { Id = "item", Name = "Item", Group = FixGroup.Devices, Scope = scope, Summary = "s", Effect = "e", Steps = steps };
    private static List<string> Check(FixScope scope, params FixStep[] steps) => FixGuard.Validate([Item(scope, steps)]);
    private static FixItem Fix(string id) => FixCatalog.Load().Single(x => x.Id == id);
    private static string Script(FixStep step) => step.Args[^1];
    private static string[] Services(FixItem item, FixStepKind kind) => item.Steps.Where(x => x.Kind == kind).Select(x => x.Service).ToArray();

    [Fact]
    public void TheDevicesTabHasTenFixesAndNoneUsesNetExe()
    {
        var devices = FixCatalog.Load().Where(x => x.Group == FixGroup.Devices).ToList();
        Assert.Equal(
            ["find-problem-devices", "fix-printing", "fix-usb-dropouts", "rescan-devices", "reset-problem-devices", "restart-audio", "restart-bluetooth", "restart-camera", "restart-controllers", "restart-scanner"],
            devices.Select(x => x.Id).Order().ToArray());
        // net.exe ends with 2 both for "already in that state" and for "could not be controlled", which hid a service that never stopped.
        Assert.All(devices, x => Assert.DoesNotContain(x.Steps, s => s.File.Equals("net.exe", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void AStuckPrintServiceIsEndedBeforeTheQueueIsEmptied()
    {
        var steps = Fix("fix-printing").Steps;
        var stop = Array.FindIndex(steps, x => x.File == "powershell.exe");
        var delete = Array.FindIndex(steps, x => x.Kind == FixStepKind.DeleteFiles);
        var start = Array.FindIndex(steps, x => x.Kind == FixStepKind.StartService);
        Assert.True(stop == 0 && stop < delete && delete < start);
        var script = Script(steps[stop]);
        // The service is asked to stop first and given 15 seconds; only then is its process ended, so the queue files are never emptied under a running spoolsv.exe.
        Assert.Contains("Stop-Service -Name spooler -Force", script);
        Assert.Contains("Start-Job", script);
        // The stop request is a job because Stop-Service blocks for about a minute on a frozen service; the job is only removed after the process is gone, or removing it blocks too.
        Assert.True(script.LastIndexOf("Remove-Job", StringComparison.Ordinal) > script.IndexOf("Stop-Process", StringComparison.Ordinal));
        Assert.Contains("AddSeconds(15)", script);
        Assert.Contains("Stop-Process -Name spoolsv -Force", script);
        Assert.True(script.IndexOf("Stop-Service", StringComparison.Ordinal) < script.IndexOf("Stop-Process", StringComparison.Ordinal));
        // A PC without a print service (4) is fine; a process that cannot be ended is a failure, and nothing is deleted after it.
        Assert.Contains(4, steps[stop].Ok);
        Assert.False(steps[stop].Optional);
        Assert.True(steps[start].Always);
        Assert.Equal("spooler", steps[start].Service);
    }

    [Fact]
    public void OnlyThePrintFixMayEndAProcessAndOnlyTheSpooler()
    {
        var uses = FixCatalog.Load().Where(x => x.Group == FixGroup.Devices).SelectMany(x => x.Steps.Select(s => (Item: x.Id, Step: s))).Where(x => x.Step.File == "powershell.exe" && Script(x.Step).Contains("Stop-Process")).ToList();
        var only = Assert.Single(uses);
        Assert.Equal("fix-printing", only.Item);
        Assert.DoesNotContain("-Name *", Script(only.Step));
        Assert.DoesNotContain("Stop-Process -Id", Script(only.Step));
        Assert.DoesNotContain("Get-Process |", Script(only.Step));
    }

    [Fact]
    public void TheProblemReportUsesWindowsOwnWordsAndNamesTheDevice()
    {
        // ConfigManagerErrorCode is an enum (CM_PROB_DISABLED) in Get-PnpDevice, not a number and not a sentence; ProblemDescription is Device Manager's own text.
        foreach (var script in new[] { Script(Fix("find-problem-devices").Steps[0]), Script(Fix("rescan-devices").Steps[^1]), Script(Fix("reset-problem-devices").Steps[^1]) })
        {
            Assert.Contains("ProblemDescription", script);
            Assert.Contains("InstanceId", script);
            Assert.DoesNotContain("\"$($_.ConfigManagerErrorCode)\"", script);
        }
        var find = Fix("find-problem-devices");
        Assert.Equal(FixScope.User, find.Scope);
        Assert.Contains(1, find.Steps[0].Warn);
    }

    [Fact]
    public void TheRescanEndsByReportingTheDevicesThatFailed()
    {
        var steps = Fix("rescan-devices").Steps;
        Assert.Equal("pnputil.exe", steps[0].File);
        Assert.Contains("Get-PnpDevice -PresentOnly", Script(steps[1]));
        Assert.True(steps[1].Optional);
        Assert.Contains(1, steps[1].Warn);
        Assert.Contains("Start-Sleep", Script(steps[1]));
    }

    [Fact]
    public void RestartingDevicesIsLimitedToAShortListOfKindsAndCodes()
    {
        var item = Fix("reset-problem-devices");
        Assert.Equal(FixRisk.Careful, item.Risk);
        var restart = FixPlan.Build(item, []).Single(x => x.Title.StartsWith("Restart devices", StringComparison.Ordinal));
        var script = Script(restart);
        foreach (var kind in new[] { "'USB'", "'Bluetooth'", "'Camera'", "'MEDIA'", "'HIDClass'" }) Assert.Contains(kind, script);
        // Display, disk, storage, system, processor and network devices are never switched off here: a wrong one blanks the screen or drops the drive Windows runs from.
        foreach (var never in new[] { "'Display'", "'DiskDrive'", "'SCSIAdapter'", "'HDC'", "'System'", "'Processor'", "'Volume'", "'Net'", "'Computer'" }) Assert.DoesNotContain(never, script);
        Assert.Contains("$codes = 10, 31, 39, 41, 43", script);
        // A device that was switched off on purpose is left as it is unless the option is ticked.
        Assert.Contains("$code -eq 22", script);
        Assert.DoesNotContain(FixPlan.Build(item, []), x => x.Args.LastOrDefault()?.Contains("Enable-PnpDevice") == true && x.Title.StartsWith("Switch on", StringComparison.Ordinal));
        Assert.Contains(FixPlan.Build(item, ["disabled"]), x => x.Title.StartsWith("Switch on", StringComparison.Ordinal));
        Assert.DoesNotContain("disabled", FixPlan.DefaultOptions(item));
    }

    [Fact]
    public void ADeviceThatIsSwitchedOffForARestartIsAlwaysSwitchedBackOn()
    {
        // The enable is in a finally block, and the step runs to the end once started, so stopping the fix can never leave a device off.
        foreach (var id in new[] { "reset-problem-devices", "fix-usb-dropouts", "restart-bluetooth", "restart-camera" })
            foreach (var step in Fix(id).Steps.Where(x => x.File == "powershell.exe" && Script(x).Contains("Disable-PnpDevice")))
            {
                var script = Script(step);
                Assert.True(step.Uninterruptible, $"{id}: {step.Title}");
                Assert.True(script.IndexOf("finally", StringComparison.Ordinal) > script.IndexOf("Disable-PnpDevice", StringComparison.Ordinal), $"{id}: {step.Title}");
                Assert.Contains("Enable-PnpDevice", script[script.IndexOf("finally", StringComparison.Ordinal)..]);
            }
    }

    [Fact]
    public void OnlyTheDeviceRestartingFixesMayDisableADevice()
    {
        var users = FixCatalog.Load().Where(x => x.Steps.Any(s => s.File == "powershell.exe" && Script(s).Contains("Disable-PnpDevice"))).Select(x => x.Id).Order().ToArray();
        Assert.Equal(["fix-usb-dropouts", "reset-problem-devices", "restart-bluetooth", "restart-camera"], users);
        // And none of them can do it as the current user.
        Assert.All(users, id => Assert.Equal(FixScope.Machine, Fix(id).Scope));
    }

    [Fact]
    public void BluetoothRestartsTheThreeServicesAndStartsThemInTheOppositeOrder()
    {
        var item = Fix("restart-bluetooth");
        Assert.Equal(["BTAGService", "BthAvctpSvc", "bthserv"], Services(item, FixStepKind.StopService));
        Assert.Equal(["bthserv", "BthAvctpSvc", "BTAGService"], Services(item, FixStepKind.StartService));
        Assert.All(item.Steps.Where(x => x.Kind == FixStepKind.StartService), x => Assert.True(x.Always, x.Title));
        // The Quiet page can turn the two audio services off; that is a warning, while the main service turned off is a failure.
        Assert.All(item.Steps.Where(x => x.Service is "BthAvctpSvc" or "BTAGService" && x.Kind == FixStepKind.StartService), x => Assert.Contains(FixSupport.ServiceDisabled, x.Warn));
        Assert.DoesNotContain(FixSupport.ServiceDisabled, item.Steps.Single(x => x.Kind == FixStepKind.StartService && x.Service == "bthserv").Warn);
        Assert.Contains(item.Steps, x => x.File == "pnputil.exe");
    }

    [Fact]
    public void TheBluetoothAdapterIsTurnedOffAndOnOnlyWhenAskedAndNeverAPairedDevice()
    {
        var item = Fix("restart-bluetooth");
        Assert.Empty(FixPlan.DefaultOptions(item));
        Assert.DoesNotContain(FixPlan.Build(item, []), x => x.File == "powershell.exe");
        var script = Script(FixPlan.Build(item, ["adapter"]).Single(x => x.File == "powershell.exe"));
        // BTH* instances are the paired devices (BTHENUM, BTHLE); only the radio itself is restarted.
        Assert.Contains("-notlike 'BTH*'", script);
        Assert.Contains("$_.Class -eq 'Bluetooth'", script);
        var steps = FixPlan.Build(item, ["adapter"]);
        Assert.True(Array.FindIndex(steps.ToArray(), x => x.File == "powershell.exe") < Array.FindIndex(steps.ToArray(), x => x.Kind == FixStepKind.StartService));
    }

    [Fact]
    public void RestartingSoundWithBluetoothAlsoCoversTheHeadsetServicesAndRescansTheAudioDevices()
    {
        var item = Fix("restart-audio");
        var plain = FixPlan.Build(item, []);
        Assert.DoesNotContain(plain, x => x.Service is "BTAGService" or "BthAvctpSvc" or "bthserv");
        Assert.Equal(["audiosrv", "AudioEndpointBuilder"], plain.Where(x => x.Kind == FixStepKind.StopService).Select(x => x.Service).ToArray());
        Assert.Equal(["AudioEndpointBuilder", "audiosrv"], plain.Where(x => x.Kind == FixStepKind.StartService).Select(x => x.Service).ToArray());
        var bluetooth = FixPlan.Build(item, ["bluetooth"]);
        Assert.Contains(bluetooth, x => x.Service == "BTAGService" && x.Kind == FixStepKind.StopService);
        Assert.Contains(bluetooth, x => x.Service == "BthAvctpSvc" && x.Kind == FixStepKind.StartService);
        Assert.Contains(plain, x => x.File == "pnputil.exe" && x.Optional);
        Assert.Equal("pnputil.exe", plain[^1].File);
    }

    [Fact]
    public void TheCameraFixRestartsTheMonitorToo()
    {
        var item = Fix("restart-camera");
        Assert.Equal(["FrameServerMonitor", "FrameServer"], Services(item, FixStepKind.StopService));
        Assert.Equal(["FrameServer", "FrameServerMonitor"], Services(item, FixStepKind.StartService));
        var script = Script(FixPlan.Build(item, ["device"]).Single(x => x.File == "powershell.exe"));
        Assert.Contains("$_.Class -eq 'Camera' -or $_.Class -eq 'Image'", script);
        Assert.Empty(FixPlan.DefaultOptions(item));
        Assert.DoesNotContain(FixPlan.Build(item, []), x => x.File == "powershell.exe");
    }

    [Fact]
    public void UsbPowerSavingIsOnlyChangedForThePlanInUseAndOnlyToOff()
    {
        var usb = new[] { "2a737441-1930-4402-8d77-b2bebba308a3", "48e6b7a6-50f5-4782-a5d4-53bb8f07e226" };
        Assert.Empty(Check(FixScope.Machine, new FixStep { Title = "t", File = "powercfg.exe", Args = ["/setacvalueindex", "SCHEME_CURRENT", usb[0], usb[1], "0"] }));
        Assert.Empty(Check(FixScope.Machine, new FixStep { Title = "t", File = "powercfg.exe", Args = ["/setdcvalueindex", "SCHEME_CURRENT", usb[0], usb[1], "0"] }));
        Assert.Empty(Check(FixScope.Machine, new FixStep { Title = "t", File = "powercfg.exe", Args = ["/setactive", "SCHEME_CURRENT"] }));
        // Another plan, another setting, or turning it ON by this route is refused; so is deleting a plan.
        Assert.NotEmpty(Check(FixScope.Machine, new FixStep { Title = "t", File = "powercfg.exe", Args = ["/setacvalueindex", "SCHEME_CURRENT", usb[0], usb[1], "1"] }));
        Assert.NotEmpty(Check(FixScope.Machine, new FixStep { Title = "t", File = "powercfg.exe", Args = ["/setacvalueindex", "0c54ec3e-a3d7-4df1-826a-f08af5c6c45b", usb[0], usb[1], "0"] }));
        Assert.NotEmpty(Check(FixScope.Machine, new FixStep { Title = "t", File = "powercfg.exe", Args = ["/setacvalueindex", "SCHEME_CURRENT", "238c9fa8-0aad-41ed-83f4-97be242c8f20", "29f6c1db-86da-48c5-9fdb-f2b67b1f44da", "0"] }));
        Assert.NotEmpty(Check(FixScope.Machine, new FixStep { Title = "t", File = "powercfg.exe", Args = ["/delete", "SCHEME_CURRENT"] }));
    }

    [Fact]
    public void TheUsbFixShowsTheSettingAndRestartsOnlyUsbDevices()
    {
        var item = Fix("fix-usb-dropouts");
        Assert.Equal(["suspend", "controllers"], FixPlan.DefaultOptions(item));
        var steps = FixPlan.Build(item, ["suspend"]);
        Assert.Equal(["/query", "/setacvalueindex", "/setdcvalueindex", "/setactive"], steps.Where(x => x.File == "powercfg.exe").Select(x => x.Args[0]).ToArray());
        Assert.DoesNotContain(steps, x => x.File == "powershell.exe" && Script(x).Contains("Disable-PnpDevice"));
        var script = Script(FixPlan.Build(item, ["controllers"]).Single(x => x.Title.StartsWith("Restart USB", StringComparison.Ordinal)));
        Assert.Contains("$classes = 'USB'", script);
        Assert.DoesNotContain("'HIDClass'", script);
        Assert.Equal(FixScope.Machine, item.Scope);
    }

    [Fact]
    public void TheScannerAndControllerFixesRestartTheirServicesAndRescan()
    {
        var scanner = Fix("restart-scanner");
        Assert.Equal(["stisvc", "ShellHWDetection"], Services(scanner, FixStepKind.StopService));
        Assert.Equal(["ShellHWDetection", "stisvc"], Services(scanner, FixStepKind.StartService));
        var controllers = Fix("restart-controllers");
        Assert.Equal(["GameInputSvc", "hidserv"], Services(controllers, FixStepKind.StopService));
        Assert.Equal(["hidserv", "GameInputSvc"], Services(controllers, FixStepKind.StartService));
        foreach (var item in new[] { scanner, controllers })
        {
            Assert.All(item.Steps.Where(x => x.Kind == FixStepKind.StartService), x => Assert.True(x.Always, x.Title));
            Assert.Equal("pnputil.exe", item.Steps[^1].File);
            // This PC may not have the service (Game Input is not on every build): that is fine, but a turned-off one is only a warning for the optional one.
            Assert.All(item.Steps.Where(x => x.Kind is FixStepKind.StartService or FixStepKind.StopService), x => Assert.Contains(FixSupport.ServiceMissing, x.Ok));
        }
    }

    [Fact]
    public void NewDeviceServicesAreOnTheListAndOthersStillAreNot()
    {
        foreach (var name in new[] { "BTAGService", "BthAvctpSvc", "FrameServerMonitor", "stisvc", "ShellHWDetection", "GameInputSvc", "hidserv" }) Assert.True(FixGuard.AllowsService(name), name);
        // Services other things depend on and that a user fix must not stop.
        foreach (var name in new[] { "BluetoothUserService", "WiaRpc", "PlugPlay", "DeviceInstall", "netprofm" }) Assert.False(FixGuard.AllowsService(name), name);
    }

    [Fact]
    public void StartingAServiceAlsoBringsBackTheAutomaticServicesThatStoppedWithIt()
    {
        // Stopping Windows Audio stops the Realtek audio service that depends on it; starting Windows Audio does not start that again.
        var context = new FixStepContext(Fix("restart-audio"), [], 0, new FixStep { Title = "t", Kind = FixStepKind.StartService, Service = "audiosrv" });
        var script = FixSupport.ServiceStep(context).Step.Args[^1];
        Assert.Contains("DependentServices", script);
        Assert.Contains("$dependent.StartType -eq 'Automatic'", script);
        Assert.Contains("$dependent.Status -eq 'Stopped'", script);
        // The dependents run after the service is running, also when it was running already, and a dependent that fails never fails the step.
        Assert.True(script.IndexOf("DependentServices", StringComparison.Ordinal) > script.IndexOf("is already running", StringComparison.Ordinal));
        Assert.Empty(Check(FixScope.Machine, new FixStep { Title = "t", File = "powershell.exe", Args = ["-NoProfile", "-NonInteractive", "-Command", script] }));
    }
}
