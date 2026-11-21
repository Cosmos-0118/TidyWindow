using System.Runtime.Versioning;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Tests;

public sealed class BatteryHealthTests
{
    private static BatteryReading Battery(long design, long full, bool relative = false, int? cycles = null) =>
        new("Battery", "Maker", "Lithium-ion", design, full, cycles, relative);

    [Fact]
    public void HealthIsFullChargeOverDesignCapacity() =>
        Assert.Equal(84.2, Battery(50_000, 42_100).HealthPercent!.Value, 1);

    [Fact]
    public void ABatteryAboveItsDesignCapacityIsCappedAtOneHundred()
    {
        var reading = Battery(50_000, 51_300);
        Assert.Equal(100, reading.HealthPercent);
        Assert.Equal(0, reading.WornCapacity);
        Assert.Equal(0, reading.WearPercent);
    }

    [Theory]
    [InlineData(0, 40_000)]
    [InlineData(50_000, 0)]
    [InlineData(0, 0)]
    public void HealthIsUnknownWhenEitherCapacityIsMissing(long design, long full)
    {
        var reading = Battery(design, full);
        Assert.Null(reading.HealthPercent);
        Assert.Null(reading.WearPercent);
        Assert.Null(reading.WornCapacity);
        Assert.Equal(BatteryGrade.Unknown, reading.Grade);
    }

    [Theory]
    [InlineData(100, BatteryGrade.Good)]
    [InlineData(80, BatteryGrade.Good)]
    [InlineData(79.9, BatteryGrade.Fair)]
    [InlineData(60, BatteryGrade.Fair)]
    [InlineData(59.9, BatteryGrade.Worn)]
    [InlineData(1, BatteryGrade.Worn)]
    public void TheGradeChangesAtEightyAndSixty(double percent, BatteryGrade expected) =>
        Assert.Equal(expected, BatteryReading.GradeFor(percent));

    [Fact]
    public void NoPercentIsNoGrade() => Assert.Equal(BatteryGrade.Unknown, BatteryReading.GradeFor(null));

    [Fact]
    public void WearIsWhatHasBeenLost()
    {
        var reading = Battery(50_000, 40_000);
        Assert.Equal(10_000, reading.WornCapacity);
        Assert.Equal(20, reading.WearPercent!.Value, 6);
        Assert.Equal(BatteryGrade.Good, reading.Grade);
    }

    [Fact]
    public void TwoBatteriesAreAddedTogetherNotAveraged()
    {
        // 10 of 10 and 20 of 40 is 30 of 50, not the average of 100% and 50%.
        var combined = BatteryReading.CombinedPercent([Battery(10_000, 10_000), Battery(40_000, 20_000)]);
        Assert.Equal(60, combined!.Value, 6);
    }

    [Fact]
    public void ABatteryCountingInItsOwnUnitsIsNotAddedToOthers()
    {
        var combined = BatteryReading.CombinedPercent([Battery(50_000, 40_000), Battery(100, 10, relative: true)]);
        Assert.Equal(80, combined!.Value, 6);
    }

    [Fact]
    public void ABatteryCountingInItsOwnUnitsStillGivesItsRatioWhenAlone() =>
        Assert.Equal(50, BatteryReading.CombinedPercent([Battery(200, 100, relative: true)])!.Value, 6);

    [Fact]
    public void NothingToCompareGivesNoCombinedPercent()
    {
        Assert.Null(BatteryReading.CombinedPercent([]));
        Assert.Null(BatteryReading.CombinedPercent([Battery(0, 0)]));
    }

    [Theory]
    [InlineData("LION", "Lithium-ion")]
    [InlineData("lion", "Lithium-ion")]
    [InlineData("LiP", "Lithium polymer")]
    [InlineData("NiMH", "Nickel-metal hydride")]
    [InlineData("PbAc", "Lead acid")]
    [InlineData("", "")]
    [InlineData("XYZ", "XYZ")]
    public void ChemistryCodesAreSaidInWords(string code, string expected) =>
        Assert.Equal(expected, WindowsBatteryReader.ChemistryName(code));
}

/// <summary>Reads this PC's batteries without changing anything. It passes on a desktop, which has none.</summary>
[SupportedOSPlatform("windows")]
public sealed class BatteryHealthLiveTests
{
    [Fact]
    public void TheRealReaderAnswersOrFindsNoBattery()
    {
        var readings = new WindowsBatteryReader().Read();
        foreach (var reading in readings)
        {
            // Whatever the battery says, nothing it says can make a number outside the range.
            if (reading.HealthPercent is { } percent) Assert.InRange(percent, 0, 100);
            Assert.True(reading.DesignCapacity >= 0 && reading.FullChargeCapacity >= 0);
        }
    }

    [Fact]
    public void ThereIsABatteryWhenWindowsSaysThereIsOne()
    {
        // The desktop check and the reader agree, so the card never offers a check on a PC that has nothing to read.
        if (!PowerSource.HasBattery()) return;
        Assert.NotEmpty(new WindowsBatteryReader().Read());
    }
}
