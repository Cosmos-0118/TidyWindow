using TidyWindow.Next.Desktop;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class AdaptiveLayoutTests
{
    [Theory]
    [InlineData(480)]
    [InlineData(560)]
    [InlineData(640)]
    [InlineData(800)]
    [InlineData(860)]
    [InlineData(960)]
    [InlineData(1100)]
    [InlineData(1920)]
    public void PackageNameRetainsUsableSpaceAcrossWindowSizes(double width)
    {
        var layout = AdaptiveLayout.ForWidth(width);
        var content = width - layout.SidebarWidth - layout.Padding * 2;
        var packageName = content - 24 - 48 - 85 - (layout.CompactResults ? 0 : 200);
        Assert.True(packageName >= 200, $"Only {packageName} DIPs remain for the package at {width} DIPs");
        if (!layout.CompactManagers) Assert.True(content >= 600);
    }

    [Fact]
    public void LargeWindowUsesTheAvailableContentWidth()
    {
        var layout = AdaptiveLayout.ForWidth(1920);
        Assert.False(layout.NavigationRail);
        Assert.False(layout.CompactResults);
        Assert.True(1920 - layout.SidebarWidth - layout.Padding * 2 > 1600);
    }

    [Fact]
    public void SmallWindowCollapsesNavigationAndMovesMetadataIntoRows()
    {
        var layout = AdaptiveLayout.ForWidth(480);
        Assert.True(layout.NavigationRail);
        Assert.True(layout.CompactManagers);
        Assert.True(layout.CompactResults);
    }
}
