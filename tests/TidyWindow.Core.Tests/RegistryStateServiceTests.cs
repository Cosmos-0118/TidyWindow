using System.Linq;
using System.Threading.Tasks;
using TidyWindow.Core.Maintenance;
using TidyWindow.Core.Automation;
using Xunit;

namespace TidyWindow.Core.Tests;

public sealed class RegistryStateServiceTests
{
    [Fact]
    public async Task RegistryStateService_ProvidesRecommendationForMenuShowDelay()
    {
        var invoker = new PowerShellInvoker();
        var optimizer = new RegistryOptimizerService(invoker);
        var service = new RegistryStateService(invoker, optimizer);

        var state = await service.GetStateAsync("menu-show-delay", forceRefresh: true);
        var value = state.Values.Single();

        Assert.Equal("60", value.RecommendedValue?.ToString());
        Assert.Equal("60", value.RecommendedDisplay);
    }
}
