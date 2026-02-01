using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using TidyWindow.Core.Cleanup;
using Xunit;

namespace TidyWindow.Core.Tests.Cleanup;

/// <summary>
/// Tests for ForceDeleteHelper aggressive deletion capabilities.
/// These tests verify the multi-step deletion approach works correctly.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ForceDeleteHelperTests : IDisposable
{
    private readonly string _testRootDirectory;

    public ForceDeleteHelperTests()
    {
        _testRootDirectory = Path.Combine(Path.GetTempPath(), $"TidyWindow-ForceDelete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRootDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRootDirectory))
            {
                // Clean up using force deletion in case test left locked files
                foreach (var file in Directory.GetFiles(_testRootDirectory, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                        File.Delete(file);
                    }
                    catch { }
                }

                try
                {
                    Directory.Delete(_testRootDirectory, recursive: true);
                }
                catch { }
            }
        }
        catch
        {
            // Ignore cleanup errors in test teardown
        }
    }

    #region Basic Deletion Tests

    [Fact]
    public void TryAggressiveDelete_NormalFile_DeletesSuccessfully()
    {
        // Arrange
        var filePath = Path.Combine(_testRootDirectory, "normal.txt");
        File.WriteAllText(filePath, "test content");
        Assert.True(File.Exists(filePath));

        // Act
        var result = ForceDeleteHelper.TryAggressiveDelete(filePath, isDirectory: false, CancellationToken.None, out var failure);

        // Assert
        Assert.True(result);
        Assert.Null(failure);
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void TryAggressiveDelete_NormalDirectory_DeletesSuccessfully()
    {
        // Arrange
        var dirPath = Path.Combine(_testRootDirectory, "normal-dir");
        Directory.CreateDirectory(dirPath);
        File.WriteAllText(Path.Combine(dirPath, "file.txt"), "content");
        Assert.True(Directory.Exists(dirPath));

        // Act
        var result = ForceDeleteHelper.TryAggressiveDelete(dirPath, isDirectory: true, CancellationToken.None, out var failure);

        // Assert
        Assert.True(result);
        Assert.Null(failure);
        Assert.False(Directory.Exists(dirPath));
    }

    [Fact]
    public void TryAggressiveDelete_NonExistentPath_ReturnsTrue()
    {
        // Arrange
        var filePath = Path.Combine(_testRootDirectory, "non-existent.txt");
        Assert.False(File.Exists(filePath));

        // Act
        var result = ForceDeleteHelper.TryAggressiveDelete(filePath, isDirectory: false, CancellationToken.None, out var failure);

        // Assert
        Assert.True(result);
        Assert.Null(failure);
    }

    [Fact]
    public void TryAggressiveDelete_EmptyPath_ReturnsTrue()
    {
        // Act
        var result = ForceDeleteHelper.TryAggressiveDelete(string.Empty, isDirectory: false, CancellationToken.None, out var failure);

        // Assert
        Assert.True(result);
        Assert.Null(failure);
    }

    [Fact]
    public void TryAggressiveDelete_NullPath_ReturnsTrue()
    {
        // Act
        var result = ForceDeleteHelper.TryAggressiveDelete(null!, isDirectory: false, CancellationToken.None, out var failure);

        // Assert
        Assert.True(result);
        Assert.Null(failure);
    }

    [Fact]
    public void TryAggressiveDelete_WhitespacePath_ReturnsTrue()
    {
        // Act
        var result = ForceDeleteHelper.TryAggressiveDelete("   ", isDirectory: false, CancellationToken.None, out var failure);

        // Assert
        Assert.True(result);
        Assert.Null(failure);
    }

    #endregion

    #region Attribute Handling Tests

    [Fact]
    public void TryAggressiveDelete_ReadOnlyFile_DeletesSuccessfully()
    {
        // Arrange
        var filePath = Path.Combine(_testRootDirectory, "readonly.txt");
        File.WriteAllText(filePath, "test content");
        File.SetAttributes(filePath, FileAttributes.ReadOnly);
        Assert.True(File.Exists(filePath));

        // Act
        var result = ForceDeleteHelper.TryAggressiveDelete(filePath, isDirectory: false, CancellationToken.None, out var failure);

        // Assert
        Assert.True(result);
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void TryAggressiveDelete_HiddenFile_DeletesSuccessfully()
    {
        // Arrange
        var filePath = Path.Combine(_testRootDirectory, "hidden.txt");
        File.WriteAllText(filePath, "test content");
        File.SetAttributes(filePath, FileAttributes.Hidden);
        Assert.True(File.Exists(filePath));

        // Act
        var result = ForceDeleteHelper.TryAggressiveDelete(filePath, isDirectory: false, CancellationToken.None, out var failure);

        // Assert
        Assert.True(result);
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void TryAggressiveDelete_SystemFile_DeletesSuccessfully()
    {
        // Arrange
        var filePath = Path.Combine(_testRootDirectory, "system.txt");
        File.WriteAllText(filePath, "test content");
        File.SetAttributes(filePath, FileAttributes.System);
        Assert.True(File.Exists(filePath));

        // Act
        var result = ForceDeleteHelper.TryAggressiveDelete(filePath, isDirectory: false, CancellationToken.None, out var failure);

        // Assert
        Assert.True(result);
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void TryAggressiveDelete_MultipleRestrictiveAttributes_DeletesSuccessfully()
    {
        // Arrange
        var filePath = Path.Combine(_testRootDirectory, "protected.txt");
        File.WriteAllText(filePath, "test content");
        File.SetAttributes(filePath, FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
        Assert.True(File.Exists(filePath));

        // Act
        var result = ForceDeleteHelper.TryAggressiveDelete(filePath, isDirectory: false, CancellationToken.None, out var failure);

        // Assert
        Assert.True(result);
        Assert.False(File.Exists(filePath));
    }

    #endregion

    #region Directory Handling Tests

    [Fact]
    public void TryAggressiveDelete_DirectoryWithReadOnlyFiles_DeletesSuccessfully()
    {
        // Arrange
        var dirPath = Path.Combine(_testRootDirectory, "readonly-dir");
        Directory.CreateDirectory(dirPath);

        for (int i = 0; i < 5; i++)
        {
            var filePath = Path.Combine(dirPath, $"file{i}.txt");
            File.WriteAllText(filePath, "content");
            File.SetAttributes(filePath, FileAttributes.ReadOnly);
        }
        Assert.True(Directory.Exists(dirPath));

        // Act
        var result = ForceDeleteHelper.TryAggressiveDelete(dirPath, isDirectory: true, CancellationToken.None, out var failure);

        // Assert
        Assert.True(result);
        Assert.False(Directory.Exists(dirPath));
    }

    [Fact]
    public void TryAggressiveDelete_NestedDirectory_DeletesSuccessfully()
    {
        // Arrange
        var dirPath = Path.Combine(_testRootDirectory, "nested");
        Directory.CreateDirectory(dirPath);

        var level1 = Path.Combine(dirPath, "level1");
        Directory.CreateDirectory(level1);

        var level2 = Path.Combine(level1, "level2");
        Directory.CreateDirectory(level2);

        var level3 = Path.Combine(level2, "level3");
        Directory.CreateDirectory(level3);

        // Add files at each level
        File.WriteAllText(Path.Combine(dirPath, "root.txt"), "content");
        File.WriteAllText(Path.Combine(level1, "l1.txt"), "content");
        File.WriteAllText(Path.Combine(level2, "l2.txt"), "content");
        File.WriteAllText(Path.Combine(level3, "l3.txt"), "content");

        Assert.True(Directory.Exists(dirPath));

        // Act
        var result = ForceDeleteHelper.TryAggressiveDelete(dirPath, isDirectory: true, CancellationToken.None, out var failure);

        // Assert
        Assert.True(result);
        Assert.False(Directory.Exists(dirPath));
    }

    [Fact]
    public void TryAggressiveDelete_EmptyDirectory_DeletesSuccessfully()
    {
        // Arrange
        var dirPath = Path.Combine(_testRootDirectory, "empty-dir");
        Directory.CreateDirectory(dirPath);
        Assert.True(Directory.Exists(dirPath));

        // Act
        var result = ForceDeleteHelper.TryAggressiveDelete(dirPath, isDirectory: true, CancellationToken.None, out var failure);

        // Assert
        Assert.True(result);
        Assert.False(Directory.Exists(dirPath));
    }

    [Fact]
    public void TryAggressiveDelete_ReadOnlyDirectory_DeletesSuccessfully()
    {
        // Arrange
        var dirPath = Path.Combine(_testRootDirectory, "readonly-attr-dir");
        Directory.CreateDirectory(dirPath);
        File.WriteAllText(Path.Combine(dirPath, "file.txt"), "content");
        new DirectoryInfo(dirPath).Attributes |= FileAttributes.ReadOnly;
        Assert.True(Directory.Exists(dirPath));

        // Act
        var result = ForceDeleteHelper.TryAggressiveDelete(dirPath, isDirectory: true, CancellationToken.None, out var failure);

        // Assert
        Assert.True(result);
        Assert.False(Directory.Exists(dirPath));
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void TryAggressiveDelete_VeryLongFileName_DeletesSuccessfully()
    {
        // Arrange - Create a file with a long name (but not exceeding MAX_PATH)
        var longName = new string('a', 200) + ".txt";
        var filePath = Path.Combine(_testRootDirectory, longName);

        try
        {
            File.WriteAllText(filePath, "test content");
            Assert.True(File.Exists(filePath));
        }
        catch (PathTooLongException)
        {
            // Skip test if path is too long for the system
            return;
        }

        // Act
        var result = ForceDeleteHelper.TryAggressiveDelete(filePath, isDirectory: false, CancellationToken.None, out var failure);

        // Assert
        Assert.True(result);
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void TryAggressiveDelete_FileWithSpecialCharacters_DeletesSuccessfully()
    {
        // Arrange
        var filePath = Path.Combine(_testRootDirectory, "special-chars (1) [test] {file}.txt");
        File.WriteAllText(filePath, "test content");
        Assert.True(File.Exists(filePath));

        // Act
        var result = ForceDeleteHelper.TryAggressiveDelete(filePath, isDirectory: false, CancellationToken.None, out var failure);

        // Assert
        Assert.True(result);
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void TryAggressiveDelete_ZeroByteFile_DeletesSuccessfully()
    {
        // Arrange
        var filePath = Path.Combine(_testRootDirectory, "zero-bytes.txt");
        File.WriteAllText(filePath, string.Empty);
        Assert.True(File.Exists(filePath));
        Assert.Equal(0, new FileInfo(filePath).Length);

        // Act
        var result = ForceDeleteHelper.TryAggressiveDelete(filePath, isDirectory: false, CancellationToken.None, out var failure);

        // Assert
        Assert.True(result);
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void TryAggressiveDelete_LargeFile_DeletesSuccessfully()
    {
        // Arrange - Create a 1MB file
        var filePath = Path.Combine(_testRootDirectory, "large-file.bin");
        var content = new byte[1024 * 1024]; // 1 MB
        new Random().NextBytes(content);
        File.WriteAllBytes(filePath, content);
        Assert.True(File.Exists(filePath));

        // Act
        var result = ForceDeleteHelper.TryAggressiveDelete(filePath, isDirectory: false, CancellationToken.None, out var failure);

        // Assert
        Assert.True(result);
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void TryAggressiveDelete_DirectoryWithManyFiles_DeletesSuccessfully()
    {
        // Arrange
        var dirPath = Path.Combine(_testRootDirectory, "many-files");
        Directory.CreateDirectory(dirPath);

        for (int i = 0; i < 100; i++)
        {
            File.WriteAllText(Path.Combine(dirPath, $"file{i:D3}.txt"), $"content{i}");
        }
        Assert.True(Directory.Exists(dirPath));

        // Act
        var result = ForceDeleteHelper.TryAggressiveDelete(dirPath, isDirectory: true, CancellationToken.None, out var failure);

        // Assert
        Assert.True(result);
        Assert.False(Directory.Exists(dirPath));
    }

    #endregion

    #region Cancellation Tests

    [Fact]
    public void TryAggressiveDelete_CancellationRequested_ReturnsEarly()
    {
        // Arrange
        var dirPath = Path.Combine(_testRootDirectory, "cancel-test");
        Directory.CreateDirectory(dirPath);
        for (int i = 0; i < 10; i++)
        {
            File.WriteAllText(Path.Combine(dirPath, $"file{i}.txt"), "content");
        }

        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act - Should return early, possibly without deleting
        var result = ForceDeleteHelper.TryAggressiveDelete(dirPath, isDirectory: true, cts.Token, out var failure);

        // Assert - Directory may or may not exist depending on implementation
        // Just verify it doesn't throw and handles cancellation gracefully
        Assert.True(result || Directory.Exists(dirPath));
    }

    #endregion
}
