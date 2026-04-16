using System;
using System.Reflection;
using TidyWindow.Core.Startup;
using Xunit;

namespace TidyWindow.Core.Tests;

public sealed class StartupInventoryServiceTests
{
    private static readonly Type ServiceType = typeof(StartupInventoryService);

    [Theory]
    [InlineData(2, false, true)]
    [InlineData(2, true, true)]
    [InlineData(4, false, false)]
    [InlineData(4, true, true)]
    [InlineData(3, false, false)]
    [InlineData(3, true, false)]
    [InlineData(1, false, false)]
    [InlineData(1, true, false)]
    [InlineData(0, false, false)]
    [InlineData(0, true, false)]
    [InlineData(-1, false, false)]
    [InlineData(-1, true, false)]
    [InlineData(5, false, false)]
    [InlineData(5, true, false)]
    public void ShouldIncludeAutostartService_HandlesAllStartModeEdges(int startValue, bool includeDisabled, bool expected)
    {
        var result = InvokeBool("ShouldIncludeAutostartService", startValue, includeDisabled);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(4, false)]
    [InlineData(3, false)]
    [InlineData(1, false)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(5, false)]
    public void IsServiceEnabledFromStartValue_OnlyAutomaticIsEnabled(int startValue, bool expected)
    {
        var result = InvokeBool("IsServiceEnabledFromStartValue", startValue);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(2, true, "Service (Automatic, Delayed)")]
    [InlineData(2, false, "Service (Automatic)")]
    [InlineData(4, true, "Service (Disabled)")]
    [InlineData(4, false, "Service (Disabled)")]
    [InlineData(3, true, "Service")]
    [InlineData(3, false, "Service")]
    [InlineData(1, false, "Service")]
    [InlineData(0, true, "Service")]
    [InlineData(-1, false, "Service")]
    [InlineData(5, true, "Service")]
    public void GetServiceSourceTag_ReturnsExpectedLabelsAcrossModes(int startValue, bool delayed, string expected)
    {
        var result = InvokeString("GetServiceSourceTag", startValue, delayed);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void DisabledServiceRegression_WhenIncludeDisabled_StaysVisibleAndReenableable()
    {
        var includeDisabled = InvokeBool("ShouldIncludeAutostartService", 4, true);
        var enabledState = InvokeBool("IsServiceEnabledFromStartValue", 4);
        var tag = InvokeString("GetServiceSourceTag", 4, false);

        Assert.True(includeDisabled);
        Assert.False(enabledState);
        Assert.Equal("Service (Disabled)", tag);
    }

    private static bool InvokeBool(string methodName, params object[] args)
    {
        var method = ServiceType.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var value = method!.Invoke(obj: null, parameters: args);
        Assert.IsType<bool>(value);
        return (bool)value;
    }

    private static string InvokeString(string methodName, params object[] args)
    {
        var method = ServiceType.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var value = method!.Invoke(obj: null, parameters: args);
        Assert.IsType<string>(value);
        return (string)value;
    }
}
