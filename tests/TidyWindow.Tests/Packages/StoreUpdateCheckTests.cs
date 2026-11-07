using System.Net;
using System.Text;
using System.Text.Json;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class StoreUpdateCheckTests(Xunit.Abstractions.ITestOutputHelper output)
{
    // Asks the real WinGet and the real Store catalog about this PC. Opt in with TIDYWINDOW_LIVE_TESTS=1.
    [LiveFact]
    public async Task LiveStoreUpdatesOnThisPcWhenExplicitlyEnabled()
    {
        var runner = new ProcessRunner(); var locator = new ExecutableLocator();
        var packages = new PackageService(runner, locator);
        using var check = new StoreUpdateCheck(runner);
        var service = new InstallationService(packages, runner, locator, store: check);
        var status = (await packages.DetectAsync()).Single(x => x.Definition.Kind == ManagerKind.Winget);
        var inventory = await service.ReadAsync(status);
        Assert.Null(inventory.Error); Assert.Null(inventory.UpdateError);
        foreach (var app in inventory.Packages.Where(x => x.HasUpdateInfo)) output.WriteLine($"{app.Name} [{app.Id}] {app.Version} -> {app.AvailableVersion ?? app.StoreVersion + " (Store)"}");
        Assert.NotEmpty(inventory.Packages);
    }

    private const string Installed = """TIDYWINDOW_JSON:[{"Name":"OpenAI.Codex","PackageFamilyName":"OpenAI.Codex_2p2nqsd0c76g0","Version":"26.1103.3517.0"},{"Name":"Microsoft.WindowsCalculator","PackageFamilyName":"Microsoft.WindowsCalculator_8wekyb3d8bbwe","Version":"11.2607.0.0"}]""";
    private static readonly Version Newest = new(26, 1103, 4239, 0);

    private static ManagerStatus Ready() => new(ManagerDefinition.All.Single(x => x.Kind == ManagerKind.Winget), ManagerHealth.Ready, "1", "winget.exe", "Ready");
    private static ManagedPackage ChatGpt(string version = "26.1103.3517.0") => new(ManagerKind.Winget, "OpenAI.ChatGPT", "ChatGPT", version, Source: "winget");

    [Theory]
    [InlineData("26.1103.3517.0", "26.1103.4239.0", true)]   // ChatGPT: a later build of the same line
    [InlineData("22609.1401.11.0", "22609.1401.12.0", true)]
    [InlineData("26.1103.4239.0", "26.1103.4239.0", false)]  // up to date
    [InlineData("26.1103.4239.0", "26.1103.3517.0", false)]  // the Store lists an older build: never a downgrade
    [InlineData("1.2.20.0", "1.2.20.70", false)]             // the Store reserves the last number for itself
    [InlineData("11.2607.0.0", "2021.2607.0.0", false)]      // a second numbering scheme the Store lists for the same app
    [InlineData("7.6.9.0", "2026.1025.336.0", false)]
    [InlineData("Unknown", "1.0.1.0", false)]
    public void OnlyALaterBuildOfTheSameLineCountsAsAnUpdate(string installed, string latest, bool expected) =>
        Assert.Equal(expected, StoreUpdateCheck.IsNewer(installed, Version.Parse(latest)));

    [Fact]
    public void ParsesTheStoreAppsWindowsReportsWhetherOneOrManyCameBack()
    {
        Assert.Equal(2, StoreUpdateCheck.ParseInstalled("noise\r\n" + Installed + "\r\n").Count);
        var one = Assert.Single(StoreUpdateCheck.ParseInstalled("""TIDYWINDOW_JSON:{"Name":"A.B","PackageFamilyName":"A.B_x","Version":"1.0.0.0"}"""));
        Assert.Equal("A.B_x", one.FamilyName);
        Assert.Empty(StoreUpdateCheck.ParseInstalled("no json here"));
        Assert.Empty(StoreUpdateCheck.ParseInstalled("TIDYWINDOW_JSON:[]"));
    }

    [Fact]
    public void AWingetCatalogIdIsPairedWithItsStoreAppByVersionAndPublisher()
    {
        var stores = StoreUpdateCheck.ParseInstalled(Installed);
        var match = Assert.Single(StoreUpdateCheck.Match([ChatGpt()], stores));
        Assert.Equal("OpenAI.Codex_2p2nqsd0c76g0", match.Store.FamilyName);
        // Another publisher, another version, or two equally good candidates: nothing is guessed.
        Assert.Empty(StoreUpdateCheck.Match([ChatGpt("1.0")], stores));
        Assert.Empty(StoreUpdateCheck.Match([ChatGpt() with { Id = "Other.ChatGPT" }], stores));
        Assert.Empty(StoreUpdateCheck.Match([ChatGpt()], [.. stores, new("OpenAI.Other", "OpenAI.Other_x", "26.1103.3517.0")]));
    }

    [Fact]
    public void AStoreAppListedByPackageNameIsPairedByItsFamily()
    {
        var stores = StoreUpdateCheck.ParseInstalled(Installed);
        var calculator = new ManagedPackage(ManagerKind.Winget, @"MSIX\Microsoft.WindowsCalculator_11.2607.0.0_x64__8wekyb3d8bbwe", "Calculator", "11.2607.0.0");
        Assert.Equal("Microsoft.WindowsCalculator_8wekyb3d8bbwe", Assert.Single(StoreUpdateCheck.Match([calculator], stores)).Store.FamilyName);
    }

    [Fact]
    public void AppsThatAlreadyHaveAnUpdateOrArePinnedOrBelongToAnotherManagerAreLeftAlone()
    {
        var stores = StoreUpdateCheck.ParseInstalled(Installed);
        Assert.Empty(StoreUpdateCheck.Match([ChatGpt() with { AvailableVersion = "9" }, ChatGpt() with { Held = true }, ChatGpt() with { Manager = ManagerKind.Scoop }], stores));
    }

    [Fact]
    public async Task ReportsTheNewerVersionTheStoreListsForAnInstalledApp()
    {
        using var check = new StoreUpdateCheck(Powershell(Installed), Store());
        var newer = await check.NewerAsync([ChatGpt()]);
        Assert.Equal(Newest.ToString(), newer[ChatGpt().Key]);
    }

    [Fact]
    public async Task NothingIsReportedWhenTheStoreIsUnreachableOrAnswersWithGarbage()
    {
        using var offline = new StoreUpdateCheck(Powershell(Installed), new Network(_ => throw new HttpRequestException("offline")));
        Assert.Empty(await offline.NewerAsync([ChatGpt()]));
        using var garbage = new StoreUpdateCheck(Powershell(Installed), new Network(_ => new(HttpStatusCode.OK) { Content = new StringContent("<html>") }));
        Assert.Empty(await garbage.NewerAsync([ChatGpt()]));
        using var noPowerShell = new StoreUpdateCheck(new Scripted(_ => new(1, "", "boom")), Store());
        Assert.Empty(await noPowerShell.NewerAsync([ChatGpt()]));
    }

    [Fact]
    public async Task NothingIsAskedOfTheStoreForAnAppThatPairsWithNoStoreAppAndNothingIsAskedTwice()
    {
        var calls = 0;
        using var check = new StoreUpdateCheck(Powershell(Installed), Store(() => calls++));
        // Its version differs from the installed Store app's, so it is not that app and the Store is never contacted.
        Assert.Empty(await check.NewerAsync([ChatGpt("26.1103.4239.0")]));
        Assert.Equal(0, calls);
        await check.NewerAsync([ChatGpt()]);
        var first = calls;
        await check.NewerAsync([ChatGpt()]);
        Assert.Equal(first, calls);
    }

    [Fact]
    public async Task InstalledAppsGetTheStoreVersionWhenWingetListsNoUpdate()
    {
        var runner = new Scripted(command =>
            command.FileName.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase) ? new(0, Installed, "")
            : command.Arguments.Contains("--upgrade-available") ? new(0, "No installed package found matching input criteria.\r\n", "")
            : new(0, "Name    Id             Version        Source\r\n---------------------------------------------\r\nChatGPT OpenAI.ChatGPT 26.1103.3517.0 winget\r\n", ""));
        using var check = new StoreUpdateCheck(runner, Store());
        var service = new InstallationService(new PackageService(runner, new Locator()), runner, new Locator(), store: check);
        var app = Assert.Single((await service.ReadAsync(Ready())).Packages);
        Assert.Null(app.AvailableVersion);
        Assert.Equal(Newest.ToString(), app.StoreVersion);
        Assert.True(app.HasStoreUpdate);
        Assert.True(app.HasUpdateInfo);
    }

    [Fact]
    public async Task AFailingStoreCheckNeverTurnsIntoAnUpdateError()
    {
        var runner = new Scripted(command =>
            command.FileName.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase) ? throw new IOException("no powershell")
            : command.Arguments.Contains("--upgrade-available") ? new(0, "No installed package found matching input criteria.\r\n", "")
            : new(0, "Name    Id             Version        Source\r\n---------------------------------------------\r\nChatGPT OpenAI.ChatGPT 26.1103.3517.0 winget\r\n", ""));
        using var check = new StoreUpdateCheck(runner, Store());
        var service = new InstallationService(new PackageService(runner, new Locator()), runner, new Locator(), store: check);
        var inventory = await service.ReadAsync(Ready());
        Assert.Null(inventory.UpdateError);
        Assert.Null(Assert.Single(inventory.Packages).StoreVersion);
    }

    [Fact]
    public void StoreVersionSurvivesTheSavedList()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "installed.json");
        try
        {
            var snapshot = new InstalledSnapshot(path);
            snapshot.Save([ChatGpt() with { StoreVersion = "26.1103.4239.0" }]);
            Assert.Equal("26.1103.4239.0", Assert.Single(snapshot.Load()).StoreVersion);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void ParsesTheHighestPublishedVersionPerPackageFamilyFromTheCatalog()
    {
        using var document = JsonDocument.Parse(Catalog("9PLM9XGG6VKS", "OpenAI.Codex_2p2nqsd0c76g0", "26.1103.4239.0", "26.1103.3517.0"));
        var (product, latest) = Assert.Single(StoreUpdateCheck.ParseCatalog(document.RootElement));
        Assert.Equal("9PLM9XGG6VKS", product);
        Assert.Equal(Newest, latest["OpenAI.Codex_2p2nqsd0c76g0"]);
    }

    private static string Catalog(string product, string family, params string[] versions)
    {
        var name = family.Split('_')[0]; var publisher = family.Split('_')[1];
        var packages = string.Join(",", versions.Select(v => $$"""{"PackageFullName":"{{name}}_{{v}}_x64__{{publisher}}","PackageFamilyName":"{{family}}"}"""));
        return "{\"Products\":[{\"ProductId\":\"" + product + "\",\"DisplaySkuAvailabilities\":[{\"Sku\":{\"Properties\":{\"Packages\":[" + packages + "]}}}]}]}";
    }

    private static Network Store(Action? asked = null) => new(request =>
    {
        asked?.Invoke();
        var url = request.RequestUri!.AbsoluteUri;
        if (url.Contains("/products/lookup")) return Json("""{"Products":[{"ProductId":"9PLM9XGG6VKS"}]}""");
        if (url.Contains("/products?bigIds=9PLM9XGG6VKS")) return Json(Catalog("9PLM9XGG6VKS", "OpenAI.Codex_2p2nqsd0c76g0", "26.1103.4239.0", "26.1103.3517.0"));
        return new(HttpStatusCode.NotFound);
    });
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static Scripted Powershell(string output) => new(_ => new(0, output, ""));

    private sealed class Network(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(answer(request));
    }
    private sealed class Scripted(Func<CommandSpec, CommandResult> answer) : ICommandRunner
    {
        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default) => Task.FromResult(answer(command));
    }
    private sealed class Locator : IExecutableLocator
    {
        public string? Find(ManagerKind manager) => "manager.exe";
        public string PowerShell => "powershell.exe";
        public (string FileName, string? Problem) Elevated(string fileName) => (fileName, null);
    }
}
