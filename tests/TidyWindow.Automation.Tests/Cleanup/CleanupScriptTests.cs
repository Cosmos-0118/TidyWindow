using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TidyWindow.Core.Automation;
using TidyWindow.Core.Cleanup;

namespace TidyWindow.Automation.Tests.Cleanup;

public sealed class CleanupScriptTests
{
    [Fact]
    public async Task PreviewWithoutDownloads_ReturnsTempEntry()
    {
        Assert.True(OperatingSystem.IsWindows(), "Cleanup preview script requires Windows.");

        var invoker = new PowerShellInvoker();
        var service = new CleanupService(invoker);

        var report = await service.PreviewAsync(includeDownloads: false, previewCount: 5);

        Assert.NotNull(report);
        Assert.NotEmpty(report.Targets);
        Assert.Contains(report.Targets, target =>
            target.Classification.Equals("Temp", StringComparison.OrdinalIgnoreCase) &&
            target.Category.Contains("User Temp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PreviewWithDownloads_AddsDownloadsCategory()
    {
        Assert.True(OperatingSystem.IsWindows(), "Cleanup preview script requires Windows.");

        var invoker = new PowerShellInvoker();
        var service = new CleanupService(invoker);

        var report = await service.PreviewAsync(includeDownloads: true, previewCount: 1);

        Assert.NotNull(report);
        Assert.NotEmpty(report.Targets);
        Assert.Contains(report.Targets, target => target.Classification.Equals("Downloads", StringComparison.OrdinalIgnoreCase));
    }
}
