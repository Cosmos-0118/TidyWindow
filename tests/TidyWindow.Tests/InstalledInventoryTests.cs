using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class InstalledInventoryTests
{
    private static Package App(ManagerKind manager, string id, string name) => new(manager, id, name, "99", "source");

    [Fact]
    public void ExactSourceIdMatchesEvenWhenDisplayNameDiffers()
    {
        var inventory = new InstalledInventory([new(ManagerKind.Winget, "Git.Git", "Git version 2.50", "2.50")]);
        Assert.Equal("2.50", inventory.Find(App(ManagerKind.Winget, "git.git", "Git"))?.Version);
        Assert.Null(inventory.Find(App(ManagerKind.Winget, "Other.Git", "Git")));
    }

    [Fact]
    public void WindowsInstalledDisplayNameRecognizesAnotherSource()
    {
        var inventory = new InstalledInventory([new(null, null, "Google Chrome", "144")]);
        Assert.Equal("144", inventory.Find(App(ManagerKind.Chocolatey, "googlechrome", "googlechrome"))?.Version);
        Assert.Null(inventory.Find(App(ManagerKind.Chocolatey, "googlechrome-beta", "googlechrome-beta")));
    }

    [Theory]
    [InlineData("Git", "GitHub")]
    [InlineData("Visual Studio Code", "Visual Studio Code Insiders")]
    [InlineData("Python", "Python Launcher")]
    [InlineData("C++", "C#")]
    public void RelatedNamesDoNotBecomeFalseInstalledMatches(string installed, string candidate)
    {
        var inventory = new InstalledInventory([new(null, null, installed, "1")]);
        Assert.Null(inventory.Find(App(ManagerKind.Winget, "Vendor.App", candidate)));
    }

    [Fact]
    public void SourceIdWinsOverAnAmbiguousDisplayName()
    {
        var inventory = new InstalledInventory([new(null, null, "App", "old"), new(ManagerKind.Chocolatey, "app", "App", "new")]);
        Assert.Equal("new", inventory.Find(App(ManagerKind.Chocolatey, "app", "App"))?.Version);
    }

    [Fact]
    public void InventoryIncludesInstalledPackagesBeyondSearchCap()
    {
        var output = string.Join("\n", Enumerable.Range(0, 125).Select(x => $"app{x}|1.0"));
        Assert.Equal(60, PackageOutputParser.Parse(ManagerKind.Chocolatey, output).Count);
        var inventory = new InstalledInventory(PackageOutputParser.Parse(ManagerKind.Chocolatey, output, int.MaxValue)
            .Select(x => new InstalledApp(x.Manager, x.Id, x.Name, x.Version)));
        Assert.NotNull(inventory.Find(App(ManagerKind.Chocolatey, "app124", "app124")));
    }
}
