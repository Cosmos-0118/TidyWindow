using System;
using TidyWindow.Core.Cleanup;
using Xunit;

namespace TidyWindow.Core.Tests.Cleanup;

public class CleanupSystemPathSafetyTests
{
    [Fact]
    public void NullPath_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CleanupSystemPathSafety.IsSystemCriticalPath(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void EmptyOrWhitespace_Throws(string path)
    {
        Assert.Throws<ArgumentException>(() => CleanupSystemPathSafety.IsSystemCriticalPath(path));
    }

    [Fact]
    public void InvalidCharacters_Throws()
    {
        Assert.Throws<ArgumentException>(() => CleanupSystemPathSafety.IsSystemCriticalPath("C:<>\\"));
    }

    [Theory]
    [InlineData("C:../bootmgr")]
    [InlineData("..\\..\\..\\bootmgr")]
    [InlineData("../../Windows/System32")]
    public void Traversal_ReturnsFalse(string path)
    {
        var result = CleanupSystemPathSafety.IsSystemCriticalPath(path);
        Assert.False(result);
    }

    [Theory]
    [InlineData("C\\Windows\\System32\\..\\System32\\config\\SAM")]
    public void NormalizedCriticalSubpath_ReturnsTrue(string path)
    {
        var normalized = path.Replace('\\', System.IO.Path.DirectorySeparatorChar);
        Assert.True(CleanupSystemPathSafety.IsSystemCriticalPath(normalized));
    }

    [Theory]
    [InlineData("C:\\bootmgr")]
    [InlineData("X:\\BCD")]
    public void CriticalFiles_ReturnTrue(string path)
    {
        Assert.True(CleanupSystemPathSafety.IsSystemCriticalPath(path));
    }

    [Fact]
    public void SystemDirectorySubpath_ReturnsTrue()
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var sam = System.IO.Path.Combine(system32, "config", "SAM");
        Assert.True(CleanupSystemPathSafety.IsSystemCriticalPath(sam));
    }

    [Fact]
    public void NonCritical_WindowsTemp_ReturnsFalse()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var temp = System.IO.Path.Combine(windows, "Temp");
        Assert.False(CleanupSystemPathSafety.IsSystemCriticalPath(temp));
    }

    [Fact]
    public void CaseInsensitive_Matches()
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var mixed = system32.Replace("System32", "SySTeM32", StringComparison.OrdinalIgnoreCase);
        Assert.True(CleanupSystemPathSafety.IsSystemCriticalPath(mixed));
    }

    [Fact]
    public void AdditionalRoots_AreHonored()
    {
        var customRoot = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DangerZone");
        CleanupSystemPathSafety.SetAdditionalCriticalRoots(new[] { customRoot });

        Assert.True(CleanupSystemPathSafety.IsSystemCriticalPath(customRoot));
    }
}
