using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class InstalledHintsTests
{
    private static ManagedPackage App(ManagerKind manager, string id, string name = "App", bool global = false) => new(manager, id, name, "1.0", Global: global);

    [Fact]
    public void WindowsInstallerProductIdBecomesTheProductCode()
    {
        var query = InstalledHints.IconFor(App(ManagerKind.Winget, @"ARP\Machine\X64\{AC76BA86-7AD7-1033-7B44-AC0F074E4100}", "Adobe Acrobat"));
        Assert.Equal("{AC76BA86-7AD7-1033-7B44-AC0F074E4100}", query.ProductCode);
        Assert.Equal("Adobe Acrobat", query.Name);
    }

    [Theory]
    [InlineData(@"ARP\Machine\X64\Git_is1")]
    [InlineData("Microsoft.VisualStudioCode")]
    public void OtherIdsAreNotProductCodes(string id) => Assert.Equal("", InstalledHints.IconFor(App(ManagerKind.Winget, id)).ProductCode);

    [Fact]
    public void ScoopAppsPointAtTheirCurrentFolder()
    {
        Assert.Equal(@"%UserProfile%\scoop\apps\git\current", InstalledHints.IconFor(App(ManagerKind.Scoop, "git")).InstallLocation);
        Assert.Equal(@"%ProgramData%\scoop\apps\git\current", InstalledHints.IconFor(App(ManagerKind.Scoop, "git", global: true)).InstallLocation);
        Assert.Equal("", InstalledHints.IconFor(App(ManagerKind.Scoop, @"..\evil")).InstallLocation);
        Assert.Equal("", InstalledHints.IconFor(App(ManagerKind.Chocolatey, "git")).InstallLocation);
    }

    [Fact]
    public void StoreAppsGetAPathInsideTheirPackage()
    {
        var path = InstalledHints.PackagePath(App(ManagerKind.Winget, @"MSIX\Microsoft.WindowsTerminal_1.20.0.0_x64__8wekyb3d8bbwe"));
        Assert.Equal(@"%ProgramFiles%\WindowsApps\Microsoft.WindowsTerminal_1.20.0.0_x64__8wekyb3d8bbwe\app.exe", path);
        Assert.Equal("", InstalledHints.PackagePath(App(ManagerKind.Winget, "Git.Git")));
    }
}
