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
    public async Task Restore_UsesDestinationRoot()
    {
        var sourceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var destinationRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
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
                DestinationRoot = destinationRoot,
                VerifyHashes = true
            });

            var mappedFile = Path.Combine(destinationRoot, StripDrive(sourceRoot), "docs", "readme.txt");
            Assert.True(File.Exists(mappedFile));
            Assert.Equal("hello remap", File.ReadAllText(mappedFile));
            Assert.Empty(result.Issues);
        }
        finally
        {
            SafeDelete(sourceRoot);
            SafeDelete(destinationRoot);
            SafeDelete(archivePath);
        }
    }

    [Fact]
    public async Task Restore_UsesVolumeOverride()
    {
        var sourceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".rrarchive");
        var overrideRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path.Combine(sourceRoot, "Projects", "App"));
        File.WriteAllText(Path.Combine(sourceRoot, "Projects", "App", "config.json"), "{}");

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
                VolumeRootOverride = overrideRoot,
                VerifyHashes = true
            });

            var expectedNested = Path.Combine(overrideRoot, StripDrive(sourceRoot), "Projects", "App", "config.json");

            Assert.True(File.Exists(expectedNested));
            Assert.Equal("{}", File.ReadAllText(expectedNested));
            Assert.Empty(result.Issues);
        }
        finally
        {
            SafeDelete(sourceRoot);
            SafeDelete(overrideRoot);
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

    private static string StripDrive(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(root))
        {
            return path.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return path[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
