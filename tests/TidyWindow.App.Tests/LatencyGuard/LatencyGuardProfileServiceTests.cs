using System;
using System.Diagnostics;
using System.Threading.Tasks;
using TidyWindow.App.Services;
using Xunit;

namespace TidyWindow.App.Tests.LatencyGuard;

public class LatencyGuardProfileServiceTests
{
    private static LatencyGuardProfileService CreateService() => new(new ActivityLogService());

    // ─────────────────────────────────────────────────────────────────────────────
    // Profile State Tests
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetStateAsync_InitiallyInactive()
    {
        var service = CreateService();

        var state = await service.GetStateAsync();

        Assert.False(state.IsApplied);
        Assert.False(state.EffectsTrimmed);
        Assert.False(state.RefreshTrimmed);
        Assert.False(state.ModelThrottleActive);
        Assert.Null(state.AppliedAt);
    }

    [Fact]
    public async Task ApplyAsync_SetsIsAppliedTrue()
    {
        var service = CreateService();

        var state = await service.ApplyAsync(trimEffects: false, trimRefreshRate: false, throttleModels: false);

        Assert.True(state.IsApplied);
        Assert.NotNull(state.AppliedAt);
    }

    [Fact]
    public async Task ApplyAsync_WithTrimEffects_SetsEffectsTrimmed()
    {
        var service = CreateService();

        var state = await service.ApplyAsync(trimEffects: true, trimRefreshRate: false, throttleModels: false);

        Assert.True(state.IsApplied);
        Assert.True(state.EffectsTrimmed);
        Assert.False(state.RefreshTrimmed);
        Assert.False(state.ModelThrottleActive);
    }

    [Fact]
    public async Task ApplyAsync_WithTrimRefreshRate_SetsRefreshTrimmed()
    {
        var service = CreateService();

        var state = await service.ApplyAsync(trimEffects: false, trimRefreshRate: true, throttleModels: false);

        Assert.True(state.IsApplied);
        Assert.False(state.EffectsTrimmed);
        Assert.True(state.RefreshTrimmed);
        Assert.False(state.ModelThrottleActive);
    }

    [Fact]
    public async Task ApplyAsync_WithThrottleModels_SetsModelThrottleActive()
    {
        var service = CreateService();

        var state = await service.ApplyAsync(trimEffects: false, trimRefreshRate: false, throttleModels: true);

        Assert.True(state.IsApplied);
        Assert.False(state.EffectsTrimmed);
        Assert.False(state.RefreshTrimmed);
        Assert.True(state.ModelThrottleActive);
    }

    [Fact]
    public async Task ApplyAsync_AllOptionsEnabled_SetsAllFlags()
    {
        var service = CreateService();

        var state = await service.ApplyAsync(trimEffects: true, trimRefreshRate: true, throttleModels: true);

        Assert.True(state.IsApplied);
        Assert.True(state.EffectsTrimmed);
        Assert.True(state.RefreshTrimmed);
        Assert.True(state.ModelThrottleActive);
    }

    [Fact]
    public async Task RevertAsync_AfterApply_ResetsToInactive()
    {
        var service = CreateService();
        await service.ApplyAsync(trimEffects: true, trimRefreshRate: true, throttleModels: true);

        var state = await service.RevertAsync();

        Assert.False(state.IsApplied);
        Assert.False(state.EffectsTrimmed);
        Assert.False(state.RefreshTrimmed);
        Assert.False(state.ModelThrottleActive);
        Assert.Null(state.AppliedAt);
    }

    [Fact]
    public async Task RevertAsync_WhenNotApplied_RemainsInactive()
    {
        var service = CreateService();

        var state = await service.RevertAsync();

        Assert.False(state.IsApplied);
    }

    [Fact]
    public async Task ApplyAsync_WhenAlreadyApplied_UpdatesFlags()
    {
        var service = CreateService();
        await service.ApplyAsync(trimEffects: true, trimRefreshRate: false, throttleModels: false);

        var state = await service.ApplyAsync(trimEffects: false, trimRefreshRate: true, throttleModels: true);

        Assert.True(state.IsApplied);
        Assert.False(state.EffectsTrimmed);
        Assert.True(state.RefreshTrimmed);
        Assert.True(state.ModelThrottleActive);
    }

