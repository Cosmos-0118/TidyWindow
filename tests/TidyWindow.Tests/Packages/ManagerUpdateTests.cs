using System.Net;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class ManagerUpdateTests
{
    [Theory]
    [InlineData(ManagerKind.Winget, "https://github.com/microsoft/winget-cli/releases/tag/v1.29.380", "1.29.380")]
    [InlineData(ManagerKind.Scoop, "https://github.com/ScoopInstaller/Scoop/releases/tag/v0.6.0", "0.6.0")]
    [InlineData(ManagerKind.Chocolatey, "https://packages.chocolatey.org/chocolatey.2.7.5.nupkg", "2.7.5")]
    [InlineData(ManagerKind.Chocolatey, "https://packages.chocolatey.org/chocolatey.2.7.nupkg", "2.7")]
    // Another repository, a pre-release, another host, a page that is not a release, or nothing at all is not an answer.
    [InlineData(ManagerKind.Winget, "https://github.com/evil/winget-cli/releases/tag/v1.29.380", null)]
    [InlineData(ManagerKind.Winget, "https://github.com/microsoft/winget-cli/releases/tag/v1.30.0-preview", null)]
    [InlineData(ManagerKind.Scoop, "https://github.com/ScoopInstaller/Scoop/releases/tag/nightly", null)]
    [InlineData(ManagerKind.Scoop, "https://example.com/ScoopInstaller/Scoop/releases/tag/v0.6.0", null)]
    [InlineData(ManagerKind.Chocolatey, "https://packages.chocolatey.org/chocolatey.2.8.0-beta1.nupkg", null)]
    [InlineData(ManagerKind.Chocolatey, "https://packages.chocolatey.org/chocolatey-core.extension.2.0.0.nupkg", null)]
    [InlineData(ManagerKind.Chocolatey, "", null)]
    [InlineData(ManagerKind.Chocolatey, null, null)]
    public void ARedirectIsReadAsAVersionOnlyWhenItIsThatManagersOwnPlainRelease(ManagerKind kind, string? location, string? expected) =>
        Assert.Equal(expected, ManagerUpdateCheck.ParseLatest(kind, location));

    [Theory]
    [InlineData("v1.28.0", "1.29.380", true)]
    [InlineData("1.29.380", "1.29.380", false)]
    [InlineData("v1.29.380", "1.29.380", false)]
    [InlineData("1.29.380.0", "1.29.380", false)]
    [InlineData("1.29.381", "1.29.380", false)]
    [InlineData("2.5.1", "2.7.5", true)]
    [InlineData("2.10.0", "2.9.0", false)]
    [InlineData("v0.5.3", "0.6.0", true)]
    [InlineData("v0.5.3 - Released at 2024-01-01", "0.6.0", true)]
    // Scoop on another branch reports a commit, and a missing version says nothing: neither is ever called outdated.
    [InlineData("a1b2c3d", "0.6.0", false)]
    [InlineData("1234567", "0.6.0", false)]
    [InlineData("", "0.6.0", false)]
    [InlineData("1.0", "not a version", false)]
    public void OnlyAHigherReleaseCountsAsNewer(string installed, string latest, bool newer) =>
        Assert.Equal(newer, ManagerUpdateCheck.IsNewer(installed, latest));

    [Fact]
    public async Task TheLatestVersionIsReadFromTheRedirectAndAskedOnlyOnceWhileItIsFresh()
    {
        var asked = new List<Uri>();
        using var check = new ManagerUpdateCheck(new Handler(request => { asked.Add(request.RequestUri!); return Redirect("https://github.com/microsoft/winget-cli/releases/tag/v1.29.380"); }));
        Assert.Equal("1.29.380", await check.LatestAsync(ManagerKind.Winget));
        Assert.Equal("1.29.380", await check.LatestAsync(ManagerKind.Winget));
        Assert.Single(asked);
        Assert.Equal(ManagerUpdateCheck.Probe(ManagerKind.Winget), asked[0]);
    }

    [Fact]
    public async Task ARelativeRedirectIsResolvedAgainstTheAddressThatWasAsked()
    {
        using var check = new ManagerUpdateCheck(new Handler(_ => Redirect("/ScoopInstaller/Scoop/releases/tag/v0.6.0")));
        Assert.Equal("0.6.0", await check.LatestAsync(ManagerKind.Scoop));
    }

    [Fact]
    public async Task AnswersThatAreNotARedirectToARealReleaseAreNothing()
    {
        foreach (var handler in new[]
        {
            new Handler(_ => new(HttpStatusCode.OK)),
            new Handler(_ => new(HttpStatusCode.NotFound)),
            new Handler(_ => new(HttpStatusCode.Found)),
            new Handler(_ => Redirect("https://github.com/ScoopInstaller/Scoop/releases/tag/nightly")),
            new Handler(_ => throw new HttpRequestException("offline")),
            new Handler(_ => throw new TaskCanceledException("slow"))
        })
        {
            using var check = new ManagerUpdateCheck(handler);
            Assert.Null(await check.LatestAsync(ManagerKind.Scoop));
        }
    }

    [Fact]
    public async Task APersonCancellingIsNotSwallowed()
    {
        using var check = new ManagerUpdateCheck(new Handler(_ => throw new HttpRequestException("offline")));
        using var cancelled = new CancellationTokenSource(); await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check.LatestAsync(ManagerKind.Winget, cancelled.Token));
    }

    [Fact]
    public async Task AFailedLookupIsNotRememberedAndTheNextOneTriesAgain()
    {
        var calls = 0;
        using var check = new ManagerUpdateCheck(new Handler(_ => ++calls == 1 ? throw new HttpRequestException("offline") : Redirect("https://github.com/microsoft/winget-cli/releases/tag/v1.29.380")));
        Assert.Null(await check.LatestAsync(ManagerKind.Winget));
        Assert.Equal("1.29.380", await check.LatestAsync(ManagerKind.Winget));
    }

    [Fact]
    public async Task OnlyAWorkingManagerWithANewerReleaseIsOfferedAnUpdate()
    {
        using var check = new ManagerUpdateCheck(new Handler(_ => Redirect("https://github.com/ScoopInstaller/Scoop/releases/tag/v0.6.0")));
        var scoop = ManagerDefinition.All.Single(x => x.Kind == ManagerKind.Scoop);
        ManagerStatus Status(ManagerHealth health, string version) => new(scoop, health, version, "scoop.ps1", "detail");

        Assert.Equal("0.6.0", (await check.WithAvailableAsync(Status(ManagerHealth.Ready, "v0.5.3"))).AvailableVersion);
        Assert.Null((await check.WithAvailableAsync(Status(ManagerHealth.Ready, "v0.6.0"))).AvailableVersion);
        Assert.Null((await check.WithAvailableAsync(Status(ManagerHealth.Ready, "a1b2c3d"))).AvailableVersion);
        Assert.Null((await check.WithAvailableAsync(Status(ManagerHealth.Ready, ""))).AvailableVersion);
        Assert.Null((await check.WithAvailableAsync(Status(ManagerHealth.Unavailable, "v0.5.3"))).AvailableVersion);
        Assert.Null((await check.WithAvailableAsync(Status(ManagerHealth.Missing, ""))).AvailableVersion);
        // A stale answer is cleared, not carried over.
        Assert.Null((await check.WithAvailableAsync(Status(ManagerHealth.Ready, "v0.6.0") with { AvailableVersion = "0.5.9" })).AvailableVersion);
    }

    [Fact]
    public async Task ALookupThatFailsNeverChangesHowHealthyAManagerLooks()
    {
        using var check = new ManagerUpdateCheck(new Handler(_ => throw new HttpRequestException("offline")));
        var status = new ManagerStatus(ManagerDefinition.All.Single(x => x.Kind == ManagerKind.Winget), ManagerHealth.Ready, "v1.28.0", "winget.exe", "Ready");
        var result = await check.WithAvailableAsync(status);
        Assert.Equal(status, result);
    }

    [Theory]
    [InlineData(ManagerKind.Winget, "Microsoft.AppInstaller", true)]
    [InlineData(ManagerKind.Winget, "microsoft.appinstaller", true)]
    [InlineData(ManagerKind.Winget, @"MSIX\Microsoft.DesktopAppInstaller_2025.1.2.3_x64__8wekyb3d8bbwe", true)]
    [InlineData(ManagerKind.Chocolatey, "chocolatey", true)]
    [InlineData(ManagerKind.Chocolatey, "Chocolatey", true)]
    // Everything else stays: another package that merely has the name in it, and the same ids under another manager.
    [InlineData(ManagerKind.Chocolatey, "chocolatey-core.extension", false)]
    [InlineData(ManagerKind.Chocolatey, "chocolateygui", false)]
    [InlineData(ManagerKind.Winget, "Microsoft.AppInstallerFileBuilder", false)]
    [InlineData(ManagerKind.Winget, "Git.Git", false)]
    [InlineData(ManagerKind.Scoop, "chocolatey", false)]
    [InlineData(ManagerKind.Scoop, "Microsoft.AppInstaller", false)]
    public void ThePackageManagersThemselvesAreNotListedAsInstalledApps(ManagerKind manager, string id, bool hidden)
    {
        var package = new ManagedPackage(manager, id, id, "1.0");
        Assert.Equal(hidden, InstallationOutput.IsPackageManager(package));
        Assert.Equal(hidden, InstallationOutput.IsPlatformComponent(package));
    }

    private static HttpResponseMessage Redirect(string location) => new(HttpStatusCode.Found) { Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) } };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}
