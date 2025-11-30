using System;
using System.Threading.Tasks;
using TidyWindow.App.Services;
using Xunit;

namespace TidyWindow.App.Tests;

public sealed class SystemRestoreGuardServiceTests
{
    [Fact]
    public async Task CheckAsync_ReturnsUnsatisfied_WhenNoCheckpointExists()
    {
        var service = new SystemRestoreGuardService(_ => Task.FromResult<DateTimeOffset?>(null));

        var result = await service.CheckAsync(TimeSpan.FromHours(12));

        Assert.False(result.IsSatisfied);
        Assert.Null(result.LatestRestorePointUtc);
        Assert.Equal("No System Restore checkpoints were found.", result.ErrorMessage);
    }

    [Fact]
    public async Task CheckAsync_ReturnsSatisfied_WhenCheckpointIsFresh()
    {
        var timestamp = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromHours(1));
        var service = new SystemRestoreGuardService(_ => Task.FromResult<DateTimeOffset?>(timestamp));

        var result = await service.CheckAsync(TimeSpan.FromHours(24));

        Assert.True(result.IsSatisfied);
        Assert.Equal(timestamp, result.LatestRestorePointUtc);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task CheckAsync_ConvertsRecoverableExceptionsToFailure()
    {
        var service = new SystemRestoreGuardService(_ => throw new InvalidOperationException("WMI unavailable"));

        var result = await service.CheckAsync(TimeSpan.FromMinutes(5));

        Assert.False(result.IsSatisfied);
        Assert.Equal("WMI unavailable", result.ErrorMessage);
    }

    [Fact]
    public void RequestPrompt_NotifiesSubscribersAndStoresPendingPrompt()
    {
        var service = new SystemRestoreGuardService(_ => Task.FromResult<DateTimeOffset?>(DateTimeOffset.UtcNow));
        SystemRestoreGuardPrompt? observedPrompt = null;
        service.PromptRequested += (_, args) => observedPrompt = args.Prompt;

        var prompt = new SystemRestoreGuardPrompt("Registry optimizer", "Need checkpoint", "Create a restore point.");
        service.RequestPrompt(prompt);

        Assert.Same(prompt, observedPrompt);
        Assert.True(service.TryConsumePendingPrompt(out var pending));
        Assert.Same(prompt, pending);
        Assert.False(service.TryConsumePendingPrompt(out _));
    }
}
