using System;
using System.IO;
using System.Linq;
using TidyWindow.Core.Processes;
using Xunit;

namespace TidyWindow.Core.Tests.Processes;

public sealed class ProcessStateStoreTests
{
    [Fact]
    public void UpsertPreference_RoundTripsToDisk()
    {
        var path = CreateTempPath();
        try
        {
            var store = new ProcessStateStore(path);
            var preference = new ProcessPreference(
                "GameBar",
                ProcessActionPreference.AutoStop,
                ProcessPreferenceSource.UserOverride,
                DateTimeOffset.UtcNow,
                "Disable overlay");

            store.UpsertPreference(preference);

            var reloaded = new ProcessStateStore(path);
            Assert.True(reloaded.TryGetPreference("gamebar", out var persisted));
            Assert.Equal(ProcessActionPreference.AutoStop, persisted.Action);
            Assert.Equal(ProcessPreferenceSource.UserOverride, persisted.Source);
        }
        finally
        {
            DeleteIfExists(path);
        }
    }

    [Fact]
    public void RecordSuspiciousHit_RoundTripsToDisk()
    {
        var path = CreateTempPath();
        try
        {
            var store = new ProcessStateStore(path);
            var hit = new SuspiciousProcessHit(
                Guid.NewGuid().ToString("N"),
                "svchost.exe",
                "C:/Windows/System32/svchost.exe",
                SuspicionLevel.Orange,
                new[] { "svchost outside Services" },
                DateTimeOffset.UtcNow,
                "abc123",
                "unit-test",
                "demo");

            store.RecordSuspiciousHit(hit);

            var reloaded = new ProcessStateStore(path);
            var hits = reloaded.GetSuspiciousHits();
            Assert.Single(hits);
            Assert.Equal(SuspicionLevel.Orange, hits.Single().Level);
            Assert.Equal("abc123", hits.Single().Hash);
        }
        finally
        {
            DeleteIfExists(path);
        }
    }

    private static string CreateTempPath()
    {
        return Path.Combine(Path.GetTempPath(), $"TidyWindow_State_{Guid.NewGuid():N}.json");
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
