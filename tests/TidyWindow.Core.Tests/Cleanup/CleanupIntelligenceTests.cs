using System;
using TidyWindow.Core.Cleanup;
using Xunit;

namespace TidyWindow.Core.Tests.Cleanup;

public static class CleanupIntelligenceTests
{
    [Fact]
    public static void EvaluateFile_ReturnsHighConfidence_ForStaleTempFile()
    {
        var definition = new CleanupTargetDefinition("Temp", "Temp Files", "C:\\Temp", "");
        var staleTimestamp = DateTime.UtcNow.AddDays(-90);
        var context = new CleanupFileContext(
            name: "example.tmp",
            fullPath: "C:\\Temp\\example.tmp",
            extension: ".tmp",
            sizeBytes: 150_000_000,
            lastModifiedUtc: staleTimestamp,
            isHidden: false,
            isSystem: false,
            wasRecentlyModified: false,
            lastAccessUtc: staleTimestamp,
            creationUtc: staleTimestamp);

        var result = CleanupIntelligence.EvaluateFile(definition, context, DateTime.UtcNow);

        Assert.True(result.ShouldInclude);
        Assert.True(result.Confidence >= 0.55);
        Assert.Contains(result.Signals, signal => signal.Contains("Temporary extension", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public static void EvaluateFile_DetectsLowConfidence_ForRecentSystemFile()
    {
        var definition = new CleanupTargetDefinition("Cache", "Cache", "C:\\Cache", "");
        var context = new CleanupFileContext(
            name: "important.dll",
            fullPath: "C:\\Cache\\important.dll",
            extension: ".dll",
            sizeBytes: 25_000_000,
            lastModifiedUtc: DateTime.UtcNow.AddMinutes(-10),
            isHidden: false,
            isSystem: true,
            wasRecentlyModified: true,
            lastAccessUtc: DateTime.UtcNow.AddMinutes(-5),
            creationUtc: DateTime.UtcNow.AddDays(-2));

        var result = CleanupIntelligence.EvaluateFile(definition, context, DateTime.UtcNow);

        Assert.True(result.Confidence <= 0.2);
        Assert.Contains(result.Signals, signal => signal.Contains("system", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public static void EvaluateDirectory_FlagsEmptyDirectory_AsHighConfidence()
    {
        var definition = new CleanupTargetDefinition("Orphaned", "Empty Folder", "C:\\Empty", "");
        var snapshot = new CleanupDirectorySnapshot(
            fullPath: "C:\\Empty",
            name: "Empty",
            sizeBytes: 0,
            lastModifiedUtc: DateTime.UtcNow.AddDays(-45),
            isHidden: false,
            isSystem: false,
            fileCount: 0,
            hiddenFileCount: 0,
            systemFileCount: 0,
            recentFileCount: 0,
            tempFileCount: 0,
            extensionCounts: null);

        var result = CleanupIntelligence.EvaluateDirectory(definition, snapshot, DateTime.UtcNow);

        Assert.True(result.ShouldInclude);
        Assert.True(result.Confidence >= 0.45);
        Assert.Contains(result.Signals, signal => signal.Contains("Empty directory", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public static void EvaluateDirectory_FlagsTempHeavyDirectory()
    {
        var definition = new CleanupTargetDefinition("Cache", "App Cache", "C:\\Cache", "");
        var snapshot = new CleanupDirectorySnapshot(
            fullPath: "C:\\Cache",
            name: "Cache",
            sizeBytes: 300_000_000,
            lastModifiedUtc: DateTime.UtcNow.AddDays(-40),
            isHidden: false,
            isSystem: false,
            fileCount: 20,
            hiddenFileCount: 0,
            systemFileCount: 0,
            recentFileCount: 0,
            tempFileCount: 15,
            extensionCounts: null);

        var result = CleanupIntelligence.EvaluateDirectory(definition, snapshot, DateTime.UtcNow);

        Assert.True(result.ShouldInclude);
        Assert.True(result.Confidence >= 0.4);
        Assert.Contains(result.Signals, signal => signal.Contains("temporary", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public static void EvaluateFile_BoostsConfidence_ForCrashPathHints()
    {
        var reference = DateTime.UtcNow;
        var definition = new CleanupTargetDefinition("Orphaned", "Crash Dumps", "C:\\Users\\user\\AppData\\Local\\CrashDumps", string.Empty);
        var timestamp = reference.AddDays(-14);
        var context = new CleanupFileContext(
            name: "app.exe.12345.dmp",
            fullPath: "C:\\Users\\user\\AppData\\Local\\CrashDumps\\app.exe.12345.dmp",
            extension: ".dmp",
            sizeBytes: 75_000_000,
            lastModifiedUtc: timestamp,
            isHidden: false,
            isSystem: false,
            wasRecentlyModified: false,
            lastAccessUtc: timestamp,
            creationUtc: timestamp);

        var result = CleanupIntelligence.EvaluateFile(definition, context, reference);

        Assert.True(result.ShouldInclude);
        Assert.True(result.Confidence >= 0.6);
        Assert.Contains(result.Signals, signal => signal.Contains("Crash path hint", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public static void ShouldCheckActiveLock_ReturnsTrue_ForCrashDump()
    {
        var definition = new CleanupTargetDefinition("Orphaned", "Crash Dumps", "C:\\Crashes", string.Empty);
        var reference = DateTime.UtcNow.AddHours(-2);
        var context = new CleanupFileContext(
            name: "sample.exe.9999.dmp",
            fullPath: "C:\\Crashes\\sample.exe.9999.dmp",
            extension: ".dmp",
            sizeBytes: 50_000_000,
            lastModifiedUtc: reference,
            isHidden: false,
            isSystem: false,
            wasRecentlyModified: true,
            lastAccessUtc: reference,
            creationUtc: reference);

        Assert.True(CleanupIntelligence.ShouldCheckActiveLock(definition, context));
    }
}
