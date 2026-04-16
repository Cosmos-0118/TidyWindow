using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TidyWindow.Core.Diagnostics;
using Xunit;

namespace TidyWindow.Core.Tests.Diagnostics;

public sealed class DeepScanServiceTests
{
    [Fact]
    public void DeepScanRequest_InvalidMaxItems_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeepScanRequest("C:\\", 0, 10, includeHiddenFiles: false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeepScanRequest("C:\\", 5, -1, includeHiddenFiles: false));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(5 * 1024 * 1024, "5.0 MB")]
    public void DeepScanFinding_SizeDisplayFormats(long bytes, string expected)
    {
        var finding = new DeepScanFinding(
            path: "C:/temp/sample.bin",
            name: "sample.bin",
            directory: "C:/temp",
            sizeBytes: bytes,
            modifiedUtc: DateTimeOffset.UtcNow,
            extension: ".bin");

        Assert.Equal(expected, finding.SizeDisplay);
        Assert.False(string.IsNullOrWhiteSpace(finding.Category));
    }

    [Fact]
    public async Task RunScanAsync_ReturnsLargestFilesAcrossTree()
    {
        using var temp = new TempDirectoryScope();
        var root = temp.DirectoryPath;

        var service = new DeepScanService();

        var nested = Directory.CreateDirectory(Path.Combine(root, "nested"));
        var deeper = Directory.CreateDirectory(Path.Combine(nested.FullName, "deeper"));

        var small = CreateFile(root, "small.bin", 128);
        var medium = CreateFile(nested.FullName, "medium.bin", 2048);
        var large = CreateFile(deeper.FullName, "large.bin", 4096);

        var request = new DeepScanRequest(root, maxItems: 2, minimumSizeInMegabytes: 0, includeHiddenFiles: false);
        var result = await service.RunScanAsync(request);

        Assert.Equal(2, result.Findings.Count);
        Assert.Equal(new[] { large, medium }, result.Findings.Select(item => item.Path).ToArray());
        Assert.Equal(2, result.TotalCandidates);
        Assert.Equal(4096 + 2048, result.TotalSizeBytes);
        Assert.DoesNotContain(result.Findings, item => item.Path == small);
        Assert.All(result.Findings, item => Assert.False(item.IsDirectory));
    }

    [Fact]
    public async Task RunScanAsync_RespectsHiddenFileToggle()
    {
        using var temp = new TempDirectoryScope();
        var root = temp.DirectoryPath;

        var service = new DeepScanService();

        var visible = CreateFile(root, "visible.bin", 2048);
        var hidden = CreateFile(root, "hidden.bin", 3072);
        File.SetAttributes(hidden, FileAttributes.Hidden);

        var withoutHidden = await service.RunScanAsync(new DeepScanRequest(root, maxItems: 5, minimumSizeInMegabytes: 0, includeHiddenFiles: false));
        Assert.Equal(new[] { visible }, withoutHidden.Findings.Select(item => item.Path).ToArray());

        var withHidden = await service.RunScanAsync(new DeepScanRequest(root, maxItems: 5, minimumSizeInMegabytes: 0, includeHiddenFiles: true));
        var withHiddenPaths = withHidden.Findings.Select(item => item.Path).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        var expectedPaths = new[] { hidden, visible }.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(expectedPaths, withHiddenPaths);

        File.SetAttributes(hidden, FileAttributes.Normal);
    }

    [Fact]
    public async Task RunScanAsync_AppliesExactNameFilters()
    {
        using var temp = new TempDirectoryScope();
        var root = temp.DirectoryPath;

        var service = new DeepScanService();

        var match = CreateFile(root, "archive.zip", 4096);
        CreateFile(root, "archive-2024.zip", 4096);

        var request = new DeepScanRequest(root, 5, 0, includeHiddenFiles: true, nameFilters: new[] { "archive.zip" }, nameMatchMode: DeepScanNameMatchMode.Exact);
        var result = await service.RunScanAsync(request);

        Assert.Single(result.Findings);
        Assert.Equal(match, result.Findings[0].Path);
    }

    [Fact]
    public async Task RunScanAsync_RespectsCaseSensitivity()
    {
        using var temp = new TempDirectoryScope();
        var root = temp.DirectoryPath;

        var service = new DeepScanService();

        var lower = CreateFile(root, "report.dat", 2048);
        var upper = CreateFile(root, "REPORT.DAT", 2048);

        var insensitive = await service.RunScanAsync(new DeepScanRequest(root, 5, 0, includeHiddenFiles: true, nameFilters: new[] { "report.dat" }, nameMatchMode: DeepScanNameMatchMode.Exact, isCaseSensitiveNameMatch: false));
        Assert.True(insensitive.Findings.Count >= 1);
        Assert.Contains(insensitive.Findings, item => string.Equals(item.Path, lower, StringComparison.OrdinalIgnoreCase));

        var sensitive = await service.RunScanAsync(new DeepScanRequest(root, 5, 0, includeHiddenFiles: true, nameFilters: new[] { "report.dat" }, nameMatchMode: DeepScanNameMatchMode.Exact, isCaseSensitiveNameMatch: true));
        Assert.Single(sensitive.Findings);
        Assert.Equal(lower, sensitive.Findings[0].Path);
        Assert.DoesNotContain(sensitive.Findings, item => string.Equals(item.Path, upper, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunScanAsync_ProducesDirectoryFindingsWhenRequested()
    {
        using var temp = new TempDirectoryScope();
        var root = temp.DirectoryPath;

        var service = new DeepScanService();

        var folder = Directory.CreateDirectory(Path.Combine(root, "logs"));
        var nestedFolder = Directory.CreateDirectory(Path.Combine(folder.FullName, "daily"));
        CreateFile(folder.FullName, "app.log", 1024 * 5);
        CreateFile(nestedFolder.FullName, "batch.log", 1024 * 3);

        var result = await service.RunScanAsync(new DeepScanRequest(root, 5, 0, includeHiddenFiles: true, includeDirectories: true));

        Assert.Contains(result.Findings, item => item.IsDirectory && item.Path == folder.FullName);
        Assert.Contains(result.Findings, item => item.IsDirectory && item.Path == nestedFolder.FullName);
        Assert.True(result.Findings.First(item => item.Path == folder.FullName).SizeBytes >= 8 * 1024);
    }

    [Fact]
    public async Task RunScanAsync_AssignsCategories()
    {
        using var temp = new TempDirectoryScope();
        var root = temp.DirectoryPath;

        var systemFolder = Directory.CreateDirectory(Path.Combine(root, "Windows"));
        var gameFolder = Directory.CreateDirectory(Path.Combine(root, "SteamLibrary"));
        var mediaFolder = Directory.CreateDirectory(Path.Combine(root, "Videos"));

        CreateFile(systemFolder.FullName, "kernel.bin", 1024 * 10);
        CreateFile(gameFolder.FullName, "game.iso", 1024 * 12);
        CreateFile(mediaFolder.FullName, "clip.mp4", 1024 * 14);

        var service = new DeepScanService();
        var result = await service.RunScanAsync(new DeepScanRequest(root, 6, 0, includeHiddenFiles: true));

        var categories = result.Findings.Select(finding => finding.Category).ToArray();
        Assert.Contains("System", categories);
        Assert.All(categories, category => Assert.False(string.IsNullOrWhiteSpace(category)));
        Assert.True(categories.Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2);
    }

    [Fact]
    public async Task RunScanAsync_MaintainsTopRankingAfterQueueSaturates()
    {
        using var temp = new TempDirectoryScope();
        var root = temp.DirectoryPath;

        var service = new DeepScanService();
        const int totalFiles = 220;
        const int maxItems = 12;

        var allPathsBySize = new (long Size, string Path)[totalFiles];
        for (var index = 0; index < totalFiles; index++)
        {
            var size = 1_000L + (index * 37L);
            var path = CreateFile(root, $"payload-{index:D3}.bin", size);
            allPathsBySize[index] = (size, path);
        }

        var expected = allPathsBySize
            .OrderByDescending(item => item.Size)
            .Take(maxItems)
            .Select(item => item.Path)
            .ToArray();

        var result = await service.RunScanAsync(new DeepScanRequest(root, maxItems, 0, includeHiddenFiles: true));

        Assert.Equal(maxItems, result.Findings.Count);
        Assert.Equal(expected, result.Findings.Select(item => item.Path).ToArray());
    }

    [Fact]
    public async Task RunScanAsync_NameFilterStillAppliesAfterQueueSaturates()
    {
        using var temp = new TempDirectoryScope();
        var root = temp.DirectoryPath;

        var service = new DeepScanService();

        // Fill the queue with many large non-matching files first.
        for (var index = 0; index < 160; index++)
        {
            CreateFile(root, $"ignore-{index:D3}.bin", 20_000 + index);
        }

        var keepA = CreateFile(root, "keep-report-a.log", 14_500);
        var keepB = CreateFile(root, "keep-report-b.log", 13_700);

        var request = new DeepScanRequest(
            root,
            maxItems: 5,
            minimumSizeInMegabytes: 0,
            includeHiddenFiles: true,
            nameFilters: new[] { "keep-report" },
            nameMatchMode: DeepScanNameMatchMode.StartsWith,
            isCaseSensitiveNameMatch: false);

        var result = await service.RunScanAsync(request);

        Assert.Equal(new[] { keepA, keepB }, result.Findings.Select(item => item.Path).ToArray());
        Assert.All(result.Findings, finding => Assert.StartsWith("keep-report", finding.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateFile(string directory, string fileName, long sizeBytes)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        if (sizeBytes > 0)
        {
            stream.SetLength(sizeBytes);
        }

        return path;
    }

    private sealed class TempDirectoryScope : IDisposable
    {
        public TempDirectoryScope()
        {
            var basePath = Path.Combine(Path.GetTempPath(), "DeepScanServiceTests", Guid.NewGuid().ToString("N"));
            DirectoryPath = Directory.CreateDirectory(basePath).FullName;
        }

        public string DirectoryPath { get; }

        public void Dispose()
        {
            try
            {
                if (!Directory.Exists(DirectoryPath))
                {
                    return;
                }

                foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                    catch
                    {
                    }
                }

                foreach (var directory in Directory.EnumerateDirectories(DirectoryPath, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.SetAttributes(directory, FileAttributes.Normal);
                    }
                    catch
                    {
                    }
                }

                Directory.Delete(DirectoryPath, recursive: true);
            }
            catch
            {
                // Swallow cleanup errors for test robustness.
            }
        }
    }
}
