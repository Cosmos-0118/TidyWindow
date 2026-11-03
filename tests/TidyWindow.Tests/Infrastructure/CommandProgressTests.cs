using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class CommandProgressTests
{
    [Theory]
    [InlineData("Found App [Vendor.App]\r\nDownloading https://example.com/app.exe\r\n  ██████████▒▒▒▒▒▒▒▒▒▒  50%", "Downloading", 50)]
    [InlineData("Downloading https://example.com/app.exe\r\n  ██████▒▒▒▒  1.20 MB / 4.80 MB", "Downloading", 25)]
    [InlineData("Progress: Downloading git 2.45.0... 87%", "Downloading", 87)]
    [InlineData("  ███████████████████████████████  100%", "Downloading", 100)]
    [InlineData("  ▒▒▒▒  0.5%", "Downloading", 0)]
    [InlineData("Installing packages 40%", "Running", 40)]
    public void ReportsThePercentageOnTheNewestLine(string output, string label, int percent) =>
        Assert.Equal(new CommandProgress(label, percent), CommandProgressReader.Latest(output));

    [Fact]
    public void KeepsOnlyTheLatestRedrawOfAnInPlaceProgressLine()
    {
        var output = "  ██▒▒▒▒▒▒▒▒  20%\r  ████▒▒▒▒▒▒  40%\r  ██████▒▒▒▒  60%";
        Assert.Equal("Downloading 60%", CommandProgressReader.Latest(output)?.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Downloading https://example.com/app.exe")]
    [InlineData("Downloading 50%\r\nSuccessfully verified installer hash")]
    [InlineData("Starting package install...")]
    [InlineData("Version 1.2.3 has 250% more things")]
    public void ReportsNothingWhenTheNewestLineHasNoProgress(string output) => Assert.Null(CommandProgressReader.Latest(output));

    [Fact]
    public void IgnoresAnsiColourCodes() =>
        Assert.Equal("Downloading 33%", CommandProgressReader.Latest("\u001b[36mDownloading\u001b[0m 33%")?.Text);
}
