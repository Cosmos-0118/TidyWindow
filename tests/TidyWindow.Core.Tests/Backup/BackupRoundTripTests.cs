using System;
using System.IO;
using System.Threading.Tasks;
using TidyWindow.Core.Backup;
using Xunit;

namespace TidyWindow.Core.Tests.Backup;

public sealed class BackupRoundTripTests
{
    [Fact]
    public async Task CreateAndRestore_RoundTripsSingleFile()
    {
        var sourceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var restoreRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(restoreRoot);

        var filePath = Path.Combine(sourceRoot, "note.txt");
        File.WriteAllText(filePath, "hello reset rescue");

        var archivePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".rrarchive");

        try
        {
            var backup = new BackupService();
            var backupResult = await backup.CreateAsync(new BackupRequest
            {
                SourcePaths = new[] { filePath },
                DestinationArchivePath = archivePath,
                Generator = "TestHarness"
            });

            Assert.True(File.Exists(archivePath));
            Assert.Single(backupResult.Manifest.Entries);
            Assert.Equal("TestHarness", backupResult.Manifest.Generator);
            Assert.Equal("SHA256", backupResult.Manifest.Hash.Algorithm);
            Assert.True(backupResult.Manifest.Hash.ChunkSizeBytes >= 64 * 1024);

            var restore = new RestoreService();
            var restoreResult = await restore.RestoreAsync(new RestoreRequest
            {
                ArchivePath = archivePath,
                DestinationRoot = restoreRoot,
                ConflictStrategy = BackupConflictStrategy.Overwrite,
                VerifyHashes = true
            });

            var restoredPath = Path.Combine(restoreRoot, StripDrive(filePath));
            Assert.True(File.Exists(restoredPath));
            Assert.Empty(restoreResult.Issues);
            Assert.Equal(0, restoreResult.RenamedCount);
            Assert.Equal(0, restoreResult.BackupCount);
            Assert.Equal(0, restoreResult.OverwrittenCount);
            Assert.Equal("hello reset rescue", File.ReadAllText(restoredPath));
        }
        finally
        {
            SafeDelete(sourceRoot);
            SafeDelete(restoreRoot);
            SafeDelete(archivePath);
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

    [Fact]
    public async Task ManifestIncludesEntriesForDirectories()
    {
        var sourceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceRoot);
        var subdir = Path.Combine(sourceRoot, "configs");
        Directory.CreateDirectory(subdir);
        var filePath = Path.Combine(subdir, "settings.json");
        File.WriteAllText(filePath, "{\"level\":42}");

        var archivePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".rrarchive");

        try
        {
            var backup = new BackupService();
            var backupResult = await backup.CreateAsync(new BackupRequest
            {
                SourcePaths = new[] { sourceRoot },
                DestinationArchivePath = archivePath,
                Generator = "TestHarness"
            });

            Assert.True(backupResult.Manifest.Entries.Count > 0);
            Assert.Contains(backupResult.Manifest.Entries, e => e.TargetPath.EndsWith("settings.json", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            SafeDelete(sourceRoot);
            SafeDelete(archivePath);
        }
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;
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
