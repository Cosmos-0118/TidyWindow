using System.Text.RegularExpressions;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

/// <summary>The Apps tab of Fixes: registering Windows' own apps again, including the ones an account has lost, without ever removing or resetting an app.</summary>
public sealed class AppsTabTests
{
    private static FixItem Fix(string id) => FixCatalog.Load().Single(x => x.Id == id);
    private static string Script(FixStep step) => step.Args[^1];
    private static List<FixItem> Apps() => FixCatalog.Load().Where(x => x.Group == FixGroup.Apps).ToList();
    private static IEnumerable<(string Item, string Script)> AppsScripts() => Apps().SelectMany(x => x.Steps.Where(s => s.File == "powershell.exe").Select(s => (x.Id, Script(s))));

    [Fact]
    public void TheAppsTabHasEightFixes()
    {
        Assert.Equal(
            ["rebuild-icon-cache", "repair-builtin-apps", "repair-gaming-services", "repair-start-menu", "repair-windows-security", "repair-winget", "reset-onedrive", "reset-store"],
            Apps().Select(x => x.Id).Order().ToArray());
    }

    [Fact]
    public void NoFixOnThisTabRemovesAnAppOrReachesIntoOtherAccounts()
    {
        foreach (var (item, script) in AppsScripts())
        {
            Assert.DoesNotContain("Remove-AppxPackage", script);
            Assert.DoesNotContain("Remove-AppxProvisionedPackage", script);
            Assert.DoesNotContain("-AllUsers", script);
            Assert.DoesNotContain("Set-Service", script);
            // Resetting an app deletes its data (Terminal's settings, Notepad's tabs), so only the Windows Security fix may, and only for that one package.
            if (item != "repair-windows-security") Assert.DoesNotContain("Reset-AppxPackage", script);
        }
        Assert.Contains("Microsoft.SecHealthUI", Script(Fix("repair-windows-security").Steps[1]));
        Assert.DoesNotContain("Get-AppxPackage |", string.Join('\n', AppsScripts().Select(x => x.Script)));
    }

