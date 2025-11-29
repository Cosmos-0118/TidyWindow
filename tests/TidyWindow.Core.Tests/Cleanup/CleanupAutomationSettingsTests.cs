using System;
using TidyWindow.Core.Cleanup;
using Xunit;

namespace TidyWindow.Core.Tests.Cleanup;

public sealed class CleanupAutomationSettingsTests
{
    [Fact]
    public void Normalize_WhenIntervalBelowMinimum_ClampsToMinimum()
    {
        var settings = new CleanupAutomationSettings(
            automationEnabled: true,
            intervalMinutes: 10,
            deletionMode: CleanupAutomationDeletionMode.SkipLocked,
            includeDownloads: true,
            includeBrowserHistory: true,
            topItemCount: CleanupAutomationSettings.Default.TopItemCount,
            lastRunUtc: null);

        var normalized = settings.Normalize();

        Assert.Equal(CleanupAutomationSettings.MinimumIntervalMinutes, normalized.IntervalMinutes);
    }

    [Fact]
    public void Normalize_WhenIntervalAboveMaximum_ClampsToMaximum()
    {
        var settings = new CleanupAutomationSettings(
            automationEnabled: true,
            intervalMinutes: CleanupAutomationSettings.MaximumIntervalMinutes * 2,
            deletionMode: CleanupAutomationDeletionMode.ForceDelete,
            includeDownloads: false,
            includeBrowserHistory: false,
            topItemCount: CleanupAutomationSettings.Default.TopItemCount,
            lastRunUtc: DateTimeOffset.UtcNow);

        var normalized = settings.Normalize();

        Assert.Equal(CleanupAutomationSettings.MaximumIntervalMinutes, normalized.IntervalMinutes);
    }

    [Fact]
    public void WithInterval_WhenZero_UsesDefaultInterval()
    {
        var settings = CleanupAutomationSettings.Default.WithInterval(0);

        Assert.Equal(CleanupAutomationSettings.Default.IntervalMinutes, settings.IntervalMinutes);
    }

    [Fact]
    public void Normalize_WhenTopItemCountBelowMinimum_ClampsToMinimum()
    {
        var settings = new CleanupAutomationSettings(
            automationEnabled: true,
            intervalMinutes: CleanupAutomationSettings.Default.IntervalMinutes,
            deletionMode: CleanupAutomationDeletionMode.SkipLocked,
            includeDownloads: true,
            includeBrowserHistory: false,
            topItemCount: CleanupAutomationSettings.MinimumTopItemCount - 25,
            lastRunUtc: null);

        var normalized = settings.Normalize();

        Assert.Equal(CleanupAutomationSettings.MinimumTopItemCount, normalized.TopItemCount);
    }

    [Fact]
    public void Normalize_WhenTopItemCountAboveMaximum_ClampsToMaximum()
    {
        var settings = new CleanupAutomationSettings(
            automationEnabled: false,
            intervalMinutes: CleanupAutomationSettings.Default.IntervalMinutes,
            deletionMode: CleanupAutomationDeletionMode.MoveToRecycleBin,
            includeDownloads: true,
            includeBrowserHistory: false,
            topItemCount: CleanupAutomationSettings.MaximumTopItemCount + 100,
            lastRunUtc: null);

        var normalized = settings.Normalize();

        Assert.Equal(CleanupAutomationSettings.MaximumTopItemCount, normalized.TopItemCount);
    }

    [Fact]
    public void WithTopItemCount_WhenZero_UsesDefault()
    {
        var settings = CleanupAutomationSettings.Default.WithTopItemCount(0);

        Assert.Equal(CleanupAutomationSettings.Default.TopItemCount, settings.TopItemCount);
    }
}
