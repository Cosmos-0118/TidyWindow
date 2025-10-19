using System;
using System.Linq;
using TidyWindow.Core.Install;
using Xunit;

namespace TidyWindow.Core.Tests;

public class InstallCatalogServiceTests
{
    [Fact]
    public void Packages_AreLoadedFromCatalog()
    {
        var service = new InstallCatalogService();
        var packages = service.Packages;

        Assert.NotNull(packages);
        Assert.NotEmpty(packages);

        var supershell = packages.FirstOrDefault(p => string.Equals(p.Id, "supershell", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(supershell);
        Assert.Contains("extras", supershell!.Buckets, StringComparer.OrdinalIgnoreCase);
    }
}
