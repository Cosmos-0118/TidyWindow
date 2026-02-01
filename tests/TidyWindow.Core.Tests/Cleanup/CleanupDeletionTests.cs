using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TidyWindow.Core.Cleanup;
using Xunit;

namespace TidyWindow.Core.Tests.Cleanup;

/// <summary>
/// Comprehensive tests for CleanupService deletion methods, including:
/// - Size verification after deletion
/// - PendingReboot disposition handling
/// - Force delete behavior
/// - Edge cases for false positive prevention
/// </summary>
public sealed class CleanupDeletionTests : IDisposable
{
    private readonly string _testRootDirectory;

    public CleanupDeletionTests()
    {
        _testRootDirectory = Path.Combine(Path.GetTempPath(), $"TidyWindow-DeletionTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRootDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRootDirectory))
            {
                Directory.Delete(_testRootDirectory, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors in test teardown
        }
    }

    #region CleanupDeletionDisposition Tests

    [Fact]
    public void CleanupDeletionDisposition_PendingReboot_ShouldExist()
    {
        // Verify the enum has the PendingReboot value
        var values = Enum.GetValues<CleanupDeletionDisposition>();
        Assert.Contains(CleanupDeletionDisposition.PendingReboot, values);
    }

    [Fact]
    public void CleanupDeletionEntry_ActualBytesFreed_ReturnsZeroForPendingReboot()
    {
        var entry = new CleanupDeletionEntry(
            @"C:\test\file.txt",
            1024 * 1024, // 1 MB
            IsDirectory: false,
            CleanupDeletionDisposition.PendingReboot,
            "Scheduled for removal after restart");

        Assert.Equal(0, entry.ActualBytesFreed);
    }

    [Fact]
    public void CleanupDeletionEntry_ActualBytesFreed_ReturnsSizeForDeleted()
    {
        var entry = new CleanupDeletionEntry(
            @"C:\test\file.txt",
            1024 * 1024, // 1 MB
            IsDirectory: false,
            CleanupDeletionDisposition.Deleted);

        Assert.Equal(1024 * 1024, entry.ActualBytesFreed);
    }

    [Fact]
    public void CleanupDeletionEntry_ActualBytesFreed_ReturnsZeroForSkipped()
    {
        var entry = new CleanupDeletionEntry(
            @"C:\test\file.txt",
            1024 * 1024,
            IsDirectory: false,
            CleanupDeletionDisposition.Skipped,
            "Item was skipped");

        Assert.Equal(0, entry.ActualBytesFreed);
    }

    [Fact]
    public void CleanupDeletionEntry_ActualBytesFreed_ReturnsZeroForFailed()
    {
        var entry = new CleanupDeletionEntry(
            @"C:\test\file.txt",
            1024 * 1024,
            IsDirectory: false,
            CleanupDeletionDisposition.Failed,
            "Deletion failed");

        Assert.Equal(0, entry.ActualBytesFreed);
    }

    [Fact]
    public void CleanupDeletionEntry_EffectiveReason_ReturnsPendingRebootMessage()
    {
        var entry = new CleanupDeletionEntry(
            @"C:\test\file.txt",
            1024,
            IsDirectory: false,
            CleanupDeletionDisposition.PendingReboot);

        Assert.Equal("Scheduled for removal after restart", entry.EffectiveReason);
    }

    #endregion

    #region CleanupDeletionResult Tests

    [Fact]
    public void CleanupDeletionResult_TotalBytesDeleted_ExcludesPendingReboot()
    {
        var entries = new List<CleanupDeletionEntry>
        {
            new(@"C:\deleted.txt", 1000, false, CleanupDeletionDisposition.Deleted),
            new(@"C:\pending.txt", 2000, false, CleanupDeletionDisposition.PendingReboot),
            new(@"C:\skipped.txt", 3000, false, CleanupDeletionDisposition.Skipped),
        };

        var result = new CleanupDeletionResult(entries);

        Assert.Equal(1000, result.TotalBytesDeleted); // Only deleted item
        Assert.Equal(2000, result.TotalBytesPendingReboot);
        Assert.Equal(3000, result.TotalBytesSkipped);
    }

    [Fact]
    public void CleanupDeletionResult_PendingRebootCount_IsTrackedSeparately()
    {
        var entries = new List<CleanupDeletionEntry>
        {
            new(@"C:\deleted1.txt", 100, false, CleanupDeletionDisposition.Deleted),
            new(@"C:\deleted2.txt", 100, false, CleanupDeletionDisposition.Deleted),
            new(@"C:\pending1.txt", 100, false, CleanupDeletionDisposition.PendingReboot),
            new(@"C:\pending2.txt", 100, false, CleanupDeletionDisposition.PendingReboot),
            new(@"C:\pending3.txt", 100, false, CleanupDeletionDisposition.PendingReboot),
        };

        var result = new CleanupDeletionResult(entries);

        Assert.Equal(2, result.DeletedCount);
        Assert.Equal(3, result.PendingRebootCount);
    }

    [Fact]
    public void CleanupDeletionResult_ToStatusMessage_IncludesPendingRebootInfo()
    {
        var entries = new List<CleanupDeletionEntry>
        {
            new(@"C:\deleted.txt", 1_048_576, false, CleanupDeletionDisposition.Deleted),
            new(@"C:\pending.txt", 2_097_152, false, CleanupDeletionDisposition.PendingReboot),
        };

        var result = new CleanupDeletionResult(entries);
        var message = result.ToStatusMessage();

        Assert.Contains("Deleted 1 item", message);
        Assert.Contains("pending reboot", message.ToLowerInvariant());
    }

    [Fact]
    public void CleanupDeletionResult_EmptyEntries_ReturnsZeroCounts()
    {
        var result = new CleanupDeletionResult(Array.Empty<CleanupDeletionEntry>());

        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(0, result.PendingRebootCount);
        Assert.Equal(0, result.TotalBytesDeleted);
        Assert.Equal(0, result.TotalBytesPendingReboot);
    }

    [Fact]
    public void CleanupDeletionResult_NullEntries_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new CleanupDeletionResult(null!));
    }

