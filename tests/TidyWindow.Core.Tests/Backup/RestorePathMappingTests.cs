using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using TidyWindow.Core.Backup;
using Xunit;

namespace TidyWindow.Core.Tests.Backup;

public sealed class RestorePathMappingTests
{
    [Fact]
    public async Task Restore_UsesProvidedPathRemapping()
    {
        var sourceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var mappedRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".rrarchive");

        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(Path.Combine(sourceRoot, "docs"));
        File.WriteAllText(Path.Combine(sourceRoot, "docs", "readme.txt"), "hello remap");

        try
        {
            var backup = new BackupService();
            await backup.CreateAsync(new BackupRequest
            {
                SourcePaths = new[] { sourceRoot },
                DestinationArchivePath = archivePath,
                Generator = "TestHarness"
            });

            var restore = new RestoreService();
            var result = await restore.RestoreAsync(new RestoreRequest
            {
                ArchivePath = archivePath,
                ConflictStrategy = BackupConflictStrategy.Overwrite,
                PathRemapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [sourceRoot] = mappedRoot
                },
                VerifyHashes = true
            });

            var mappedFile = Path.Combine(mappedRoot, "docs", "readme.txt");
            Assert.True(File.Exists(mappedFile));
            Assert.Equal("hello remap", File.ReadAllText(mappedFile));
            Assert.Empty(result.Issues);
        }
        finally
        {
            SafeDelete(sourceRoot);
            SafeDelete(mappedRoot);
            SafeDelete(archivePath);
        }
    }

    [Fact]
    public async Task Restore_PrefersLongestMappingPrefix()
    {
        var sourceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var nestedSource = Path.Combine(sourceRoot, "Projects");
        var archivePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".rrarchive");
        var baseTarget = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var nestedTarget = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path.Combine(nestedSource, "App"));
        File.WriteAllText(Path.Combine(nestedSource, "App", "config.json"), "{}");

        try
        {
            var backup = new BackupService();
            await backup.CreateAsync(new BackupRequest
            {
                SourcePaths = new[] { sourceRoot },
                DestinationArchivePath = archivePath,
                Generator = "TestHarness"
            });

            var restore = new RestoreService();
            var result = await restore.RestoreAsync(new RestoreRequest
            {
                ArchivePath = archivePath,
                ConflictStrategy = BackupConflictStrategy.Overwrite,
                PathRemapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [sourceRoot] = baseTarget,
                    [nestedSource] = nestedTarget
                },
                VerifyHashes = true
            });

            var expectedNested = Path.Combine(nestedTarget, "App", "config.json");
            var fallbackLocation = Path.Combine(baseTarget, "Projects", "App", "config.json");

            Assert.True(File.Exists(expectedNested));
            Assert.False(File.Exists(fallbackLocation));
            Assert.Equal("{}", File.ReadAllText(expectedNested));
            Assert.Empty(result.Issues);
        }
        finally
        {
            SafeDelete(sourceRoot);
            SafeDelete(baseTarget);
            SafeDelete(nestedTarget);
            SafeDelete(archivePath);
        }
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
                return;
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore cleanup failures
        }
    }
}
