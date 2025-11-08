using System;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Microsoft.Win32;
using TidyWindow.Core.Maintenance;
using TidyWindow.Core.Automation;
using Xunit;

namespace TidyWindow.Core.Tests;

public sealed class RegistryStateServiceTests
{
    [Fact]
    public async Task RegistryStateService_PopulatesCurrentValueForMenuShowDelay()
    {
        var invoker = new PowerShellInvoker();
        var optimizer = new RegistryOptimizerService(invoker);
        var service = new RegistryStateService(invoker, optimizer);

        var state = await service.GetStateAsync("menu-show-delay", forceRefresh: true);
        var value = state.Values.Single();

        Assert.NotNull(value.CurrentValue);
        Assert.True(value.CurrentDisplay.Length > 0);
        Assert.False(string.IsNullOrWhiteSpace(value.CurrentDisplay[0]));
        Assert.Equal("60", value.RecommendedValue?.ToString());
        Assert.Equal("60", value.RecommendedDisplay);
    }

    [SupportedOSPlatform("windows")]
    [Fact]
    public async Task RegistryStateService_ReflectsActualMenuDelayValue()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string registryPath = @"HKEY_CURRENT_USER\Control Panel\Desktop";
        const string valueName = "MenuShowDelay";

        var original = Registry.GetValue(registryPath, valueName, null);
        try
        {
            Registry.SetValue(registryPath, valueName, "6", RegistryValueKind.String);

            var invoker = new PowerShellInvoker();
            var optimizer = new RegistryOptimizerService(invoker);
            var service = new RegistryStateService(invoker, optimizer);

            var state = await service.GetStateAsync("menu-show-delay", forceRefresh: true);
            var value = state.Values.Single();

            Assert.NotNull(value.CurrentValue);
            Assert.True(value.CurrentDisplay.Length > 0);
            Assert.Equal("6", value.CurrentDisplay[0]);
        }
        finally
        {
            if (original is null)
            {
                RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default)
                    .OpenSubKey("Control Panel\\Desktop", writable: true)?.DeleteValue(valueName, throwOnMissingValue: false);
            }
            else
            {
                Registry.SetValue(registryPath, valueName, original, RegistryValueKind.String);
            }
        }
    }

}
