using System;
using TidyWindow.Core.Cleanup;
using Xunit;

namespace TidyWindow.Core.Tests.Cleanup;

public static class CleanupCrashRetentionPolicyTests
{
    [Fact]
    public static void GetPathsToProtect_SkipsNewestPerProduct()
    {
        var reference = DateTime.UtcNow;

        var contexts = new[]
        {
            new CleanupFileContext(
                name: "app.exe.001.dmp",
                fullPath: "C:\\Crash\\app.exe.001.dmp",
                extension: ".dmp",
                sizeBytes: 64_000_000,
                lastModifiedUtc: reference.AddMinutes(-30),
                isHidden: false,
                isSystem: false,
                wasRecentlyModified: false,
                lastAccessUtc: reference.AddMinutes(-30),
                creationUtc: reference.AddMinutes(-30)),
            new CleanupFileContext(
                name: "app.exe.002.dmp",
                fullPath: "C:\\Crash\\app.exe.002.dmp",
                extension: ".dmp",
                sizeBytes: 64_000_000,
                lastModifiedUtc: reference.AddMinutes(-20),
                isHidden: false,
                isSystem: false,
                wasRecentlyModified: false,
                lastAccessUtc: reference.AddMinutes(-20),
                creationUtc: reference.AddMinutes(-20)),
            new CleanupFileContext(
                name: "app.exe.003.dmp",
                fullPath: "C:\\Crash\\app.exe.003.dmp",
                extension: ".dmp",
                sizeBytes: 64_000_000,
                lastModifiedUtc: reference.AddMinutes(-10),
                isHidden: false,
                isSystem: false,
                wasRecentlyModified: false,
                lastAccessUtc: reference.AddMinutes(-10),
                creationUtc: reference.AddMinutes(-10)),
            new CleanupFileContext(
                name: "other.exe.001.dmp",
                fullPath: "C:\\Crash\\other.exe.001.dmp",
                extension: ".dmp",
                sizeBytes: 32_000_000,
                lastModifiedUtc: reference.AddMinutes(-5),
                isHidden: false,
                isSystem: false,
                wasRecentlyModified: false,
                lastAccessUtc: reference.AddMinutes(-5),
                creationUtc: reference.AddMinutes(-5))
        };

        var protectedPaths = CleanupCrashRetentionPolicy.GetPathsToProtect(contexts);

        Assert.Equal(3, protectedPaths.Count);
        Assert.Contains("C:\\Crash\\app.exe.003.dmp", protectedPaths);
        Assert.Contains("C:\\Crash\\app.exe.002.dmp", protectedPaths);
        Assert.DoesNotContain("C:\\Crash\\app.exe.001.dmp", protectedPaths);
        Assert.Contains("C:\\Crash\\other.exe.001.dmp", protectedPaths);
    }
}
