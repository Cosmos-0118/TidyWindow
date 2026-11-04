using TidyWindow.Next.Core;
using TidyWindow.Next.Desktop;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class CloseNoticeTests
{
    private static QueueEntry Running(PackageOperation operation) => new(Guid.NewGuid(), operation, QueueItemState.Running);

    [Fact]
    public void AFixIsToldItWillFinishItsStepAndPutThingsBackBeforeTheWindowCloses()
    {
        var text = CloseNotice.Finishing(Running(new(PackageOperationKind.Fix, default, "sfc", "System file check", Fix: new("sfc", []))));
        Assert.Contains("System file check", text);
        Assert.Contains("puts back anything it changed", text);
        Assert.Contains("Close now", text);
    }

    [Fact]
    public void APackageOperationIsToldItIsBeingStopped()
    {
        var text = CloseNotice.Finishing(Running(PackageOperation.Install(new(ManagerKind.Scoop, "git", "Git", "1", "main"))));
        Assert.StartsWith("Stopping Git", text);
        Assert.DoesNotContain("puts back", text);
    }

    [Fact]
    public void NothingRunningStillSaysTheWindowClosesByItself()
    {
        Assert.Contains("closes by itself", CloseNotice.Finishing(null));
    }
}