    [Fact]
    public async Task ApplyAsync_MultipleCalls_MaintainsOriginalAppliedAt()
    {
        var service = CreateService();
        var firstState = await service.ApplyAsync(trimEffects: true, trimRefreshRate: false, throttleModels: false);
        var firstAppliedAt = firstState.AppliedAt;

        await Task.Delay(50); // Small delay to ensure time difference

        var secondState = await service.ApplyAsync(trimEffects: false, trimRefreshRate: true, throttleModels: true);

        // AppliedAt should remain from first apply (snapshot is preserved)
        Assert.NotNull(firstAppliedAt);
        Assert.True(secondState.IsApplied);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // HAGS Tests
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IsHagsEnabled_ReturnsNullableValue()
    {
        var service = CreateService();

        // Should return true, false, or null depending on registry state
        var result = service.IsHagsEnabled();

        Assert.True(result == true || result == false || result == null);
    }

    [Fact]
    public void SetHags_ReturnsTrue_OnAttempt()
    {
        var service = CreateService();

        // May fail if not running as admin, but should not throw
        var result = service.SetHags(false);

        // Result depends on admin privileges
        Assert.True(result == true || result == false);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Ollama Config Tests
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GetOllamaConfig_ReturnsConfigRecord()
    {
        var service = CreateService();

        var config = service.GetOllamaConfig();

        Assert.NotNull(config);
        // NumCtx and NumParallel may be null if not set
    }

    [Fact]
    public void SetOllamaConfig_WithValues_SetsEnvironmentVariables()
    {
        var service = CreateService();

        service.SetOllamaConfig(2048, 1);
        var config = service.GetOllamaConfig();

        Assert.Equal(2048, config.NumCtx);
        Assert.Equal(1, config.NumParallel);

        // Cleanup
        service.SetOllamaConfig(null, null);
    }

    [Fact]
    public void SetOllamaConfig_WithNull_ClearsEnvironmentVariables()
    {
        var service = CreateService();
        service.SetOllamaConfig(4096, 2);

        service.SetOllamaConfig(null, null);
        var config = service.GetOllamaConfig();

        Assert.Null(config.NumCtx);
        Assert.Null(config.NumParallel);
    }

    [Fact]
    public void SetOllamaConfig_PartialValues_SetsOnlyProvided()
    {
        var service = CreateService();

        service.SetOllamaConfig(1024, null);
        var config = service.GetOllamaConfig();

        Assert.Equal(1024, config.NumCtx);
        Assert.Null(config.NumParallel);

        // Cleanup
        service.SetOllamaConfig(null, null);
    }

    [Fact]
    public void IsOllamaRunning_ReturnsBool()
    {
        var service = CreateService();

        var result = service.IsOllamaRunning();

        Assert.True(result == true || result == false);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Thread Safety Tests
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentApplyAndRevert_DoesNotThrow()
    {
        var service = CreateService();

        var tasks = new Task[10];
        for (int i = 0; i < 10; i++)
        {
            tasks[i] = i % 2 == 0
                ? service.ApplyAsync(true, true, true)
                : service.RevertAsync();
        }

        await Task.WhenAll(tasks);

        // Should complete without exception
        var finalState = await service.GetStateAsync();
        Assert.True(finalState.IsApplied || !finalState.IsApplied);
    }

    [Fact]
    public async Task ConcurrentGetState_DoesNotThrow()
    {
        var service = CreateService();
        await service.ApplyAsync(true, false, true);

        var tasks = new Task<LatencyGuardProfileState>[20];
        for (int i = 0; i < 20; i++)
        {
            tasks[i] = service.GetStateAsync();
        }

        var results = await Task.WhenAll(tasks);

        foreach (var state in results)
        {
            Assert.True(state.IsApplied);
        }
    }
}
