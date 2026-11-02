using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Tests;

public sealed class UnstickElevationTests
{
    [Fact]
    public async Task WhenTidyWindowRunsAsAdministratorNoProgramIsStartedWithItsRights()
    {
        var fake = new FakeUnstickSystem { Elevated = true, Running_ = { "TextInputHost", "rdpclip", "explorer" } };
        var runner = new UnstickRunner(fake);

        var input = await runner.RunAsync("unstick-input");
        Assert.False(input.Ok);
        Assert.Contains("administrator", input.Message);
        Assert.Contains("end TextInputHost", fake.Calls);
        Assert.DoesNotContain("start ctfmon.exe", fake.Calls);

        var clipboard = await runner.RunAsync("restart-clipboard");
        Assert.True(clipboard.Ok);
        Assert.Contains("rdpclip", clipboard.Message);
        Assert.Contains("host cbdhsvc", fake.Calls);
        Assert.DoesNotContain("end rdpclip", fake.Calls);
        Assert.DoesNotContain("start rdpclip.exe", fake.Calls);

        var explorer = await runner.RunAsync("restart-explorer");
        Assert.False(explorer.Ok);
        Assert.Contains("administrator", explorer.Message);
        Assert.DoesNotContain("explorer", fake.Calls);
    }

    [Fact]
    public async Task RunNormallyTheSameActionsStartWhatTheyNeed()
    {
        var fake = new FakeUnstickSystem { Running_ = { "TextInputHost", "rdpclip" } };
        var runner = new UnstickRunner(fake);
        Assert.True((await runner.RunAsync("unstick-input")).Ok);
        Assert.Contains("start ctfmon.exe", fake.Calls);
        Assert.True((await runner.RunAsync("restart-clipboard")).Ok);
        Assert.Contains("start rdpclip.exe", fake.Calls);
        Assert.True((await runner.RunAsync("restart-explorer")).Ok);
        Assert.Contains("explorer", fake.Calls);
    }
}
