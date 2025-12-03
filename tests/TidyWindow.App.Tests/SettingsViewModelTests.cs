using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels;
using Xunit;

namespace TidyWindow.App.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task CheckForUpdatesCommand_WhenUpdateIsAvailable_UpdatesBindings()
    {
        var updateResult = new UpdateCheckResult(
            "3.2.0",
            "3.3.0",
            "stable",
            true,
            new Uri("https://example.test/TidyWindow.exe"),
            new Uri("https://example.test/release"),
            "Bug fixes",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            12345,
            "abc123");

        using var updateService = new StubUpdateService(updateResult);
        var viewModel = CreateViewModel(updateService, out var mainViewModel, out var dispose);

        try
        {
            await viewModel.CheckForUpdatesCommand.ExecuteAsync(null);

            Assert.True(viewModel.IsUpdateAvailable);
            Assert.True(viewModel.HasAttemptedUpdateCheck);
            Assert.Equal(updateResult.LatestVersion, viewModel.LatestVersionDisplay);
            Assert.Equal(updateResult.Summary, viewModel.LatestReleaseSummary);
            Assert.Equal($"Update available: {updateResult.LatestVersion}", mainViewModel.StatusMessage);
        }
        finally
        {
            dispose();
        }
    }

    [Fact]
    public async Task CheckForUpdatesCommand_WhenServiceThrows_ShowsFriendlyError()
    {
        using var updateService = new StubUpdateService(new InvalidOperationException("boom"));
        var viewModel = CreateViewModel(updateService, out _, out var dispose);

        try
        {
            await viewModel.CheckForUpdatesCommand.ExecuteAsync(null);

            Assert.True(viewModel.HasAttemptedUpdateCheck);
            Assert.False(viewModel.IsCheckingForUpdates);
            Assert.Equal("Unable to contact the update service. Please try again.", viewModel.UpdateStatusMessage);
        }
        finally
        {
            dispose();
        }
    }

    private static SettingsViewModel CreateViewModel(IUpdateService updateService, out MainViewModel mainViewModel, out Action dispose)
    {
        var activityLog = new ActivityLogService();
        var smartPageCache = new SmartPageCache();
        var serviceProvider = new ServiceCollection()
            .AddSingleton(activityLog)
            .AddSingleton(smartPageCache)
            .BuildServiceProvider();

        var navigationService = new NavigationService(serviceProvider, activityLog, smartPageCache);
        mainViewModel = new MainViewModel(navigationService, activityLog);

        var privilegeService = new StubPrivilegeService();
        var preferences = CreatePreferences(out var cleanup);
        var trayService = new StubTrayService();
        var restartService = new AppRestartService(activityLog, trayService);
        var viewModel = new SettingsViewModel(
            mainViewModel,
            privilegeService,
            preferences,
            updateService,
            new StubUpdateInstallerService(),
            trayService,
            restartService);

        dispose = () =>
        {
            cleanup();
            smartPageCache.Dispose();
            if (serviceProvider is IDisposable disposable)
            {
                disposable.Dispose();
            }
        };

        return viewModel;
    }

    private static UserPreferencesService CreatePreferences(out Action cleanup)
    {
        var original = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        var tempRoot = Path.Combine(Path.GetTempPath(), "TidyWindowTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("LOCALAPPDATA", tempRoot);

        var service = new UserPreferencesService();
        cleanup = () =>
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", original);
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // best effort cleanup
            }
        };

        return service;
    }

    private sealed class StubUpdateService : IUpdateService
    {
        private readonly UpdateCheckResult? _result;
        private readonly Exception? _exception;

        public StubUpdateService(UpdateCheckResult result)
        {
            _result = result;
            CurrentVersion = result.CurrentVersion;
        }

        public StubUpdateService(Exception exception)
        {
            _exception = exception;
            CurrentVersion = "3.0.0";
        }

        public string CurrentVersion { get; }

        public Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
        {
            if (_exception is not null)
            {
                return Task.FromException<UpdateCheckResult>(_exception);
            }

            return Task.FromResult(_result!);
        }

        public void Dispose()
        {
        }
    }

    private sealed class StubPrivilegeService : IPrivilegeService
    {
        public PrivilegeMode CurrentMode => PrivilegeMode.Administrator;

        public PrivilegeRestartResult Restart(PrivilegeMode targetMode) => PrivilegeRestartResult.AlreadyRunning();
    }

    private sealed class StubUpdateInstallerService : IUpdateInstallerService
    {
        public Task<UpdateInstallationResult> DownloadAndInstallAsync(UpdateCheckResult update, IProgress<UpdateDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new UpdateInstallationResult("C:/temp/installer.exe", true));
        }

        public void Dispose()
        {
        }
    }

    private sealed class StubTrayService : ITrayService
    {
        public bool IsExitRequested => false;

        public void Attach(System.Windows.Window window)
        {
        }

        public void ShowMainWindow()
        {
        }

        public void HideToTray(bool showHint)
        {
        }

        public void ShowNotification(PulseGuardNotification notification)
        {
        }

        public void PrepareForExit()
        {
        }

        public void ResetExitRequest()
        {
        }

        public void NavigateToLogs()
        {
        }

        public void Dispose()
        {
        }
    }
}
