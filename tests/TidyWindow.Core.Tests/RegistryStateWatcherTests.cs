using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TidyWindow.Core.Automation;
using TidyWindow.Core.Maintenance;
using Xunit;

namespace TidyWindow.Core.Tests;

public sealed class RegistryStateWatcherTests
{
    [Fact]
    public async Task WatchAsync_StreamsSuccessAndFailure()
    {
        var invoker = new PowerShellInvoker();
        var optimizer = new RegistryOptimizerService(invoker);
        var stateService = new RegistryStateService(invoker, optimizer);
        var watcher = new RegistryStateWatcher(stateService);

        var updates = new List<RegistryStateUpdate>();

        await foreach (var update in watcher.WatchAsync(new[] { "menu-show-delay", "unknown-tweak" }, forceRefresh: true))
        {
            updates.Add(update);
        }

        Assert.Equal(2, updates.Count);

        var success = Assert.Single(updates.Where(static u => u.IsSuccess));
        Assert.Equal("menu-show-delay", success.TweakId);
        Assert.NotNull(success.State);
        Assert.Equal("menu-show-delay", success.State!.TweakId);

        var failure = Assert.Single(updates.Where(static u => !u.IsSuccess));
        Assert.Equal("unknown-tweak", failure.TweakId);
        Assert.Null(failure.State);
        Assert.False(string.IsNullOrWhiteSpace(failure.ErrorMessage));
        Assert.IsType<InvalidOperationException>(failure.Exception);
    }
}
