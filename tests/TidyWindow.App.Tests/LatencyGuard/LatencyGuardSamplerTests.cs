using System;
using System.Linq;
using System.Threading.Tasks;
using TidyWindow.App.Services;
using Xunit;

namespace TidyWindow.App.Tests.LatencyGuard;

public class LatencyGuardSamplerTests
{
    // ─────────────────────────────────────────────────────────────────────────────
    // Basic Sampling Tests
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SampleAsync_ReturnsValidSample()
    {
        var sampler = new LatencyGuardSampler();

        var sample = await sampler.SampleAsync();

        Assert.NotNull(sample);
        Assert.True(sample.GpuUtilPercent >= 0);
        Assert.True(sample.GpuUtilPercent <= 100);
        Assert.True(sample.DpcUtilPercent >= 0);
        Assert.NotNull(sample.TopProcesses);
    }

    [Fact]
    public async Task SampleAsync_GpuUtilPercent_IsCappedAt100()
    {
        var sampler = new LatencyGuardSampler();

        var sample = await sampler.SampleAsync();

        Assert.True(sample.GpuUtilPercent <= 100, $"GPU util was {sample.GpuUtilPercent}, expected <= 100");
    }

    [Fact]
    public async Task SampleAsync_DpcUtilPercent_IsNonNegative()
    {
        var sampler = new LatencyGuardSampler();

        var sample = await sampler.SampleAsync();

        Assert.True(sample.DpcUtilPercent >= 0, $"DPC util was {sample.DpcUtilPercent}, expected >= 0");
    }

    [Fact]
    public async Task SampleAsync_TopProcesses_HasAtMostFiveEntries()
    {
        var sampler = new LatencyGuardSampler();

        var sample = await sampler.SampleAsync();

        Assert.True(sample.TopProcesses.Count <= 5, $"Top processes count was {sample.TopProcesses.Count}, expected <= 5");
    }

    [Fact]
    public async Task SampleAsync_TopProcesses_AreOrderedByUtilization()
    {
        var sampler = new LatencyGuardSampler();

        var sample = await sampler.SampleAsync();

        if (sample.TopProcesses.Count > 1)
        {
            for (int i = 0; i < sample.TopProcesses.Count - 1; i++)
            {
                Assert.True(
                    sample.TopProcesses[i].UtilizationPercent >= sample.TopProcesses[i + 1].UtilizationPercent,
                    "Top processes should be ordered by utilization descending");
            }
        }
    }

    [Fact]
    public async Task SampleAsync_TopProcesses_HaveValidPids()
    {
        var sampler = new LatencyGuardSampler();

        var sample = await sampler.SampleAsync();

        foreach (var proc in sample.TopProcesses)
        {
            Assert.True(proc.Pid > 0, $"Process PID should be > 0, was {proc.Pid}");
        }
    }

    [Fact]
    public async Task SampleAsync_TopProcesses_HaveNonEmptyNames()
    {
        var sampler = new LatencyGuardSampler();

        var sample = await sampler.SampleAsync();

        foreach (var proc in sample.TopProcesses)
        {
            Assert.False(string.IsNullOrEmpty(proc.Name), "Process name should not be empty");
        }
    }

    [Fact]
    public async Task SampleAsync_TopProcesses_UtilizationIsRounded()
    {
        var sampler = new LatencyGuardSampler();

        var sample = await sampler.SampleAsync();

        foreach (var proc in sample.TopProcesses)
        {
            // Check that utilization is rounded to 1 decimal place
            var rounded = Math.Round(proc.UtilizationPercent, 1);
            Assert.Equal(rounded, proc.UtilizationPercent);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Multiple Samples Tests
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SampleAsync_MultipleCalls_AllSucceed()
    {
        var sampler = new LatencyGuardSampler();

        for (int i = 0; i < 3; i++)
        {
            var sample = await sampler.SampleAsync();
            Assert.NotNull(sample);
        }
    }

    [Fact]
    public async Task SampleAsync_ConcurrentCalls_AllSucceed()
    {
        var sampler = new LatencyGuardSampler();

        var tasks = Enumerable.Range(0, 5).Select(_ => sampler.SampleAsync()).ToArray();
        var samples = await Task.WhenAll(tasks);

        Assert.All(samples, sample =>
        {
            Assert.NotNull(sample);
            Assert.True(sample.GpuUtilPercent >= 0);
            Assert.True(sample.DpcUtilPercent >= 0);
        });
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Edge Case Tests
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SampleAsync_WhenNoGpuActivity_ReturnsZeroOrLow()
    {
        var sampler = new LatencyGuardSampler();

        var sample = await sampler.SampleAsync();

        // GPU util should be a valid number (may be 0 or low if idle)
        Assert.True(sample.GpuUtilPercent >= 0);
    }

    [Fact]
    public async Task SampleAsync_ReturnsWithinReasonableTime()
    {
        var sampler = new LatencyGuardSampler();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await sampler.SampleAsync();
        stopwatch.Stop();

        // Should complete within 2 seconds (250ms sampling delay + overhead)
        Assert.True(stopwatch.ElapsedMilliseconds < 2000, $"Sample took {stopwatch.ElapsedMilliseconds}ms, expected < 2000ms");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Record Type Tests
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GpuProcessSample_RecordEquality()
    {
        var sample1 = new LatencyGuardSampler.GpuProcessSample(1234, "test", 50.5);
        var sample2 = new LatencyGuardSampler.GpuProcessSample(1234, "test", 50.5);
        var sample3 = new LatencyGuardSampler.GpuProcessSample(5678, "other", 25.0);

        Assert.Equal(sample1, sample2);
        Assert.NotEqual(sample1, sample3);
    }

    [Fact]
    public void LatencySample_RecordEquality()
    {
        var procs1 = new[] { new LatencyGuardSampler.GpuProcessSample(1, "a", 10) };
        var procs2 = new[] { new LatencyGuardSampler.GpuProcessSample(1, "a", 10) };

        var sample1 = new LatencyGuardSampler.LatencySample(50.0, 2.5, procs1);
        var sample2 = new LatencyGuardSampler.LatencySample(50.0, 2.5, procs2);

        Assert.Equal(sample1.GpuUtilPercent, sample2.GpuUtilPercent);
        Assert.Equal(sample1.DpcUtilPercent, sample2.DpcUtilPercent);
    }

    [Fact]
    public void GpuProcessSample_Properties_AreAccessible()
    {
        var sample = new LatencyGuardSampler.GpuProcessSample(9999, "TestProcess", 75.3);

        Assert.Equal(9999, sample.Pid);
        Assert.Equal("TestProcess", sample.Name);
        Assert.Equal(75.3, sample.UtilizationPercent);
    }
}
