using System;
using TidyWindow.Core.Automation;
using Xunit;

namespace TidyWindow.Core.Tests.Automation;

public sealed class JsonPayloadExtractorTests
{
    [Fact]
    public void ExtractLastJsonBlock_ReturnsNull_WhenNoLines()
    {
        var result = JsonPayloadExtractor.ExtractLastJsonBlock(Array.Empty<string>());
        Assert.Null(result);
    }

    [Fact]
    public void ExtractLastJsonBlock_ReturnsLastBlock_WhenMultiplePayloadsExist()
    {
        var lines = new[]
        {
            "{ \"id\": 1 }",
            "noise",
            "{ \"id\": 2 }"
        };

        var result = JsonPayloadExtractor.ExtractLastJsonBlock(lines);
        Assert.Equal("{ \"id\": 2 }", result);
    }

    [Fact]
    public void ExtractLastJsonBlock_PreservesMultiLinePayload()
    {
        var lines = new[]
        {
            "INFO start",
            "{",
            "  \"name\": \"alpha\",",
            "  \"items\": [",
            "    { \"id\": 1 }",
            "  ]",
            "}",
            "completed"
        };

        var expected = string.Join(Environment.NewLine, new[]
        {
            "{",
            "  \"name\": \"alpha\",",
            "  \"items\": [",
            "    { \"id\": 1 }",
            "  ]",
            "}"
        });

        var result = JsonPayloadExtractor.ExtractLastJsonBlock(lines);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ExtractLastJsonBlock_StripsBomAndLeadingWhitespace()
    {
        var lines = new[]
        {
            "  info",
            "\uFEFF    [1, 2, 3]"
        };

        var result = JsonPayloadExtractor.ExtractLastJsonBlock(lines);
        Assert.Equal("[1, 2, 3]", result);
    }
}
