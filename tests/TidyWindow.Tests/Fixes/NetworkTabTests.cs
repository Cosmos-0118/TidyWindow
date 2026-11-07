using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

/// <summary>The Network tab of Fixes: the order of the refresh, what the new repairs may touch, and the rules that keep them small.</summary>
public sealed class NetworkTabTests
{
    private static FixItem Item(FixScope scope, params FixStep[] steps) => new() { Id = "item", Name = "Item", Group = FixGroup.Network, Scope = scope, Summary = "s", Effect = "e", Steps = steps };
    private static List<string> Check(FixScope scope, params FixStep[] steps) => FixGuard.Validate([Item(scope, steps)]);
    private static FixItem Fix(string id) => FixCatalog.Load().Single(x => x.Id == id);
    private static string Script(FixStep step) => step.Args[^1];
    private const string InternetSettings = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    [Fact]
    public void TheNetworkTabHasSevenFixes()
    {
        Assert.Equal(
            ["clear-network-shares", "clear-proxy", "fix-network-discovery", "refresh-network", "repair-tcp-settings", "reset-network", "restart-wifi-services"],
            FixCatalog.Load().Where(x => x.Group == FixGroup.Network).Select(x => x.Id).Order().ToArray());
    }

    [Fact]
    public void TheAdaptersAreRestartedBeforeTheNewIpAddressIsAskedFor()
    {
        // Restarting an adapter drops the link and the lease, so doing it after /renew threw the new address away and returned before Wi-Fi was back.
        var steps = Fix("refresh-network").Steps;
        var restart = Array.FindIndex(steps, x => x.When == "adapters");
        Assert.True(restart >= 0);
        Assert.True(restart < Array.FindIndex(steps, x => x.Args.SequenceEqual(["/release"])));
        Assert.True(restart < Array.FindIndex(steps, x => x.Args.SequenceEqual(["/renew"])));
        Assert.True(steps.Single(x => x.Args.SequenceEqual(["/renew"])).Always, "a stopped fix must not leave the PC without an address");
    }

    [Fact]
    public void AnAdapterThatIsStuckDisconnectedIsRestartedToo()
    {
        var script = Script(Fix("refresh-network").Steps.Single(x => x.When == "adapters"));
        Assert.Contains("-Physical", script);
        Assert.Contains("'Disconnected'", script);
        // A switched-off adapter is not switched on by a refresh, and one that fails does not hide the others.
        Assert.DoesNotContain("'Disabled'", script);
        Assert.Contains("catch", script);
        Assert.Contains("did not reconnect", script);
    }

    [Fact]
    public void TheRefreshEndsByCheckingThatTheRouterAnsweredAndIgnoresVirtualAdapters()
    {
        var last = Fix("refresh-network").Steps[^1];
        var script = Script(last);
        // Tailscale, WSL and an unplugged Ethernet port all hold 169.254 addresses on a healthy PC; only physical adapters that are up count.
        Assert.Contains("-Physical", script);
        Assert.Contains("$_.Status -eq 'Up'", script);
        Assert.Contains("169.254.", script);
        Assert.Contains(1, last.Warn);
        Assert.True(last.Optional);
    }

    [Fact]
    public void TheRefreshAlsoClearsTheIpv6AndNetBiosCaches()
    {
        var steps = Fix("refresh-network").Steps;
        Assert.Contains(steps, x => x.File == "netsh.exe" && x.Args.SequenceEqual(["interface", "ipv6", "delete", "neighbors"]));
        Assert.Contains(steps, x => x.File == "nbtstat.exe" && x.Args.SequenceEqual(["-R"]));
    }

    [Fact]
    public void NbtstatMayOnlyPurgeTheNameCache()
    {
        Assert.Empty(Check(FixScope.Machine, new FixStep { Title = "t", File = "nbtstat.exe", Args = ["-R"] }));
        // -RR sends release packets to a WINS server, which is not this fix's business.
        Assert.NotEmpty(Check(FixScope.Machine, new FixStep { Title = "t", File = "nbtstat.exe", Args = ["-RR"] }));
        Assert.NotEmpty(Check(FixScope.Machine, new FixStep { Title = "t", File = "nbtstat.exe", Args = ["-c"] }));
    }

