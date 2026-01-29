using System;
using TidyWindow.App.Services;
using Xunit;

namespace TidyWindow.App.Tests.LatencyGuard;

public class OllamaConfigTests
{
    // ─────────────────────────────────────────────────────────────────────────────
    // Record Creation Tests
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OllamaConfig_CanBeCreatedWithNulls()
    {
        var config = new OllamaConfig(null, null);

        Assert.Null(config.NumCtx);
        Assert.Null(config.NumParallel);
    }

    [Fact]
    public void OllamaConfig_CanBeCreatedWithValues()
    {
        var config = new OllamaConfig(2048, 1);

        Assert.Equal(2048, config.NumCtx);
        Assert.Equal(1, config.NumParallel);
    }

    [Fact]
    public void OllamaConfig_CanBeCreatedWithPartialValues()
    {
        var config1 = new OllamaConfig(4096, null);
        var config2 = new OllamaConfig(null, 2);

        Assert.Equal(4096, config1.NumCtx);
        Assert.Null(config1.NumParallel);

        Assert.Null(config2.NumCtx);
        Assert.Equal(2, config2.NumParallel);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Equality Tests
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OllamaConfig_EqualityWithSameValues()
    {
        var config1 = new OllamaConfig(2048, 1);
        var config2 = new OllamaConfig(2048, 1);

        Assert.Equal(config1, config2);
    }

    [Fact]
    public void OllamaConfig_InequalityWithDifferentValues()
    {
        var config1 = new OllamaConfig(2048, 1);
        var config2 = new OllamaConfig(4096, 2);

        Assert.NotEqual(config1, config2);
    }

    [Fact]
    public void OllamaConfig_EqualityWithNulls()
    {
        var config1 = new OllamaConfig(null, null);
        var config2 = new OllamaConfig(null, null);

        Assert.Equal(config1, config2);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Edge Case Values Tests
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OllamaConfig_ZeroValues()
    {
        var config = new OllamaConfig(0, 0);

        Assert.Equal(0, config.NumCtx);
        Assert.Equal(0, config.NumParallel);
    }

    [Fact]
    public void OllamaConfig_NegativeValues()
    {
        var config = new OllamaConfig(-1, -1);

        Assert.Equal(-1, config.NumCtx);
        Assert.Equal(-1, config.NumParallel);
    }

    [Fact]
    public void OllamaConfig_LargeValues()
    {
        var config = new OllamaConfig(131072, 16);

        Assert.Equal(131072, config.NumCtx);
        Assert.Equal(16, config.NumParallel);
    }

    [Fact]
    public void OllamaConfig_MaxIntValues()
    {
        var config = new OllamaConfig(int.MaxValue, int.MaxValue);

        Assert.Equal(int.MaxValue, config.NumCtx);
        Assert.Equal(int.MaxValue, config.NumParallel);
    }
}

public class LatencyGuardProfileStateTests
{
    // ─────────────────────────────────────────────────────────────────────────────
    // Static Inactive State Tests
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Inactive_IsNotApplied()
    {
        var state = LatencyGuardProfileState.Inactive;

        Assert.False(state.IsApplied);
    }

    [Fact]
    public void Inactive_AllFlagsAreFalse()
    {
        var state = LatencyGuardProfileState.Inactive;

        Assert.False(state.EffectsTrimmed);
        Assert.False(state.RefreshTrimmed);
        Assert.False(state.ModelThrottleActive);
    }

    [Fact]
    public void Inactive_AppliedAtIsNull()
    {
        var state = LatencyGuardProfileState.Inactive;

        Assert.Null(state.AppliedAt);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Record Creation Tests
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LatencyGuardProfileState_CanBeCreated()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new LatencyGuardProfileState(
            IsApplied: true,
            EffectsTrimmed: true,
            RefreshTrimmed: false,
            ModelThrottleActive: true,
            AppliedAt: now);

        Assert.True(state.IsApplied);
        Assert.True(state.EffectsTrimmed);
        Assert.False(state.RefreshTrimmed);
        Assert.True(state.ModelThrottleActive);
        Assert.Equal(now, state.AppliedAt);
    }

    [Fact]
    public void LatencyGuardProfileState_WithNullAppliedAt()
    {
        var state = new LatencyGuardProfileState(
            IsApplied: true,
            EffectsTrimmed: true,
            RefreshTrimmed: true,
            ModelThrottleActive: true,
            AppliedAt: null);

        Assert.True(state.IsApplied);
        Assert.Null(state.AppliedAt);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Equality Tests
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LatencyGuardProfileState_EqualityWithSameValues()
    {
        var now = DateTimeOffset.UtcNow;
        var state1 = new LatencyGuardProfileState(true, true, true, true, now);
        var state2 = new LatencyGuardProfileState(true, true, true, true, now);

        Assert.Equal(state1, state2);
    }

    [Fact]
    public void LatencyGuardProfileState_InequalityWithDifferentFlags()
    {
        var now = DateTimeOffset.UtcNow;
        var state1 = new LatencyGuardProfileState(true, true, true, true, now);
        var state2 = new LatencyGuardProfileState(true, false, true, true, now);

        Assert.NotEqual(state1, state2);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // With Expression Tests
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LatencyGuardProfileState_WithExpression_ChangesValue()
    {
        var now = DateTimeOffset.UtcNow;
        var original = new LatencyGuardProfileState(true, true, true, true, now);

        var modified = original with { EffectsTrimmed = false };

        Assert.True(original.EffectsTrimmed);
        Assert.False(modified.EffectsTrimmed);
        Assert.Equal(original.RefreshTrimmed, modified.RefreshTrimmed);
    }

    [Fact]
    public void LatencyGuardProfileState_WithExpression_MultipleChanges()
    {
        var now = DateTimeOffset.UtcNow;
        var original = new LatencyGuardProfileState(true, true, true, true, now);

        var modified = original with
        {
            EffectsTrimmed = false,
            RefreshTrimmed = false,
            ModelThrottleActive = false
        };

        Assert.False(modified.EffectsTrimmed);
        Assert.False(modified.RefreshTrimmed);
        Assert.False(modified.ModelThrottleActive);
        Assert.True(modified.IsApplied);
    }
}