    [Fact]
    public void TheStartMenuRepairRegistersTheXamlFrameworkFirstAndFallsBackToTheFolderWindowsKeeps()
    {
        var script = Script(Fix("repair-start-menu").Steps[0]);
        var order = new[] { "Microsoft.UI.Xaml.CBS", "MicrosoftWindows.Client.Core", "MicrosoftWindows.Client.CBS", "Microsoft.Windows.ShellExperienceHost", "Microsoft.Windows.StartMenuExperienceHost", "windows.immersivecontrolpanel" }
            .Select(x => script.IndexOf($"Name = '{x}'", StringComparison.Ordinal)).ToArray();
        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.Order().ToArray(), order);
        // The shell, start menu and Settings packages are built on Microsoft.UI.Xaml.CBS (checked in their manifests); they have to come after it.
        Assert.True(order[0] < order[3] && order[0] < order[4] && order[0] < order[5]);
        // A package the account lost is not in Get-AppxPackage; the copy in the Windows folder is what is left to register.
        Assert.Contains("is not registered for this account", script);
        Assert.Contains("Test-Path -LiteralPath $copy", script);
    }

    [Fact]
    public void TheFoldersTheStartMenuRepairFallsBackToAreTheRealOnes()
    {
        // The ShellExperienceHost folder has no "Microsoft.Windows." in front, which a pattern built from the package name would miss.
        var script = Script(Fix("repair-start-menu").Steps[0]);
        var folders = Regex.Matches(script, "Folder = \"\\$apps\\\\([^\"]+)\"").Select(x => x.Groups[1].Value).ToList();
        Assert.Contains("ShellExperienceHost_cw5n1h2txyewy", folders);
        Assert.DoesNotContain(folders, x => x.StartsWith("Microsoft.Windows.ShellExperienceHost", StringComparison.Ordinal));
        var systemApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemApps");
        // Only a Windows 11 PC with the CBS shell packages can say whether the names are right.
        if (!Directory.Exists(Path.Combine(systemApps, "MicrosoftWindows.Client.CBS_cw5n1h2txyewy"))) return;
        foreach (var folder in folders) Assert.True(Directory.Exists(Path.Combine(systemApps, folder)), folder);
    }

    [Fact]
    public void WindowsSecurityAndAppInstallerAreRegisteredByFamilyNameWhenTheAccountLostThem()
    {
        var security = Script(Fix("repair-windows-security").Steps[0]);
        Assert.Contains("-RegisterByFamilyName -MainPackage Microsoft.SecHealthUI_8wekyb3d8bbwe", security);
        // Windows Security is built on VCLibs and Microsoft.UI.Xaml.2.8 (its manifest), not on the CBS framework, and does not live in SystemApps.
        Assert.DoesNotContain("Microsoft.UI.Xaml.CBS", security);
        Assert.DoesNotContain("SystemApps", security);
        var installer = Script(Fix("repair-winget").Steps[0]);
        Assert.Contains("-RegisterByFamilyName -MainPackage Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", installer);
        // A family name that is not on the PC makes Windows fail (0x80073CF9); that has to end the step, not pass silently.
        Assert.Contains("throw", installer);
        Assert.False(Fix("repair-windows-security").Steps[0].Optional);
    }

    [Fact]
    public void WingetIsFoundEvenWhenItsCommandIsTurnedOffAndNeverGetsAFlagItRejects()
    {
        var update = Script(Fix("repair-winget").Steps[1]);
        // winget source update does not know --accept-source-agreements and stops with "Argument name was not recognized".
        Assert.DoesNotContain("--accept-source-agreements", update);
        Assert.Contains("source update --disable-interactivity", update);
        Assert.Contains("winget.exe", update);
        Assert.Contains("InstallLocation", update);
        Assert.Contains("App execution aliases", update);
    }

    [Fact]
    public void TheIconFixKeepsTheThumbnailsOnRequest()
    {
        var item = Fix("rebuild-icon-cache");
        var option = Assert.Single(item.Options);
        Assert.Equal("thumbnails", option.Id);
        Assert.True(option.Default);
        var with = FixPlan.Build(item, FixPlan.DefaultOptions(item));
        var without = FixPlan.Build(item, []);
        Assert.Contains(with, x => x.Patterns.Contains("thumbcache_*.db"));
        Assert.DoesNotContain(without, x => x.Patterns.Contains("thumbcache_*.db"));
        Assert.Contains(without, x => x.Patterns.Contains("iconcache_*.db"));
        // Explorer is always started again, with or without the thumbnails.
        Assert.True(without[^1] is { Kind: FixStepKind.StartExplorer, Always: true });
        Assert.True(with[^1] is { Kind: FixStepKind.StartExplorer, Always: true });
    }

    [Fact]
    public void OneDriveIsGivenTimeToCloseBeforeTheResetAndCanBeOnlyRestarted()
    {
        var item = Fix("reset-onedrive");
        var reset = FixPlan.Build(item, []);
        Assert.Equal("/shutdown", reset[0].Args[0]);
        Assert.Contains("Get-Process -Name OneDrive", Script(reset[1]));
        Assert.Equal("/reset", reset[2].Args[0]);
        Assert.True(reset[^1] is { File: "powershell.exe", Always: true });
        Assert.Contains("Start-Sleep -Seconds 20", Script(reset[^1]));

        var restart = FixPlan.Build(item, ["restart-only"]);
        Assert.DoesNotContain(restart, x => x.File == "onedrive" && x.Args[0] == "/reset");
        Assert.True(restart[^1] is { File: "powershell.exe", Always: true });
        Assert.DoesNotContain("Start-Sleep -Seconds 20", Script(restart[^1]));
        Assert.False(item.Options.Single().Default);
    }

    [Fact]
    public void TheStoreServicesAreCheckedBeforeTheCacheIsClearedAndNeverTurnedOn()
    {
        var steps = Fix("reset-store").Steps;
        var wsreset = Array.FindIndex(steps, x => x.File == "wsreset.exe");
        Assert.Equal(steps.Length - 1, wsreset);
        var services = steps.Where(x => x.Kind == FixStepKind.StartService).ToList();
        Assert.Equal(["InstallService", "ClipSVC", "AppXSvc"], services.Select(x => x.Service));
        // A service that is turned off is a warning with Windows' own explanation, and the cache is cleared anyway.
        Assert.All(services, x => { Assert.Contains(3, x.Warn); Assert.True(x.Optional); });
        var register = steps.Single(x => x.File == "powershell.exe");
        Assert.True(Array.IndexOf(steps, register) < wsreset);
        Assert.Contains("Microsoft.WindowsStore_8wekyb3d8bbwe", Script(register));
        Assert.Contains("if (Appx\\Get-AppxPackage -Name $name)", Script(register));
    }

    [Fact]
    public void BuiltInAppsAreOnlyRegisteredAgainWhenTheAccountStillHasThem()
    {
        var item = Fix("repair-builtin-apps");
        Assert.Equal(FixScope.User, item.Scope);
        Assert.Equal(item.Options.Select(x => x.Id), item.Steps.Select(x => x.When));
        Assert.All(item.Options, x => Assert.True(x.Default));
        foreach (var step in item.Steps)
        {
            var script = Script(step);
            // An app removed on purpose (the Debloat page does that) must not come back: no family name, no installing, only the manifest the account already has.
            Assert.DoesNotContain("RegisterByFamilyName", script);
            Assert.Contains("if (-not $package)", script);
            Assert.Contains("exit 4", script);
            Assert.Contains(4, step.Ok);
            // Close-and-restart is the user's call: an open app is reported, not shut down.
            Assert.DoesNotContain("-ForceApplicationShutdown", script);
        }
        Assert.Contains(item.Steps, x => Script(x).Contains("Microsoft.WindowsTerminal"));
    }

    [Fact]
    public void TheGamingFixStartsServicesAndRegistersOnlyWhatIsThere()
    {
        var item = Fix("repair-gaming-services");
        Assert.Equal(FixScope.Machine, item.Scope);
        var services = item.Steps.Where(x => x.Kind == FixStepKind.StartService).ToList();
        Assert.Equal(["GamingServices", "GamingServicesNet", "XblAuthManager", "XblGameSave", "XboxNetApiSvc"], services.Select(x => x.Service));
        Assert.All(services, x => { Assert.Contains(3, x.Warn); Assert.Contains(4, x.Ok); Assert.True(x.Optional); });
        Assert.DoesNotContain(item.Steps, x => x.Kind == FixStepKind.StopService);
        var script = Script(item.Steps[0]);
        Assert.DoesNotContain("RegisterByFamilyName", script);
        Assert.Contains("Gaming Services is not installed", script);
        Assert.Contains(2, item.Steps[0].Warn);
    }

    [Fact]
    public void TheNewServicesAreOnTheListAndNothingElseIs()
    {
        foreach (var service in new[] { "InstallService", "ClipSVC", "AppXSvc", "GamingServices", "GamingServicesNet", "XblAuthManager", "XblGameSave", "XboxNetApiSvc" })
            Assert.True(FixGuard.AllowsService(service), service);
        Assert.False(FixGuard.AllowsService("TrustedInstaller"));
        Assert.False(FixGuard.AllowsService("wuauserv2"));
    }

    [Fact]
    public void EveryFamilyNameIsAWindowsAppAndMatchesThePackageItIsMeantFor()
    {
        // The Start menu repair names folders in SystemApps (cw5n1h2txyewy), which are not family names.
        var all = string.Join('\n', AppsScripts().Where(x => x.Item != "repair-start-menu").Select(x => x.Script));
        var families = Regex.Matches(all, @"(?<![\w.])([A-Za-z]+(?:\.[A-Za-z]+)+)_([a-z0-9]{13})\b").ToList();
        Assert.NotEmpty(families);
        foreach (Match family in families) Assert.Equal("8wekyb3d8bbwe", family.Groups[2].Value);
        Assert.Equal(["Microsoft.DesktopAppInstaller", "Microsoft.SecHealthUI", "Microsoft.StorePurchaseApp", "Microsoft.WindowsStore"], families.Select(x => x.Groups[1].Value).Distinct().Order().ToArray());
    }
}
