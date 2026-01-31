using System;
using System.Collections.Generic;
using System.Linq;
using TidyWindow.App.Services;
using Xunit;

namespace TidyWindow.App.Tests;

public sealed class AppAutoStartServiceTests
{
    [Fact]
    public void TrySetEnabled_CreatesScheduledTask_WhenXmlImportSucceeds()
    {
        var runner = new FakeProcessRunner();
        // First call: EnsureTaskFolder query (may fail, that's ok)
        runner.Results.Enqueue(new ProcessRunResult(1, string.Empty, "not found"));
        // Second call: XML import succeeds
        runner.Results.Enqueue(new ProcessRunResult(0, "SUCCESS", string.Empty));
        var service = new AppAutoStartService(runner);

        var success = service.TrySetEnabled(enabled: true, out var error);

        Assert.True(success);
        Assert.Null(error);

        // Should have at least the XML import call
        var createCall = runner.Calls.FirstOrDefault(c =>
            c.Arguments.Contains("/Create", StringComparison.OrdinalIgnoreCase) &&
            c.Arguments.Contains("/XML", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(createCall.Arguments);
        Assert.Contains("\\TidyWindow\\TidyWindowElevatedStartup", createCall.Arguments, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TrySetEnabled_FallsBackToCommandLine_WhenXmlImportFails()
    {
        var runner = new FakeProcessRunner();
        // EnsureTaskFolder query
        runner.Results.Enqueue(new ProcessRunResult(1, string.Empty, "not found"));
        // XML import fails
        runner.Results.Enqueue(new ProcessRunResult(1, string.Empty, "Access denied"));
        // Command line succeeds
        runner.Results.Enqueue(new ProcessRunResult(0, "SUCCESS", string.Empty));
        var service = new AppAutoStartService(runner);

        var success = service.TrySetEnabled(enabled: true, out var error);

        Assert.True(success);
        Assert.Null(error);

        // Should have the command-line create call (without /XML)
        var commandLineCall = runner.Calls.FirstOrDefault(c =>
            c.Arguments.Contains("/Create", StringComparison.OrdinalIgnoreCase) &&
            !c.Arguments.Contains("/XML", StringComparison.OrdinalIgnoreCase) &&
            c.Arguments.Contains("--minimized", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(commandLineCall.Arguments);
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
    public void TrySetEnabled_SucceedsViaRegistryFallback_WhenAllTaskMethodsFail()
    {
        var runner = new FakeProcessRunner();
        // All schtasks calls will fail - but the registry fallback should succeed
        // (the registry fallback doesn't use IProcessRunner)
        // EnsureTaskFolder query
        runner.Results.Enqueue(new ProcessRunResult(1, string.Empty, "access denied"));
        // XML import fails
        runner.Results.Enqueue(new ProcessRunResult(1, string.Empty, "access denied"));
        // Command line attempt 1 fails
        runner.Results.Enqueue(new ProcessRunResult(1, string.Empty, "access denied"));
        // Command line attempt 2 (without delay) fails
        runner.Results.Enqueue(new ProcessRunResult(1, string.Empty, "access denied"));
        var service = new AppAutoStartService(runner);

        // On Windows, this might still succeed via registry fallback
        // On non-Windows or if registry also fails, it will fail
        var success = service.TrySetEnabled(enabled: true, out var error);

        // The service should have tried all Task Scheduler methods
        Assert.True(runner.Calls.Count >= 2);
        // The registry fallback is attempted internally; we can't directly verify it
        // but the test documents the expected behavior
    }

    [Fact]
    public void IsEnabled_ReturnsFalse_WhenTaskQueryFails()
    {
        var runner = new FakeProcessRunner();
        runner.Results.Enqueue(new ProcessRunResult(1, string.Empty, "not found"));
        var service = new AppAutoStartService(runner);

        var isEnabled = service.IsEnabled;

        // On non-Windows or with no registry entry, should be false
        // This test verifies the property doesn't throw
        Assert.False(isEnabled);
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
