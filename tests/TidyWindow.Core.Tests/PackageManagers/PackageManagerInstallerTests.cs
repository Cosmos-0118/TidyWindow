using System.Threading.Tasks;
using TidyWindow.Core.Automation;
using TidyWindow.Core.PackageManagers;
using Xunit;

namespace TidyWindow.Core.Tests.PackageManagers;

public sealed class PackageManagerInstallerTests
{
    [Fact(Skip = "Integration test")] // skip by default
    public async Task InstallOrRepairAsync_PassesManagerParameter()
    {
        var invoker = new PowerShellInvoker();
        var installer = new PackageManagerInstaller(invoker);

        var result = await installer.InstallOrRepairAsync("winget");

        Assert.True(result.IsSuccess);
    }
}
