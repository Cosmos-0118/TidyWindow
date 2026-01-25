using System;
using System.IO;
using System.Linq;
using TidyWindow.App.Services;
using Xunit;

namespace TidyWindow.App.Tests.ResetRescue;

public sealed class AppDataFilterTests
{
    [Fact]
    public void FilterUsefulPaths_ReturnsEmpty_ForNullOrWhitespace()
    {
        string[]? nullInput = null;
        var nullResult = AppDataFilter.FilterUsefulPaths(nullInput!);
        Assert.Empty(nullResult);

        var whitespaceResult = AppDataFilter.FilterUsefulPaths(new[] { string.Empty, "   ", "\t" });
        Assert.Empty(whitespaceResult);
    }

    [Fact]
    public void FilterUsefulPaths_ExcludesCommonCacheAndTempRoots()
    {
        var root = Path.Combine(Path.GetTempPath(), "AppDataFilterTests", Guid.NewGuid().ToString("N"));
        var candidates = new[]
        {
            "cache", "Cache", "caches", "temp", "tmp", "logs", "log", "crashpad",
            "GPUCache", "Code Cache", "Service Worker", "shadercache", "Shader Cache",
            "indexeddb", "blob_storage", "local storage", "webviewcache", "appcache"
        }.Select(name => Path.Combine(root, name + Path.DirectorySeparatorChar)).ToArray();

        var result = AppDataFilter.FilterUsefulPaths(candidates);

        Assert.Empty(result);
    }

    [Fact]
    public void FilterUsefulPaths_NormalizesAndDeduplicatesUsefulPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "AppDataFilterTests", Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "Data");
        Directory.CreateDirectory(data);
        Environment.SetEnvironmentVariable("APPDATAFILTER_TEST_ROOT", root);

        try
        {
            var input = new[]
            {
                data,
                data + Path.DirectorySeparatorChar,
                Path.Combine("%APPDATAFILTER_TEST_ROOT%", "Data"),
                data.ToUpperInvariant()
            };

            var result = AppDataFilter.FilterUsefulPaths(input);
            var normalized = Path.GetFullPath(data);

            var entry = Assert.Single(result);
            Assert.Equal(normalized, entry, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPDATAFILTER_TEST_ROOT", null);
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void FilterUsefulPaths_RetainsUsefulDataInsideCacheHierarchy()
    {
        var root = Path.Combine(Path.GetTempPath(), "AppDataFilterTests", Guid.NewGuid().ToString("N"));
        var settings = Path.Combine(root, "Cache", "Settings");
        var configs = Path.Combine(root, "UserData", "Config");
        Directory.CreateDirectory(settings);
        Directory.CreateDirectory(configs);

        try
        {
            var input = new[] { settings, configs };
            var result = AppDataFilter.FilterUsefulPaths(input);

            Assert.Contains(Path.GetFullPath(settings), result, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(Path.GetFullPath(configs), result, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Ignored.
        }
    }
}
