using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using TidyWindow.Core.Backup;
using Xunit;

namespace TidyWindow.Core.Tests.Backup;

public sealed class RegistryBackupTests
{
    [Fact]
    public async Task BackupAndRestore_HkcuValues_RoundTrip()
    {
        var keyPath = $"Software\\TidyWindowTest\\{Guid.NewGuid():N}";
        var archivePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".rrarchive");

        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true))
            {
                key!.SetValue(string.Empty, "default-value");
                key.SetValue("Name", "Sample", RegistryValueKind.String);
                key.SetValue("Count", 42, RegistryValueKind.DWord);
                key.SetValue("Size", 9007199254740991L, RegistryValueKind.QWord);
                key.SetValue("List", new[] { "a", "b" }, RegistryValueKind.MultiString);
                key.SetValue("Blob", new byte[] { 1, 2, 3, 4 }, RegistryValueKind.Binary);
            }

            var backup = new BackupService();
            var backupResult = await backup.CreateAsync(new BackupRequest
            {
                DestinationArchivePath = archivePath,
                SourcePaths = Array.Empty<string>(),
                RegistryKeys = new[] { $"HKEY_CURRENT_USER\\{keyPath}" },
                Generator = "TestHarness"
            });

            Assert.NotEmpty(backupResult.Manifest.Registry);
            Assert.Contains(backupResult.Manifest.Registry, r => string.Equals(r.Path, keyPath, StringComparison.OrdinalIgnoreCase));

            // Delete before restore to prove it is recreated
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);

            var restore = new RestoreService();
            await restore.RestoreAsync(new RestoreRequest
            {
                ArchivePath = archivePath,
                RestoreRegistry = true,
                ConflictStrategy = BackupConflictStrategy.Overwrite,
                VerifyHashes = true
            });

            using var restored = Registry.CurrentUser.OpenSubKey(keyPath);
            Assert.NotNull(restored);
            Assert.Equal("default-value", restored!.GetValue(string.Empty) as string);
            Assert.Equal("Sample", restored.GetValue("Name") as string);
            Assert.Equal(42, (int)restored.GetValue("Count")!);
            Assert.Equal(9007199254740991L, (long)restored.GetValue("Size")!);
            Assert.Equal(new[] { "a", "b" }, (string[])restored.GetValue("List")!);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, (byte[])restored.GetValue("Blob")!);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            SafeDelete(archivePath);
        }
    }

    [Fact]
    public async Task RegistryNotRestored_WhenFlagDisabled()
    {
        var keyPath = $"Software\\TidyWindowTest\\{Guid.NewGuid():N}";
        var archivePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".rrarchive");

        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true))
            {
                key!.SetValue("Name", "Sample", RegistryValueKind.String);
            }

            var backup = new BackupService();
            await backup.CreateAsync(new BackupRequest
            {
                DestinationArchivePath = archivePath,
                SourcePaths = Array.Empty<string>(),
                RegistryKeys = new[] { $"HKEY_CURRENT_USER\\{keyPath}" },
                Generator = "TestHarness"
            });

            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);

            var restore = new RestoreService();
            await restore.RestoreAsync(new RestoreRequest
            {
                ArchivePath = archivePath,
                RestoreRegistry = false,
                ConflictStrategy = BackupConflictStrategy.Overwrite,
                VerifyHashes = true
            });

            using var restored = Registry.CurrentUser.OpenSubKey(keyPath);
            Assert.Null(restored); // registry skipped
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            SafeDelete(archivePath);
        }
    }

    [Fact]
    public async Task IgnoresNonHkcuKeys()
    {
        var archivePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".rrarchive");

        try
        {
            var backup = new BackupService();
            var result = await backup.CreateAsync(new BackupRequest
            {
                DestinationArchivePath = archivePath,
                SourcePaths = Array.Empty<string>(),
                RegistryKeys = new[] { "HKEY_LOCAL_MACHINE\\Software\\SomeKey" },
                Generator = "TestHarness"
            });

            Assert.Empty(result.Manifest.Registry);
        }
        finally
        {
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
            // ignore cleanup
        }
    }
}
