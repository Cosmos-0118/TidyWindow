using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels;
using Xunit;

namespace TidyWindow.App.Tests.LatencyGuard;

public class LatencyGuardViewModelTests
{
    // ─────────────────────────────────────────────────────────────────────────────
    // Test Doubles
    // ─────────────────────────────────────────────────────────────────────────────

    private sealed class FakeLatencyGuardSampler : ILatencyGuardSampler
    {
        public int SampleCallCount { get; private set; }

        public double FakeGpuUtil { get; set; } = 45.5;
        public double FakeDpcUtil { get; set; } = 1.2;
        public List<LatencyGuardSampler.GpuProcessSample> FakeProcesses { get; set; } = new()
        {
            new LatencyGuardSampler.GpuProcessSample(1234, "ollama", 30.0),
            new LatencyGuardSampler.GpuProcessSample(5678, "chrome", 15.5)
        };

        public Task<LatencyGuardSampler.LatencySample> SampleAsync()
        {
            SampleCallCount++;
            return Task.FromResult(new LatencyGuardSampler.LatencySample(FakeGpuUtil, FakeDpcUtil, FakeProcesses));
        }
    }

    private sealed class FakeLatencyGuardProfileService : ILatencyGuardProfileService
    {
        public int ApplyCallCount { get; private set; }
        public int RevertCallCount { get; private set; }
        public int ToggleHagsCallCount { get; private set; }
        public int SetOllamaConfigCallCount { get; private set; }

        public bool? FakeHagsEnabled { get; set; } = true;
        public bool FakeOllamaRunning { get; set; } = false;
        public OllamaConfig FakeOllamaConfig { get; set; } = new OllamaConfig(null, null);

        private LatencyGuardProfileState _state = LatencyGuardProfileState.Inactive;

        public Task<LatencyGuardProfileState> GetStateAsync()
        {
            return Task.FromResult(_state);
        }

        public Task<LatencyGuardProfileState> ApplyAsync(bool trimEffects, bool trimRefreshRate, bool throttleModels)
        {
            ApplyCallCount++;
            _state = new LatencyGuardProfileState(
                IsApplied: true,
                EffectsTrimmed: trimEffects,
                RefreshTrimmed: trimRefreshRate,
                ModelThrottleActive: throttleModels,
                AppliedAt: DateTimeOffset.UtcNow);
            return Task.FromResult(_state);
        }

        public Task<LatencyGuardProfileState> RevertAsync()
        {
            RevertCallCount++;
            _state = LatencyGuardProfileState.Inactive;
            return Task.FromResult(_state);
        }

        public bool? IsHagsEnabled()
        {
            return FakeHagsEnabled;
        }

        public bool SetHags(bool enabled)
        {
            ToggleHagsCallCount++;
            FakeHagsEnabled = enabled;
            return true;
        }

        public OllamaConfig GetOllamaConfig()
        {
            return FakeOllamaConfig;
        }

        public void SetOllamaConfig(int? numCtx, int? numParallel)
        {
            SetOllamaConfigCallCount++;
            FakeOllamaConfig = new OllamaConfig(numCtx, numParallel);
        }

        public bool IsOllamaRunning()
        {
            return FakeOllamaRunning;
        }

        public void RestartOllama()
        {
            // No-op for tests
        }
    }

