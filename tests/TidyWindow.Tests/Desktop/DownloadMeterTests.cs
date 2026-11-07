using TidyWindow.Next.Desktop;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class DownloadMeterTests
{
    private const long Mb = 1024 * 1024;

    private static DownloadMeter Started(long total)
    {
        var meter = new DownloadMeter();
        meter.Reset(total);
        return meter;
    }

    [Fact]
    public void TheShownCountEasesTowardTheRealOneAndNeverOvershoots()
    {
        var meter = Started(100 * Mb);
        meter.Report(50 * Mb, 100 * Mb);
        var previous = 0d;
        for (var i = 0; i < 150; i++)
        {
            meter.Tick(1 / 30d);
            Assert.InRange(meter.ShownBytes, previous, 50 * Mb);
            previous = meter.ShownBytes;
        }
        Assert.Equal(50 * Mb, meter.ShownBytes);
        Assert.Equal(0.5, meter.Fraction, 3);
    }

    [Fact]
    public void ABurstOfReportsBetweenTicksMovesTheBarOnlyOneStep()
    {
        var meter = Started(100 * Mb);
        for (var i = 1; i <= 500; i++) meter.Report(i * Mb / 10, 100 * Mb);
        meter.Tick(1 / 30d);
        Assert.InRange(meter.ShownBytes, 1, 50 * Mb * 0.2);
    }

    [Fact]
    public void ALateOrReorderedReportCannotMakeTheCountGoBackwards()
    {
        var meter = Started(10 * Mb);
        meter.Report(6 * Mb, 10 * Mb);
        meter.Report(4 * Mb, 10 * Mb);
        for (var i = 0; i < 90; i++) meter.Tick(1 / 30d);
        Assert.Equal(6 * Mb, meter.ShownBytes);
    }

    [Fact]
    public void ProgressTextKeepsOneUnitAndOneDecimalSoItDoesNotChangeWidth()
    {
        var meter = Started(Mb * 45 + Mb / 2);
        meter.Report(10 * Mb, meter.Total);
        for (var i = 0; i < 120; i++) meter.Tick(1 / 30d);
        Assert.Equal("10.0 of 45.5 MB", meter.Progress());
        meter.Report(10 * Mb + 1000, meter.Total);
        meter.Tick(1 / 30d);
        Assert.EndsWith(" of 45.5 MB", meter.Progress());
    }

    [Fact]
    public void ASmallDownloadIsMeasuredInKilobytesThroughout()
    {
        var meter = Started(800 * 1024);
        meter.Report(5 * 1024, meter.Total);
        for (var i = 0; i < 120; i++) meter.Tick(1 / 30d);
        Assert.Equal("5.0 of 800.0 KB", meter.Progress());
    }

    [Fact]
    public void PaceStaysEmptyUntilTheSpeedHasSettledThenShowsRateAndTimeLeft()
    {
        var meter = Started(100 * Mb);
        var received = 0L;
        for (var i = 0; i < 30; i++) { received += Mb / 15; meter.Report(received, meter.Total); meter.Tick(1 / 30d); }
        Assert.Equal("", meter.Pace());
        for (var i = 0; i < 300; i++) { received += Mb / 15; meter.Report(received, meter.Total); meter.Tick(1 / 30d); }
        Assert.Matches(@"^2\.0 MB/s · \d+ s left$", meter.Pace());
    }

    [Fact]
    public void AnUnknownSizeShowsAnIndeterminateBarAndNoPercentOrTimeLeft()
    {
        var meter = new DownloadMeter();
        meter.Reset(null);
        meter.Report(3 * Mb, null);
        for (var i = 0; i < 120; i++) meter.Tick(1 / 30d);
        Assert.True(meter.Indeterminate);
        Assert.Equal("", meter.Percent());
        Assert.Equal("3.0 MB downloaded", meter.Progress());
        Assert.DoesNotContain("left", meter.Pace());
    }

    [Fact]
    public void ResetStartsAFreshDownloadFromZero()
    {
        var meter = Started(10 * Mb);
        meter.Report(10 * Mb, meter.Total);
        for (var i = 0; i < 90; i++) meter.Tick(1 / 30d);
        meter.Reset(20 * Mb);
        Assert.Equal(0, meter.Fraction);
        Assert.Equal("0.0 of 20.0 MB", meter.Progress());
        Assert.Equal("0%", meter.Percent());
    }
}
