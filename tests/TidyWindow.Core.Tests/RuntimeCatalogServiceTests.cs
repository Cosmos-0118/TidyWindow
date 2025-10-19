using System;
using System.Linq;
using System.Threading.Tasks;
using TidyWindow.Core.Automation;
using TidyWindow.Core.Updates;
using Xunit;

namespace TidyWindow.Core.Tests;

public sealed class RuntimeCatalogServiceTests
{
    [Fact]
    public async Task CheckForUpdatesAsync_ReturnsStatusForCatalogEntries()
    {
        var service = new RuntimeCatalogService(new PowerShellInvoker());

        var catalog = await service.GetCatalogAsync();
        var result = await service.CheckForUpdatesAsync();

        Assert.NotEmpty(result.Runtimes);
        Assert.Equal(catalog.Count, result.Runtimes.Count);
        Assert.Equal(
            catalog.Select(entry => entry.Id).OrderBy(static id => id, StringComparer.OrdinalIgnoreCase),
            result.Runtimes.Select(status => status.CatalogEntry.Id).OrderBy(static id => id, StringComparer.OrdinalIgnoreCase));

        Assert.All(result.Runtimes, status =>
        {
            Assert.NotNull(status.CatalogEntry);
            Assert.False(string.IsNullOrWhiteSpace(status.CatalogEntry.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(status.DownloadUrl));
        });
    }
}
