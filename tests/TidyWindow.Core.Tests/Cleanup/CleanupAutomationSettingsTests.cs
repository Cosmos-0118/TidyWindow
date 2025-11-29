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
}