    private static LatencyGuardViewModel CreateViewModel(
        FakeLatencyGuardSampler? sampler = null,
        FakeLatencyGuardProfileService? profileService = null,
        ActivityLogService? activityLog = null)
    {
        return new LatencyGuardViewModel(
            sampler ?? new FakeLatencyGuardSampler(),
            profileService ?? new FakeLatencyGuardProfileService(),
            activityLog ?? new ActivityLogService());
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Initial State Tests
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_SetsDefaultProperties()
    {
        var vm = CreateViewModel();

        Assert.NotNull(vm.HeroHeadline);
        Assert.NotNull(vm.HeroBody);
        Assert.NotNull(vm.RefreshCommand);
        Assert.NotNull(vm.ApplyProfileCommand);
        Assert.NotNull(vm.RevertProfileCommand);
        Assert.NotNull(vm.ToggleHagsCommand);
        Assert.NotNull(vm.ApplyOllamaConfigCommand);
    }

    [Fact]
    public void Constructor_DefaultTrimOptions()
    {
        var vm = CreateViewModel();

        Assert.True(vm.TrimEffects);
        Assert.True(vm.TrimRefreshRate);
        Assert.False(vm.ThrottleModels);
    }

    [Fact]
    public void Constructor_DefaultOllamaConfig()
    {
        var vm = CreateViewModel();

        Assert.Equal("2048", vm.OllamaNumCtxText);
        Assert.Equal("1", vm.OllamaNumParallelText);
    }

    [Fact]
    public void Constructor_TopProcesses_IsEmpty()
    {
        var vm = CreateViewModel();

        Assert.NotNull(vm.TopProcesses);
        Assert.Empty(vm.TopProcesses);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Property Binding Tests
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TrimEffects_CanBeToggled()
    {
        var vm = CreateViewModel();

        vm.TrimEffects = false;

        Assert.False(vm.TrimEffects);
    }

    [Fact]
    public void TrimRefreshRate_CanBeToggled()
    {
        var vm = CreateViewModel();

        vm.TrimRefreshRate = false;

        Assert.False(vm.TrimRefreshRate);
    }

    [Fact]
    public void ThrottleModels_CanBeToggled()
    {
        var vm = CreateViewModel();

        vm.ThrottleModels = true;

        Assert.True(vm.ThrottleModels);
    }

    [Fact]
    public void OllamaNumCtxText_CanBeSet()
    {
        var vm = CreateViewModel();

        vm.OllamaNumCtxText = "4096";

        Assert.Equal("4096", vm.OllamaNumCtxText);
    }

    [Fact]
    public void OllamaNumParallelText_CanBeSet()
    {
        var vm = CreateViewModel();

        vm.OllamaNumParallelText = "2";

        Assert.Equal("2", vm.OllamaNumParallelText);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Hero Content Tests
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HeroHeadline_ContainsAudio()
    {
        var vm = CreateViewModel();

        Assert.Contains("audio", vm.HeroHeadline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HeroBody_ContainsOllama()
    {
        var vm = CreateViewModel();

        Assert.Contains("Ollama", vm.HeroBody);
    }

    [Fact]
    public void QuickMitigations_HasItems()
    {
        var vm = CreateViewModel();

        Assert.NotEmpty(vm.QuickMitigations);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Null Argument Tests
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullSampler_ThrowsArgumentNullException()
    {
        var profileService = new FakeLatencyGuardProfileService();
        var activityLog = new ActivityLogService();

        Assert.Throws<ArgumentNullException>(() =>
            new LatencyGuardViewModel(null!, profileService, activityLog));
    }

    [Fact]
    public void Constructor_NullProfileService_ThrowsArgumentNullException()
    {
        var sampler = new FakeLatencyGuardSampler();
        var activityLog = new ActivityLogService();

        Assert.Throws<ArgumentNullException>(() =>
            new LatencyGuardViewModel(sampler, null!, activityLog));
    }

    [Fact]
    public void Constructor_NullActivityLogService_ThrowsArgumentNullException()
    {
        var sampler = new FakeLatencyGuardSampler();
        var profileService = new FakeLatencyGuardProfileService();

        Assert.Throws<ArgumentNullException>(() =>
            new LatencyGuardViewModel(sampler, profileService, null!));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Profile State Tests
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IsProfileActive_InitiallyFalse()
    {
        var vm = CreateViewModel();

        Assert.False(vm.IsProfileActive);
    }

    [Fact]
    public void ProfileStatus_InitiallyNotApplied()
    {
        var vm = CreateViewModel();

        Assert.Contains("not applied", vm.ProfileStatus, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Command Tests
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Commands_AreNotNull()
    {
        var vm = CreateViewModel();

        Assert.NotNull(vm.RefreshCommand);
        Assert.NotNull(vm.ApplyProfileCommand);
        Assert.NotNull(vm.RevertProfileCommand);
        Assert.NotNull(vm.ToggleHagsCommand);
        Assert.NotNull(vm.ApplyOllamaConfigCommand);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // HAGS Tests
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HagsStatus_IsNotEmpty()
    {
        var vm = CreateViewModel();

        Assert.False(string.IsNullOrWhiteSpace(vm.HagsStatus));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // StatusNote Tests
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StatusNote_IsNotEmpty()
    {
        var vm = CreateViewModel();

        Assert.False(string.IsNullOrWhiteSpace(vm.StatusNote));
    }

    [Fact]
    public void StatusNote_ContainsLiveSampling()
    {
        var vm = CreateViewModel();

        Assert.Contains("Live", vm.StatusNote, StringComparison.OrdinalIgnoreCase);
    }
}
