using System;
using System.IO;
using TidyWindow.App.Services;
using TidyWindow.Core.Cleanup;
using Xunit;

namespace TidyWindow.App.Tests;

public sealed class CleanupAutomationSettingsStoreTests : IDisposable
{
    private readonly string _root;

    public CleanupAutomationSettingsStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "TidyWindowTests", "CleanupAutomation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Get_WhenMissing_ReturnsDefaultSettings()
    {
        var store = CreateStore(out _);

        var settings = store.Get();

        Assert.Equal(CleanupAutomationSettings.Default.AutomationEnabled, settings.AutomationEnabled);
        Assert.Equal(CleanupAutomationSettings.Default.IntervalMinutes, settings.IntervalMinutes);
        Assert.Equal(CleanupAutomationSettings.Default.DeletionMode, settings.DeletionMode);
        Assert.Equal(CleanupAutomationSettings.Default.IncludeDownloads, settings.IncludeDownloads);
        Assert.Equal(CleanupAutomationSettings.Default.IncludeBrowserHistory, settings.IncludeBrowserHistory);
        Assert.Equal(CleanupAutomationSettings.Default.TopItemCount, settings.TopItemCount);
        Assert.Null(settings.LastRunUtc);
    }

    [Fact]
    public void Save_ThenGet_RoundTripsValues()
    {
        var store = CreateStore(out var directory);
        var input = new CleanupAutomationSettings(
            automationEnabled: true,
            intervalMinutes: 360,
            deletionMode: CleanupAutomationDeletionMode.MoveToRecycleBin,
            includeDownloads: false,
            includeBrowserHistory: true,
            topItemCount: CleanupAutomationSettings.MinimumTopItemCount,
            lastRunUtc: DateTimeOffset.UtcNow);

        store.Save(input);
        var persisted = store.Get();

        Assert.True(persisted.AutomationEnabled);
        Assert.Equal(360, persisted.IntervalMinutes);
        Assert.Equal(CleanupAutomationDeletionMode.MoveToRecycleBin, persisted.DeletionMode);
        Assert.False(persisted.IncludeDownloads);
        Assert.True(persisted.IncludeBrowserHistory);
        Assert.Equal(CleanupAutomationSettings.MinimumTopItemCount, persisted.TopItemCount);
        Assert.Equal(input.LastRunUtc!.Value.ToUniversalTime(), persisted.LastRunUtc!.Value.ToUniversalTime());

        Assert.True(File.Exists(Path.Combine(directory, "cleanup-automation.json")));
    }

    private CleanupAutomationSettingsStore CreateStore(out string directory)
    {
        directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return new CleanupAutomationSettingsStore(directory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }
}
