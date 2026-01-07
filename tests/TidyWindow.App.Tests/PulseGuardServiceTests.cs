using System;
using System.IO;
using System.Threading.Tasks;
using TidyWindow.App.Services;
using Xunit;

namespace TidyWindow.App.Tests;

public sealed class PulseGuardServiceTests
{
    [Fact]
    public async Task LegacyPowerShellErrorShowsHighFrictionPrompt()
    {
        await WpfTestHelper.RunAsync(async () =>
        {
            using var scope = new PulseGuardTestScope();

            scope.ActivityLog.LogError("Bootstrap", "Automation halted because PowerShell 5.1 is still active.");

            var scenario = await scope.Prompt.WaitForScenarioAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(HighFrictionScenario.LegacyPowerShell, scenario);
        });
    }

    [Fact]
    public async Task SuccessNotificationsRespectCooldownWindow()
    {
        await WpfTestHelper.RunAsync(async () =>
        {
            using var scope = new PulseGuardTestScope();

            scope.ActivityLog.LogSuccess("Cleanup", "Finished removing 42 stale files.");
            await scope.Tray.WaitForFirstNotificationAsync(TimeSpan.FromSeconds(2));

            scope.ActivityLog.LogSuccess("Cleanup", "Follow-up success should be throttled.");
            await Task.Delay(300);

            Assert.Single(scope.Tray.Notifications);
        });
    }

    [Fact]
    public async Task PulseGuardEntriesDoNotTriggerPrompts()
    {
        await WpfTestHelper.RunAsync(async () =>
        {
            using var scope = new PulseGuardTestScope();

            scope.ActivityLog.LogWarning("PulseGuard", "PulseGuard detected app restart requirement.");

            await Assert.ThrowsAsync<TimeoutException>(() => scope.Prompt.WaitForScenarioAsync(TimeSpan.FromMilliseconds(200)));
        });
    }

    [Fact]
    public async Task KnownProcessesMissingServiceWarningDoesNotNotify()
    {
        await WpfTestHelper.RunAsync(async () =>
        {
            using var scope = new PulseGuardTestScope();

            scope.ActivityLog.LogWarning("Known Processes", "Contoso Telemetry Service: Service not found.");

            await Assert.ThrowsAsync<TimeoutException>(() => scope.Tray.WaitForFirstNotificationAsync(TimeSpan.FromMilliseconds(200)));
        });
    }

    [Fact]
    public async Task ThreatWatchScanClearDoesNotNotify()
    {
        await WpfTestHelper.RunAsync(async () =>
        {
            using var scope = new PulseGuardTestScope();

            scope.ActivityLog.LogSuccess("Threat Watch", "Background scan is clear.");

            await Assert.ThrowsAsync<TimeoutException>(() => scope.Tray.WaitForFirstNotificationAsync(TimeSpan.FromMilliseconds(200)));
        });
    }

    [Fact]
    public async Task MinimizedWindowStillAllowsNotificationsWhenNotifyOnlyWhenInactive()
    {
        await WpfTestHelper.RunAsync(async () =>
        {
            WpfTestHelper.EnsureApplication();

            using var scope = new PulseGuardTestScope();
            var window = new System.Windows.Window
            {
                WindowState = System.Windows.WindowState.Minimized
            };

            System.Windows.Application.Current!.MainWindow = window;

            scope.Preferences.SetNotifyOnlyWhenInactive(true);

            scope.ActivityLog.LogSuccess("Cleanup", "Background cleanup completed.");

            await scope.Tray.WaitForFirstNotificationAsync(TimeSpan.FromSeconds(2));
        });
    }

    private sealed class PulseGuardTestScope : IDisposable
    {
        private readonly string? _previousLocalAppData;
        private readonly string _tempLocalAppData;

        public PulseGuardTestScope()
        {
            _previousLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            _tempLocalAppData = Path.Combine(Path.GetTempPath(), "TidyWindowTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempLocalAppData);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", _tempLocalAppData);

            ActivityLog = new ActivityLogService();
            Preferences = new UserPreferencesService();
            Preferences.SetPulseGuardEnabled(true);
            Preferences.SetNotificationsEnabled(true);
            Preferences.SetNotifyOnlyWhenInactive(false);
            Preferences.SetShowSuccessSummaries(true);
            Preferences.SetShowActionAlerts(true);

            Tray = new TestTrayService();
            Prompt = new TestHighFrictionPromptService();
            PulseGuard = new PulseGuardService(ActivityLog, Preferences, Tray, Prompt);
        }

        public ActivityLogService ActivityLog { get; }

        public UserPreferencesService Preferences { get; }

        public TestTrayService Tray { get; }

        public TestHighFrictionPromptService Prompt { get; }

        public PulseGuardService PulseGuard { get; }

        public void Dispose()
        {
            PulseGuard.Dispose();
            Tray.Dispose();
            Environment.SetEnvironmentVariable("LOCALAPPDATA", _previousLocalAppData);
            try
            {
                Directory.Delete(_tempLocalAppData, recursive: true);
            }
            catch
            {
            }
        }
    }

}
