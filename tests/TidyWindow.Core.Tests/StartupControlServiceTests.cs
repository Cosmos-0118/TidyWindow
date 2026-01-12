using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Threading.Tasks;
using Microsoft.Win32;
using TidyWindow.Core.Startup;
using Xunit;

namespace TidyWindow.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class StartupControlServiceTests
{
    [Fact]
    public async Task DisableAndEnable_RunEntry_UsesStartupApprovedWithoutRemovingValue()
    {
        if (!IsAdministrator())
        {
            return; // Skip silently when not elevated; the service enforces elevation.
        }

        var tempStoreRoot = CreateTempRoot();
        var tempStore = new StartupBackupStore(tempStoreRoot);
        var control = new StartupControlService(tempStore);

        var valueName = $"TestRun_{Guid.NewGuid():N}";
        var runPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        var approvedPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run";
        var command = "\"cmd.exe\" /c echo test";

        try
        {
            using (var runKey = Registry.CurrentUser.CreateSubKey(runPath, writable: true)!)
            {
                runKey.SetValue(valueName, command);
            }

            using (var approvedKey = Registry.CurrentUser.CreateSubKey(approvedPath, writable: true)!)
            {
                approvedKey.DeleteValue(valueName, throwOnMissingValue: false);
            }

            var item = new StartupItem(
                Id: $"run:HKCU Run:{valueName}",
                Name: valueName,
                ExecutablePath: "cmd.exe",
                SourceKind: StartupItemSourceKind.RunKey,
                SourceTag: "HKCU Run",
                Arguments: "/c echo test",
                RawCommand: command,
                IsEnabled: true,
                EntryLocation: $"HKCU\\{runPath}",
                Publisher: null,
                SignatureStatus: StartupSignatureStatus.Unknown,
                Impact: StartupImpact.Unknown,
                FileSizeBytes: null,
                LastModifiedUtc: null,
                UserContext: "CurrentUser");

            var disable = await control.DisableAsync(item);
            Assert.True(disable.Succeeded);
            Assert.False(disable.Item.IsEnabled);

            using (var runKey = Registry.CurrentUser.OpenSubKey(runPath, writable: false)!)
            {
                Assert.NotNull(runKey.GetValue(valueName));
            }

            var disabledData = Registry.CurrentUser.OpenSubKey(approvedPath, writable: false)?.GetValue(valueName) as byte[];
            Assert.NotNull(disabledData);
            Assert.Equal(3, disabledData![0]);

            var enable = await control.EnableAsync(disable.Item);
            Assert.True(enable.Succeeded);
            Assert.True(enable.Item.IsEnabled);

            var enabledData = Registry.CurrentUser.OpenSubKey(approvedPath, writable: false)?.GetValue(valueName) as byte[];
            Assert.NotNull(enabledData);
            Assert.Equal(2, enabledData![0]);
        }
        finally
        {
            using (var runKey = Registry.CurrentUser.CreateSubKey(runPath, writable: true)!)
            {
                runKey.DeleteValue(valueName, throwOnMissingValue: false);
            }

            using (var approvedKey = Registry.CurrentUser.CreateSubKey(approvedPath, writable: true)!)
            {
                approvedKey.DeleteValue(valueName, throwOnMissingValue: false);
            }

            TryDeleteDirectory(tempStoreRoot);
        }
    }

    [Fact]
    public async Task DisableAndEnable_RunServicesEntry_RemovesAndRestoresValueWhenNoStartupApproved()
    {
        if (!IsAdministrator())
        {
            return; // Skip silently when not elevated; the service enforces elevation.
        }

        var tempStoreRoot = CreateTempRoot();
        var tempStore = new StartupBackupStore(tempStoreRoot);
        var control = new StartupControlService(tempStore);

        var valueName = $"TestRunServices_{Guid.NewGuid():N}";
        var runServicesPath = "Software\\Microsoft\\Windows\\CurrentVersion\\RunServices";
        var command = "\"cmd.exe\" /c echo test";

        try
        {
            using (var runKey = Registry.CurrentUser.CreateSubKey(runServicesPath, writable: true)!)
            {
                runKey.SetValue(valueName, command);
            }

            var item = new StartupItem(
                Id: $"run:HKCU RunServices:{valueName}",
                Name: valueName,
                ExecutablePath: "cmd.exe",
                SourceKind: StartupItemSourceKind.RunKey,
                SourceTag: "HKCU RunServices",
                Arguments: "/c echo test",
                RawCommand: command,
                IsEnabled: true,
                EntryLocation: $"HKCU\\{runServicesPath}",
                Publisher: null,
                SignatureStatus: StartupSignatureStatus.Unknown,
                Impact: StartupImpact.Unknown,
                FileSizeBytes: null,
                LastModifiedUtc: null,
                UserContext: "CurrentUser");

            var disable = await control.DisableAsync(item);
            Assert.True(disable.Succeeded);
            Assert.False(disable.Item.IsEnabled);

            using (var runKey = Registry.CurrentUser.OpenSubKey(runServicesPath, writable: false)!)
            {
                Assert.Null(runKey.GetValue(valueName));
            }

            var enable = await control.EnableAsync(disable.Item);
            Assert.True(enable.Succeeded);
            Assert.True(enable.Item.IsEnabled);

            using (var runKey = Registry.CurrentUser.OpenSubKey(runServicesPath, writable: false)!)
            {
                Assert.Equal(command, runKey.GetValue(valueName)?.ToString());
            }
        }
        finally
        {
            using (var runKey = Registry.CurrentUser.CreateSubKey(runServicesPath, writable: true)!)
            {
                runKey.DeleteValue(valueName, throwOnMissingValue: false);
            }

            TryDeleteDirectory(tempStoreRoot);
        }
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "TidyWindowTests", "StartupControl", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
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
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
