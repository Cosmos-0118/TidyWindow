using System;
using System.Collections.Generic;
using TidyWindow.App.Services;
using Xunit;

namespace TidyWindow.App.Tests;

public sealed class AppAutoStartServiceTests
{
    [Fact]
    public void TrySetEnabled_CreatesScheduledTask_WhenCommandSucceeds()
    {
        var runner = new FakeProcessRunner();
        runner.Results.Enqueue(new ProcessRunResult(0, "SUCCESS", string.Empty));
        var service = new AppAutoStartService(runner);

        var success = service.TrySetEnabled(enabled: true, out var error);

        Assert.True(success);
        Assert.Null(error);
        var call = Assert.Single(runner.Calls);
        Assert.Equal("schtasks.exe", call.FileName, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("/Create", call.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\\TidyWindow\\TidyWindowElevatedStartup", call.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--minimized", call.Arguments, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TrySetEnabled_Disable_IgnoresMissingTaskErrors()
    {
        var runner = new FakeProcessRunner();
        runner.Results.Enqueue(new ProcessRunResult(1, string.Empty, "ERROR: The system cannot find the file specified."));
        var service = new AppAutoStartService(runner);

        var success = service.TrySetEnabled(enabled: false, out var error);

        Assert.True(success);
        Assert.Null(error);
        var call = Assert.Single(runner.Calls);
        Assert.Contains("/Delete", call.Arguments, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TrySetEnabled_ReturnsFalse_WhenSchtasksFails()
    {
        var runner = new FakeProcessRunner();
        runner.Results.Enqueue(new ProcessRunResult(2, string.Empty, "Access is denied."));
        var service = new AppAutoStartService(runner);

        var success = service.TrySetEnabled(enabled: true, out var error);

        Assert.False(success);
        Assert.Contains("Access is denied", error, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public Queue<ProcessRunResult> Results { get; } = new();
        public List<(string FileName, string Arguments)> Calls { get; } = new();

        public ProcessRunResult Run(string fileName, string arguments)
        {
            Calls.Add((fileName, arguments));
            return Results.Count > 0
                ? Results.Dequeue()
                : new ProcessRunResult(0, string.Empty, string.Empty);
        }
    }
}
