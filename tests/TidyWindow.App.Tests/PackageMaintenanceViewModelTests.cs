using System;
using System.Collections.Immutable;
using System.Reflection;
using TidyWindow.App.ViewModels;
using TidyWindow.Core.Maintenance;
using Xunit;

namespace TidyWindow.App.Tests;

public sealed class PackageMaintenanceViewModelTests
{
    [Fact]
    public void TryGetNonActionableMaintenanceMessage_ReturnsHashMismatchGuidance()
    {
        var result = new PackageMaintenanceResult(
            Operation: "update",
            Manager: "winget",
            PackageId: "REALiX.HWiNFO",
            Success: false,
            Summary: "Winget reported an installer hash mismatch for 'HWiNFO'.",
            RequestedVersion: "8.34",
            StatusBefore: "UpdateAvailable",
            StatusAfter: "UpdateAvailable",
            InstalledVersion: "8.32",
            LatestVersion: "8.34",
            Attempted: true,
            Output: ImmutableArray<string>.Empty,
            Errors: ImmutableArray<string>.Empty,
            ExitCode: -1978335215,
            LogFilePath: null);

        var method = typeof(PackageMaintenanceViewModel).GetMethod(
            "TryGetNonActionableMaintenanceMessage",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var arguments = new object?[] { result, "HWiNFO", null };
        var handled = (bool)method!.Invoke(null, arguments)!;

        Assert.True(handled);
        var guidance = Assert.IsType<string>(arguments[2]);
        Assert.Contains("installer hash mismatch", guidance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Install the update manually", guidance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("version 8.34", guidance, StringComparison.OrdinalIgnoreCase);
    }
}