    [Fact]
    public void TheProxyFixTurnsOffOnlyTheThreeUserValuesAndIsAFixForTheCurrentUser()
    {
        var item = Fix("clear-proxy");
        Assert.Equal(FixScope.User, item.Scope);
        Assert.Equal(FixRisk.Safe, item.Risk);
        var changes = item.Steps.Where(x => x.Kind is FixStepKind.SetRegistry or FixStepKind.DeleteRegistry).ToList();
        Assert.Equal([("ProxyEnable", FixStepKind.SetRegistry), ("ProxyServer", FixStepKind.DeleteRegistry), ("AutoConfigURL", FixStepKind.DeleteRegistry)], changes.Select(x => (x.Name, x.Kind)).ToArray());
        Assert.All(changes, x => Assert.Equal(InternetSettings, x.Path));
        Assert.Equal(0, changes[0].Data);
        // A user fix runs inside the app, never in the administrator helper.
        Assert.DoesNotContain(item.Steps, x => x.File is "net.exe" or "netsh.exe");
    }

    [Fact]
    public void TheRegistryRulesAllowOnlyTheProxyValuesForTheCurrentUser()
    {
        Assert.Empty(Check(FixScope.User, new FixStep { Title = "t", Kind = FixStepKind.SetRegistry, Path = InternetSettings, Name = "ProxyEnable", Data = 0 }));
        Assert.Empty(Check(FixScope.User, new FixStep { Title = "t", Kind = FixStepKind.DeleteRegistry, Path = InternetSettings, Name = "ProxyServer" }));
        Assert.Empty(Check(FixScope.User, new FixStep { Title = "t", Kind = FixStepKind.DeleteRegistry, Path = InternetSettings, Name = "AutoConfigURL" }));
        // Turning a proxy ON, removing anything else, another key, or doing it as the whole-PC helper are all refused.
        Assert.NotEmpty(Check(FixScope.User, new FixStep { Title = "t", Kind = FixStepKind.SetRegistry, Path = InternetSettings, Name = "ProxyEnable", Data = 1 }));
        Assert.NotEmpty(Check(FixScope.User, new FixStep { Title = "t", Kind = FixStepKind.DeleteRegistry, Path = InternetSettings, Name = "ProxyOverride" }));
        Assert.NotEmpty(Check(FixScope.User, new FixStep { Title = "t", Kind = FixStepKind.DeleteRegistry, Path = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", Name = "ProxyServer" }));
        Assert.NotEmpty(Check(FixScope.Machine, new FixStep { Title = "t", Kind = FixStepKind.DeleteRegistry, Path = InternetSettings, Name = "ProxyServer" }));
        Assert.NotEmpty(Check(FixScope.Machine, new FixStep { Title = "t", Kind = FixStepKind.SetRegistry, Path = InternetSettings, Name = "ProxyEnable", Data = 0 }));
        Assert.Equal($@"Remove {InternetSettings}\ProxyServer", FixPlan.Describe(new FixStep { Title = "t", Kind = FixStepKind.DeleteRegistry, Path = InternetSettings, Name = "ProxyServer" }));
    }

    [Fact]
    public void TheProxyFixShowsWhatItIsAboutToRemoveAndChecksTheResult()
    {
        var steps = Fix("clear-proxy").Steps;
        var show = Array.FindIndex(steps, x => x.File == "powershell.exe");
        Assert.True(show < Array.FindIndex(steps, x => x.Kind == FixStepKind.SetRegistry));
        Assert.Contains("ProxyServer", Script(steps[show]));
        Assert.Contains("AutoConfigURL", Script(steps[show]));
        // Tools such as git and npm read these variables, not the Windows setting. They are named, never changed.
        Assert.Contains("HTTPS_PROXY", Script(steps[show]));
        var check = steps[^1];
        Assert.Contains("GetSystemWebProxy", Script(check));
        Assert.Contains(1, check.Warn);
    }

    [Fact]
    public void TheNetworkStackResetPointsToTheOtherProxy()
    {
        var option = Fix("reset-network").Options.Single(x => x.Id == "proxy");
        Assert.Contains("Windows Update", option.Detail);
        Assert.Contains("stuck proxy", option.Detail);
    }

    [Fact]
    public void DiscoveryRestartsAllFourServicesAndStartsThemInDependencyOrder()
    {
        var steps = Fix("fix-network-discovery").Steps;
        string[] Names(FixStepKind kind) => steps.Where(x => x.Kind == kind).Select(x => x.Service).ToArray();
        // Stopping the provider host stops the publication service that depends on it, so the dependents go first and start last.
        Assert.Equal(["upnphost", "SSDPSRV", "FDResPub", "fdPHost"], Names(FixStepKind.StopService));
        Assert.Equal(["fdPHost", "FDResPub", "SSDPSRV", "upnphost"], Names(FixStepKind.StartService));
        Assert.All(steps.Where(x => x.Kind == FixStepKind.StartService), x => Assert.True(x.Always, x.Title));
        // The two device services are an option, and a Disabled one is a warning, not a failure of the whole fix.
        foreach (var step in steps.Where(x => x.Service is "SSDPSRV" or "upnphost")) Assert.Equal("devices", step.When);
        Assert.All(steps.Where(x => x.Kind == FixStepKind.StartService && x.Service is "SSDPSRV" or "upnphost"), x => Assert.Contains(FixSupport.ServiceDisabled, x.Warn));
        Assert.All(steps.Where(x => x.Kind == FixStepKind.StartService && x.Service is "fdPHost" or "FDResPub"), x => Assert.DoesNotContain(FixSupport.ServiceDisabled, x.Warn));
    }

    [Fact]
    public void DiscoveryChecksTheNetworkTypeFirstAndOnlyChangesItWhenAsked()
    {
        var item = Fix("fix-network-discovery");
        Assert.DoesNotContain("private", FixPlan.DefaultOptions(item));
        Assert.DoesNotContain("enable", FixPlan.DefaultOptions(item));
        Assert.Contains("devices", FixPlan.DefaultOptions(item));
        var withoutOptions = FixPlan.Build(item, FixPlan.DefaultOptions(item));
        Assert.DoesNotContain(withoutOptions, x => x.File == "powershell.exe" && Script(x).Contains("Set-NetConnectionProfile"));
        var check = withoutOptions.First(x => x.File == "powershell.exe");
        Assert.Contains("Get-NetConnectionProfile", Script(check));
        Assert.Contains("'Public'", Script(check));
        Assert.Contains(1, check.Warn);
        // The change is the only place a network is made Private, and it only touches networks that are Public.
        var change = FixPlan.Build(item, ["private"]).Single(x => x.File == "powershell.exe" && Script(x).Contains("Set-NetConnectionProfile"));
        Assert.Contains("-NetworkCategory Private", Script(change));
        Assert.Contains("-eq 'Public'", Script(change));
    }

    [Fact]
    public void OnlyTheDiscoveryFixMayChangeAStartupTypeAndOnlyToManualWhenAsked()
    {
        var steps = FixCatalog.Load().SelectMany(x => x.Steps.Select(s => (Item: x.Id, Step: s))).Where(x => x.Step.File == "powershell.exe" && Script(x.Step).Contains("Set-Service")).ToList();
        var only = Assert.Single(steps);
        Assert.Equal("fix-network-discovery", only.Item);
        Assert.Equal("enable", only.Step.When);
        Assert.Contains("-StartupType Manual", Script(only.Step));
        Assert.DoesNotContain("Automatic", Script(only.Step));
        Assert.Contains("'Disabled'", Script(only.Step));
        // Only the four discovery services are named, by a fixed list.
        Assert.Contains("'fdPHost', 'FDResPub', 'SSDPSRV', 'upnphost'", Script(only.Step));
    }

    [Fact]
    public void NoFixChangesTheFirewall()
    {
        foreach (var cmdlet in new[] { "Set-NetFirewallRule", "New-NetFirewallRule", "Remove-NetFirewallRule", "Enable-NetFirewallRule", "Disable-NetFirewallRule", "Set-NetFirewallProfile" })
            Assert.NotEmpty(Check(FixScope.Machine, new FixStep { Title = "t", File = "powershell.exe", Args = ["-NoProfile", "-NonInteractive", "-Command", $"NetSecurity\\{cmdlet} -Name x"] }));
        Assert.Empty(Check(FixScope.Machine, new FixStep { Title = "t", File = "powershell.exe", Args = ["-NoProfile", "-NonInteractive", "-Command", "NetSecurity\\Get-NetFirewallRule -Group 'x'"] }));
        // The reset command that wipes every firewall rule is not on the list either.
        Assert.NotEmpty(Check(FixScope.Machine, new FixStep { Title = "t", File = "netsh.exe", Args = ["advfirewall", "reset"] }));
    }

    [Fact]
    public void TheWifiFixRestartsTheTwoServicesThatNothingElseDependsOn()
    {
        var steps = Fix("restart-wifi-services").Steps;
        Assert.Equal(["WlanSvc", "NlaSvc"], steps.Where(x => x.Kind == FixStepKind.StopService).Select(x => x.Service).ToArray());
        Assert.Equal(["NlaSvc", "WlanSvc"], steps.Where(x => x.Kind == FixStepKind.StartService).Select(x => x.Service).ToArray());
        Assert.All(steps.Where(x => x.Kind == FixStepKind.StartService), x => Assert.True(x.Always, x.Title));
        // The Network List Service is not restarted: Tailscale and other VPN services depend on it, and stopping it would stop them with nothing to start them again.
        Assert.False(FixGuard.AllowsService("netprofm"));
        Assert.DoesNotContain(steps, x => x.Service == "netprofm");
        Assert.Contains("Internet", Script(steps.First(x => x.File == "powershell.exe")));
    }

    [Fact]
    public void TheSharesFixDisconnectsWithoutForgettingMappedDrives()
    {
        var item = Fix("clear-network-shares");
        Assert.Equal(FixScope.User, item.Scope);
        var script = Script(Assert.Single(item.Steps));
        Assert.Contains("SmbShare\\Remove-SmbMapping", script);
        Assert.Contains("SmbShare\\Get-SmbMapping", script);
        // -UpdateProfile would remove the saved mapping, which is what net use /delete does and this must not.
        Assert.DoesNotContain("UpdateProfile", script);
        Assert.DoesNotContain("net use", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cmdkey", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheDownloadSpeedFixSetsOnlyTheThreeDefaultsAndShowsThemBeforeAndAfter()
    {
        var item = Fix("repair-tcp-settings");
        Assert.Equal(FixScope.Machine, item.Scope);
        Assert.Equal(FixRisk.Safe, item.Risk);
        Assert.Equal(FixRestart.Never, item.Restart);
        var sets = item.Steps.Where(x => x.Args.Contains("set")).Select(x => string.Join(' ', x.Args)).ToArray();
        Assert.Equal(["int tcp set global autotuninglevel=normal", "int tcp set heuristics disabled", "int tcp set global rss=enabled"], sets);
        Assert.Equal(["show", "global"], item.Steps[0].Args[2..]);
        Assert.Equal(["show", "global"], item.Steps[^1].Args[2..]);
        // The slow settings are refused, so a typo in the catalog cannot start throttling a connection.
        foreach (var bad in new[] { "autotuninglevel=disabled", "autotuninglevel=restricted", "rss=disabled", "rsc=disabled", "chimney=enabled" })
            Assert.NotEmpty(Check(FixScope.Machine, new FixStep { Title = "t", File = "netsh.exe", Args = ["int", "tcp", "set", "global", bad] }));
        Assert.NotEmpty(Check(FixScope.Machine, new FixStep { Title = "t", File = "netsh.exe", Args = ["int", "tcp", "set", "heuristics", "enabled"] }));
    }

    [Fact]
    public void TheNewNetworkReportsAreReadOnly()
    {
        foreach (var args in new[] { new[] { "wlan", "show", "interfaces" }, ["int", "tcp", "show", "global"], ["int", "tcp", "show", "heuristics"] })
            Assert.Empty(Check(FixScope.Machine, new FixStep { Title = "t", File = "netsh.exe", Args = args }));
        Assert.NotEmpty(Check(FixScope.Machine, new FixStep { Title = "t", File = "netsh.exe", Args = ["wlan", "delete", "profile", "name=*"] }));
        Assert.NotEmpty(Check(FixScope.Machine, new FixStep { Title = "t", File = "netsh.exe", Args = ["wlan", "show", "profiles", "key=clear"] }));
    }

    [Fact]
    public void EveryNetworkFixThatNeedsAdministratorRightsIsAWholePcFix()
    {
        foreach (var id in new[] { "refresh-network", "reset-network", "fix-network-discovery", "restart-wifi-services", "repair-tcp-settings" })
            Assert.Equal(FixScope.Machine, Fix(id).Scope);
        foreach (var id in new[] { "clear-proxy", "clear-network-shares" })
            Assert.Equal(FixScope.User, Fix(id).Scope);
    }
}
