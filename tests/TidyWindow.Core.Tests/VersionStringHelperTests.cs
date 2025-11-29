using TidyWindow.Core.Maintenance;
using Xunit;

namespace TidyWindow.Core.Tests;

public sealed class VersionStringHelperTests
{
    [Theory]
    [InlineData("< 3.12.10", "3.12.10")]
    [InlineData("3.12.10 (64-bit)", "3.12.10")]
    [InlineData("3_12_10", "3.12.10")]
    [InlineData("1.2.3.4.5", "1.2.3.4")]
    public void Normalize_ExtractsNumericVersion(string input, string expected)
    {
        var normalized = VersionStringHelper.Normalize(input);
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void Normalize_UnknownReturnsNull()
    {
        Assert.Null(VersionStringHelper.Normalize("Unknown"));
    }
}