    #endregion

    #region Actual Deletion Tests

    [Fact]
    public async Task DeleteAsync_ExistingFile_ReportsCorrectSize()
    {
        // Arrange
        var filePath = Path.Combine(_testRootDirectory, "test-file.txt");
        var content = new string('x', 10000); // ~10KB of data
        await File.WriteAllTextAsync(filePath, content);

        var fileInfo = new FileInfo(filePath);
        var expectedSize = fileInfo.Length;

        var service = new CleanupService();
        var previewItem = new CleanupPreviewItem(
            "test-file.txt",
            filePath,
            expectedSize,
            DateTime.UtcNow.AddDays(-1),
            isDirectory: false,
            ".txt");

        // Act
        var result = await service.DeleteAsync(new[] { previewItem });

        // Assert
        Assert.Equal(1, result.DeletedCount);
        Assert.True(result.TotalBytesDeleted > 0);
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public async Task DeleteAsync_NonExistentFile_ReportsAsSkippedWithZeroSize()
    {
        // Arrange
        var filePath = Path.Combine(_testRootDirectory, "non-existent.txt");
        var service = new CleanupService();
        var previewItem = new CleanupPreviewItem(
            "non-existent.txt",
            filePath,
            1_000_000, // 1MB claimed size
            DateTime.UtcNow,
            isDirectory: false,
            ".txt");

        // Act
        var result = await service.DeleteAsync(new[] { previewItem });

        // Assert
        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(0, result.TotalBytesDeleted);

        var entry = result.Entries.FirstOrDefault();
        Assert.NotNull(entry);
        Assert.Equal(CleanupDeletionDisposition.Skipped, entry.Disposition);
        Assert.Equal(0, entry.SizeBytes); // Should be 0, not the claimed 1MB
    }

    [Fact]
    public async Task DeleteAsync_Directory_ReportsActualSize()
    {
        // Arrange
        var dirPath = Path.Combine(_testRootDirectory, "test-dir");
        Directory.CreateDirectory(dirPath);

        // Create some files
        for (int i = 0; i < 5; i++)
        {
            var filePath = Path.Combine(dirPath, $"file{i}.txt");
            await File.WriteAllTextAsync(filePath, new string('x', 1000));
        }

        var service = new CleanupService();
        var previewItem = new CleanupPreviewItem(
            "test-dir",
            dirPath,
            100, // Stale/wrong size
            DateTime.UtcNow.AddDays(-1),
            isDirectory: true,
            string.Empty);

        // Act
        var result = await service.DeleteAsync(new[] { previewItem });

        // Assert
        Assert.Equal(1, result.DeletedCount);
        Assert.True(result.TotalBytesDeleted > 100); // Should be actual size, not stale 100 bytes
        Assert.False(Directory.Exists(dirPath));
    }

    [Fact]
    public async Task DeleteAsync_ProtectedSystemPath_ReportsAsSkipped()
    {
        // Arrange
        var service = new CleanupService();
        // Using System32 which is definitely protected (not C:\Windows which allows safe cleanup subdirs)
        var protectedPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");
        var previewItem = new CleanupPreviewItem(
            "System32",
            protectedPath,
            1_000_000,
            DateTime.UtcNow,
            isDirectory: true,
            string.Empty);

        var options = new CleanupDeletionOptions
        {
            AllowProtectedSystemPaths = false
        };

        // Act
        var result = await service.DeleteAsync(new[] { previewItem }, options: options);

        // Assert
        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(1, result.SkippedCount);

        var entry = result.Entries.FirstOrDefault();
        Assert.NotNull(entry);
        Assert.Contains("Protected", entry.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteAsync_HiddenFile_SkippedWhenOptionEnabled()
    {
        // Arrange
        var filePath = Path.Combine(_testRootDirectory, "hidden-file.txt");
        await File.WriteAllTextAsync(filePath, "test");
        File.SetAttributes(filePath, FileAttributes.Hidden);

        var service = new CleanupService();
        var previewItem = new CleanupPreviewItem(
            "hidden-file.txt",
            filePath,
            4,
            DateTime.UtcNow.AddDays(-1),
            isDirectory: false,
            ".txt",
            isHidden: true);

        var options = new CleanupDeletionOptions
        {
            SkipHiddenItems = true
        };

        // Act
        var result = await service.DeleteAsync(new[] { previewItem }, options: options);

        // Assert
        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.True(File.Exists(filePath)); // File should still exist
    }

    [Fact]
    public async Task DeleteAsync_ReadOnlyFile_DeletedWithForceOption()
    {
        // Arrange
        var filePath = Path.Combine(_testRootDirectory, "readonly-file.txt");
        await File.WriteAllTextAsync(filePath, "test content");
        File.SetAttributes(filePath, FileAttributes.ReadOnly);

        var service = new CleanupService();
        var previewItem = new CleanupPreviewItem(
            "readonly-file.txt",
            filePath,
            100,
            DateTime.UtcNow.AddDays(-1),
            isDirectory: false,
            ".txt");

        var options = new CleanupDeletionOptions
        {
            TakeOwnershipOnAccessDenied = true
        };

        // Act
        var result = await service.DeleteAsync(new[] { previewItem }, options: options);

        // Assert
        Assert.Equal(1, result.DeletedCount);
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public async Task DeleteAsync_MultipleItems_ReportsIndividualResults()
    {
        // Arrange
        var file1 = Path.Combine(_testRootDirectory, "file1.txt");
        var file2 = Path.Combine(_testRootDirectory, "file2.txt");
        var file3 = Path.Combine(_testRootDirectory, "non-existent.txt");

        await File.WriteAllTextAsync(file1, "content1");
        await File.WriteAllTextAsync(file2, "content2");

        var service = new CleanupService();
        var items = new[]
        {
            new CleanupPreviewItem("file1.txt", file1, 100, DateTime.UtcNow.AddDays(-1), false, ".txt"),
            new CleanupPreviewItem("file2.txt", file2, 100, DateTime.UtcNow.AddDays(-1), false, ".txt"),
            new CleanupPreviewItem("non-existent.txt", file3, 100, DateTime.UtcNow, false, ".txt"),
        };

        // Act
        var result = await service.DeleteAsync(items);

        // Assert
        Assert.Equal(2, result.DeletedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(3, result.Entries.Count);
    }

    [Fact]
    public async Task DeleteAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        // Arrange
        var service = new CleanupService();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var previewItem = new CleanupPreviewItem(
            "test.txt",
            Path.Combine(_testRootDirectory, "test.txt"),
            100,
            DateTime.UtcNow,
            isDirectory: false,
            ".txt");

        // Act & Assert
        // TaskCanceledException is a subclass of OperationCanceledException
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await service.DeleteAsync(new[] { previewItem }, cancellationToken: cts.Token));
        Assert.True(cts.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task DeleteAsync_EmptyItems_ReturnsEmptyResult()
    {
        // Arrange
        var service = new CleanupService();

        // Act
        var result = await service.DeleteAsync(Array.Empty<CleanupPreviewItem>());

        // Assert
        Assert.Equal(0, result.DeletedCount);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task DeleteAsync_NullItems_ThrowsArgumentNullException()
    {
        // Arrange
        var service = new CleanupService();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await service.DeleteAsync(null!));
    }

    [Fact]
    public async Task DeleteAsync_DuplicatePaths_AreDeduplicatedOnce()
    {
        // Arrange
        var filePath = Path.Combine(_testRootDirectory, "duplicate-test.txt");
        await File.WriteAllTextAsync(filePath, "test");

        var service = new CleanupService();
        var items = new[]
        {
            new CleanupPreviewItem("dup1.txt", filePath, 100, DateTime.UtcNow.AddDays(-1), false, ".txt"),
            new CleanupPreviewItem("dup2.txt", filePath, 100, DateTime.UtcNow.AddDays(-1), false, ".txt"),
            new CleanupPreviewItem("dup3.txt", filePath, 100, DateTime.UtcNow.AddDays(-1), false, ".txt"),
        };

        // Act
        var result = await service.DeleteAsync(items);

        // Assert
        // Only one entry should be processed since paths are deduplicated
        Assert.Single(result.Entries);
    }

    [Fact]
    public async Task DeleteAsync_RecentlyModifiedFile_SkippedWhenOptionEnabled()
    {
        // Arrange
        var filePath = Path.Combine(_testRootDirectory, "recent-file.txt");
        await File.WriteAllTextAsync(filePath, "test");

        var service = new CleanupService();
        var previewItem = new CleanupPreviewItem(
            "recent-file.txt",
            filePath,
            100,
            DateTime.UtcNow, // Just created
            isDirectory: false,
            ".txt");

        var options = new CleanupDeletionOptions
        {
            SkipRecentItems = true,
            RecentItemThreshold = TimeSpan.FromMinutes(5)
        };

        // Act
        var result = await service.DeleteAsync(new[] { previewItem }, options: options);

        // Assert
        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.True(File.Exists(filePath));
    }

    #endregion

    #region Force Delete Tests

    [Fact]
    public async Task DeleteAsync_ForceDelete_ClearsReadOnlyAndDeletes()
    {
        // Arrange
        var filePath = Path.Combine(_testRootDirectory, "force-readonly.txt");
        await File.WriteAllTextAsync(filePath, "test");
        File.SetAttributes(filePath, FileAttributes.ReadOnly | FileAttributes.Hidden);

        var service = new CleanupService();
        var previewItem = new CleanupPreviewItem(
            "force-readonly.txt",
            filePath,
            100,
            DateTime.UtcNow.AddDays(-1),
            isDirectory: false,
            ".txt");

        var options = new CleanupDeletionOptions
        {
            TakeOwnershipOnAccessDenied = true,
            SkipHiddenItems = false
        };

        // Act
        var result = await service.DeleteAsync(new[] { previewItem }, options: options);

        // Assert
        Assert.Equal(1, result.DeletedCount);
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public async Task DeleteAsync_DirectoryWithReadOnlyFiles_ForceDeleted()
    {
        // Arrange
        var dirPath = Path.Combine(_testRootDirectory, "force-dir");
        Directory.CreateDirectory(dirPath);

        for (int i = 0; i < 3; i++)
        {
            var filePath = Path.Combine(dirPath, $"readonly{i}.txt");
            await File.WriteAllTextAsync(filePath, "test");
            File.SetAttributes(filePath, FileAttributes.ReadOnly);
        }

        var service = new CleanupService();
        var previewItem = new CleanupPreviewItem(
            "force-dir",
            dirPath,
            100,
            DateTime.UtcNow.AddDays(-1),
            isDirectory: true,
            string.Empty);

        var options = new CleanupDeletionOptions
        {
            TakeOwnershipOnAccessDenied = true
        };

        // Act
        var result = await service.DeleteAsync(new[] { previewItem }, options: options);

        // Assert
        Assert.Equal(1, result.DeletedCount);
        Assert.False(Directory.Exists(dirPath));
    }

    #endregion

    #region Size Verification Tests

    [Fact]
    public async Task DeleteAsync_VerifiesDeletionBeforeCountingSize()
    {
        // Arrange
        var filePath = Path.Combine(_testRootDirectory, "verify-size.txt");
        var content = new string('x', 5000);
        await File.WriteAllTextAsync(filePath, content);

        var actualSize = new FileInfo(filePath).Length;

        var service = new CleanupService();
        var previewItem = new CleanupPreviewItem(
            "verify-size.txt",
            filePath,
            999999, // Intentionally wrong stale size
            DateTime.UtcNow.AddDays(-1),
            isDirectory: false,
            ".txt");

        // Act
        var result = await service.DeleteAsync(new[] { previewItem });

        // Assert
        Assert.Equal(1, result.DeletedCount);
        // The reported size should be the actual size at deletion time, not the stale preview size
        var entry = result.Entries.First();
        Assert.True(entry.SizeBytes > 0);
        Assert.NotEqual(999999, entry.SizeBytes); // Should not be the stale value
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task DeleteAsync_WhitespacePath_IsIgnored()
    {
        // Arrange
        var service = new CleanupService();
        var previewItem = new CleanupPreviewItem(
            "whitespace",
            "   ",
            100,
            DateTime.UtcNow,
            isDirectory: false,
            ".txt");

        // Act
        var result = await service.DeleteAsync(new[] { previewItem });

        // Assert
        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task DeleteAsync_NullPreviewItem_IsIgnored()
    {
        // Arrange
        var service = new CleanupService();
        var items = new CleanupPreviewItem?[] { null };

        // Act
        var result = await service.DeleteAsync(items!);

        // Assert
        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task DeleteAsync_EmptyPath_IsIgnored()
    {
        // Arrange
        var service = new CleanupService();
        var previewItem = new CleanupPreviewItem(
            "empty-path",
            string.Empty,
            100,
            DateTime.UtcNow,
            isDirectory: false,
            ".txt");

        // Act
        var result = await service.DeleteAsync(new[] { previewItem });

        // Assert
        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task DeleteAsync_NestedDirectory_CalculatesFullSize()
    {
        // Arrange
        var dirPath = Path.Combine(_testRootDirectory, "nested-dir");
        Directory.CreateDirectory(dirPath);

        var subDir = Path.Combine(dirPath, "subdir");
        Directory.CreateDirectory(subDir);

        // Create files at different levels
        await File.WriteAllTextAsync(Path.Combine(dirPath, "root.txt"), new string('x', 1000));
        await File.WriteAllTextAsync(Path.Combine(subDir, "sub1.txt"), new string('y', 2000));
        await File.WriteAllTextAsync(Path.Combine(subDir, "sub2.txt"), new string('z', 3000));

        var service = new CleanupService();
        var previewItem = new CleanupPreviewItem(
            "nested-dir",
            dirPath,
            1, // Intentionally wrong
            DateTime.UtcNow.AddDays(-1),
            isDirectory: true,
            string.Empty);

        // Act
        var result = await service.DeleteAsync(new[] { previewItem });

        // Assert
        Assert.Equal(1, result.DeletedCount);
        Assert.True(result.TotalBytesDeleted >= 6000); // Should be at least the sum of file contents
        Assert.False(Directory.Exists(dirPath));
    }

    #endregion
}
