using System;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;
using WpfApplication = System.Windows.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TidyWindow.App.Services;
using TidyWindow.App.Services.Cleanup;
using TidyWindow.App.ViewModels;
using TidyWindow.App.Views;
using TidyWindow.Core.Automation;
using TidyWindow.Core.Cleanup;
using TidyWindow.Core.PackageManagers;
using TidyWindow.Core.Diagnostics;
using TidyWindow.Core.Install;
using TidyWindow.Core.Maintenance;
using TidyWindow.Core.Processes;
using TidyWindow.Core.Processes.ThreatWatch;
using TidyWindow.Core.PathPilot;
using TidyWindow.Core.Startup;
using TidyWindow.Core.Uninstall;

namespace TidyWindow.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : WpfApplication
{
    private const string SingleInstanceMutexName = "Global\\TidyWindow.App.Singleton";
    private const string ActivationEventName = "Global\\TidyWindow.App.Activate";

    private IHost? _host;
    private CrashLogService? _crashLogs;
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private EventWaitHandle? _instanceActivationSignal;
    private CancellationTokenSource? _activationListenerCts;
    private volatile bool _uiReadyForActivation;
    private volatile bool _activationRequestedDuringInit;

    protected override async void OnStartup(StartupEventArgs e)
    {
        CaptureOriginalUserSid(e);

        if (!EnsureSingleInstance())
        {
            Shutdown();
            return;
        }

        AppUserModelIdService.EnsureCurrentProcessAppUserModelId();

        _crashLogs = new CrashLogService();
        _crashLogs.Attach(this);

        if (!EnsureElevated())
        {
            Shutdown();
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown; // Keep the process alive while the splash screen owns the dispatcher.

        base.OnStartup(e);

        var launchMinimized = e.Args?.Any(arg => string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase)) == true;
        var showSplash = !launchMinimized;

        SplashScreenWindow? splash = null;
        if (showSplash)
        {
            splash = new SplashScreenWindow();
            splash.Show();
            splash.UpdateStatus("Initializing system context...");

            await Task.Delay(200);
            splash.UpdateStatus("Configuring cockpit services...");
        }

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<NavigationService>();
                services.AddSingleton<SmartPageCache>();
                services.AddSingleton<ActivityLogService>();
                services.AddSingleton<IPrivilegeService, PrivilegeService>();
                services.AddSingleton<UserPreferencesService>();
                services.AddSingleton<IProcessRunner, ProcessRunner>();
                services.AddSingleton<AppAutoStartService>();
                services.AddSingleton<AppRestartService>();
                services.AddSingleton<ITrayService, TrayService>();
                services.AddSingleton<IHighFrictionPromptService, HighFrictionPromptService>();
                services.AddSingleton<IAutomationWorkTracker, AutomationWorkTracker>();
                services.AddSingleton<IUserConfirmationService, UserConfirmationService>();
                services.AddSingleton<BackgroundPresenceService>();
                services.AddSingleton<PulseGuardService>();
                services.AddSingleton<IUpdateService, UpdateService>();
                services.AddSingleton<IUpdateInstallerService, UpdateInstallerService>();
                services.AddSingleton<ISystemRestoreGuardService, SystemRestoreGuardService>();
                services.AddSingleton<InstallQueueWorkObserver>();
                services.AddSingleton<EssentialsQueueWorkObserver>();
                services.AddSingleton<IBrowserCleanupService, BrowserCleanupService>();
                services.AddSingleton<CleanupAutomationSettingsStore>();
                services.AddSingleton<CleanupAutomationScheduler>();
                services.AddSingleton<EssentialsAutomationSettingsStore>();
                services.AddSingleton<EssentialsAutomationScheduler>();
                services.AddSingleton<MaintenanceAutomationSettingsStore>();
                services.AddSingleton<MaintenanceAutoUpdateScheduler>();
                services.AddSingleton<IRelativeTimeTicker, RelativeTimeTicker>();

                services.AddSingleton<PowerShellInvoker>();
                services.AddSingleton<PackageManagerDetector>();
                services.AddSingleton<PackageManagerInstaller>();
                services.AddSingleton<CleanupService>();
                services.AddSingleton<IResourceLockService, ResourceLockService>();
                services.AddSingleton<DeepScanService>();
                services.AddSingleton<InstallCatalogService>();
                services.AddSingleton<InstallQueue>();
                services.AddSingleton<BundlePresetService>();
                services.AddSingleton<PackageInventoryService>();
                services.AddSingleton<PackageMaintenanceService>();
                services.AddSingleton<PackageVersionDiscoveryService>();
                services.AddSingleton<IAppInventoryService, AppInventoryService>();
                services.AddSingleton<IAppUninstallService, AppUninstallService>();
                services.AddSingleton<AppCleanupPlanner>();
                services.AddSingleton<EssentialsTaskCatalog>();
                services.AddSingleton<IEssentialsQueueStateStore, EssentialsQueueStateStore>();
                services.AddSingleton<EssentialsTaskQueue>();
                services.AddSingleton<IRegistryOptimizerService, RegistryOptimizerService>();
                services.AddSingleton<RegistryPreferenceService>();
                services.AddSingleton<IRegistryStateService, RegistryStateService>();
                services.AddSingleton<RegistryStateWatcher>();
                services.AddSingleton<PathPilotInventoryService>();
                services.AddSingleton<ProcessCatalogParser>();
                services.AddSingleton<ProcessStateStore>();
                services.AddSingleton<ProcessQuestionnaireEngine>();
                services.AddSingleton<ProcessControlService>();
                services.AddSingleton<ServiceResolver>();
                services.AddSingleton<TaskControlService>();
                services.AddSingleton<ProcessAutoStopEnforcer>();
                services.AddSingleton<IThreatIntelProvider, WindowsDefenderThreatIntelProvider>();
                services.AddSingleton<IThreatIntelProvider, MalwareHashBlocklist>();
                services.AddSingleton<ThreatWatchDetectionService>();
                services.AddSingleton<StartupInventoryService>();
                services.AddSingleton<StartupControlService>();
                services.AddSingleton<StartupDelayService>();
                services.AddSingleton<StartupGuardService>();
                services.AddSingleton<StartupGuardBackgroundService>();
                services.AddSingleton<ThreatWatchScanService>();
                services.AddSingleton<ThreatWatchBackgroundScanner>();

                services.AddSingleton<MainViewModel>();
                services.AddTransient<BootstrapViewModel>();
                services.AddTransient<CleanupViewModel>();
                services.AddTransient<DeepScanViewModel>();
                services.AddTransient<SettingsViewModel>();
                services.AddTransient<ProcessPreferencesViewModel>();
                services.AddTransient<InstallHubViewModel>();
                services.AddTransient<PackageMaintenanceViewModel>();
                services.AddTransient<LogsViewModel>();
                services.AddTransient<EssentialsAutomationViewModel>();
                services.AddTransient<MaintenanceAutomationViewModel>();
                services.AddTransient<EssentialsViewModel>();
                services.AddTransient<RegistryOptimizerViewModel>();
                services.AddTransient<PathPilotViewModel>();
                services.AddTransient<KnownProcessesViewModel>();
                services.AddTransient<ThreatWatchViewModel>();
                services.AddTransient<StartupControllerViewModel>();

                services.AddTransient<BootstrapPage>();
                services.AddTransient<CleanupPage>();
                services.AddTransient<DeepScanPage>();
                services.AddTransient<SettingsPage>();
                services.AddTransient<InstallHubPage>();
                services.AddTransient<PackageMaintenancePage>();
                services.AddTransient<LogsPage>();
                services.AddTransient<EssentialsPage>();
                services.AddTransient<RegistryOptimizerPage>();
                services.AddTransient<PathPilotPage>();
                services.AddTransient<KnownProcessesPage>();
                services.AddTransient<StartupControllerPage>();

                services.AddSingleton<MainWindow>();
            })
            .Build();

        splash?.UpdateStatus("Starting background services...");
        await _host.StartAsync();

        splash?.UpdateStatus("Preparing interface...");

        var preferences = _host.Services.GetRequiredService<UserPreferencesService>();
        var trayService = _host.Services.GetRequiredService<ITrayService>();
        _ = _host.Services.GetRequiredService<BackgroundPresenceService>();
        _ = _host.Services.GetRequiredService<PulseGuardService>();
        _ = _host.Services.GetRequiredService<IHighFrictionPromptService>();
        _ = _host.Services.GetRequiredService<InstallQueueWorkObserver>();
        _ = _host.Services.GetRequiredService<EssentialsQueueWorkObserver>();
        _ = _host.Services.GetRequiredService<CleanupAutomationScheduler>();
        _ = _host.Services.GetRequiredService<EssentialsAutomationScheduler>();
        _ = _host.Services.GetRequiredService<MaintenanceAutoUpdateScheduler>();
        _ = _host.Services.GetRequiredService<ProcessAutoStopEnforcer>();
        _ = _host.Services.GetRequiredService<ThreatWatchBackgroundScanner>();
        _ = _host.Services.GetRequiredService<StartupGuardBackgroundService>();

        var startHidden = launchMinimized && preferences.Current.RunInBackground;

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        Current.MainWindow = mainWindow;
        mainWindow.Opacity = startHidden ? 1 : 0;
        mainWindow.WindowState = startHidden ? WindowState.Minimized : WindowState.Maximized;
        mainWindow.Show();
        if (!startHidden)
        {
            mainWindow.Activate();
        }

        if (splash is not null)
        {
            splash.UpdateStatus("Launching cockpit...");
            await splash.CloseWithFadeAsync(TimeSpan.FromMilliseconds(1600));
        }

        if (!startHidden)
        {
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            mainWindow.BeginAnimation(Window.OpacityProperty, fadeIn);
        }
        else
        {
            trayService.HideToTray(showHint: false);
        }

        ShutdownMode = ShutdownMode.OnMainWindowClose;
        MarkUiReadyForActivation();
    }

    private static bool EnsureElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        var privilegeService = new PrivilegeService();
        if (privilegeService.CurrentMode == PrivilegeMode.Administrator)
        {
            return true;
        }

        var restartResult = privilegeService.Restart(PrivilegeMode.Administrator);
        if (restartResult.Success)
        {
            return false;
        }

        if (restartResult.AlreadyInTargetMode)
        {
            return true;
        }

        var message = string.IsNullOrWhiteSpace(restartResult.ErrorMessage)
            ? "Administrator privileges are required to run TidyWindow."
            : restartResult.ErrorMessage;

        System.Windows.MessageBox.Show(message, "TidyWindow", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        _crashLogs?.Dispose();

        CleanupSingleInstanceResources();

        base.OnExit(e);
    }

    private static void CaptureOriginalUserSid(StartupEventArgs e)
    {
        string? sid = null;

        if (e?.Args is { Length: > 0 })
        {
            foreach (var argument in e.Args)
            {
                if (argument is null)
                {
                    continue;
                }

                if (argument.StartsWith(RegistryUserContext.OriginalUserSidArgumentPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    sid = argument.Substring(RegistryUserContext.OriginalUserSidArgumentPrefix.Length).Trim('"');
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(sid))
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                sid = identity?.User?.Value;
            }
            catch
            {
                sid = null;
            }
        }

        if (!string.IsNullOrWhiteSpace(sid))
        {
            Environment.SetEnvironmentVariable(RegistryUserContext.OriginalUserSidEnvironmentVariable, sid);
        }
    }

    private bool EnsureSingleInstance()
    {
        try
        {
            _instanceActivationSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        }
        catch
        {
            _instanceActivationSignal = null;
        }

        try
        {
            _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
            if (!createdNew)
            {
                _instanceActivationSignal?.Set();
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
                return false;
            }

            _ownsSingleInstanceMutex = true;
            _activationListenerCts = new CancellationTokenSource();
            Task.Factory.StartNew(
                () => WatchForActivationRequests(_activationListenerCts.Token),
                _activationListenerCts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            return true;
        }
        catch
        {
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
            // If we cannot create or own the mutex, allow the app to proceed to avoid blocking users.
            return true;
        }
    }

    private void WatchForActivationRequests(CancellationToken token)
    {
        var signal = _instanceActivationSignal;
        if (signal is null)
        {
            return;
        }

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!signal.WaitOne())
                {
                    continue;
                }
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (token.IsCancellationRequested)
            {
                break;
            }

            Dispatcher.BeginInvoke(HandleActivationRequest);
        }
    }

    private void HandleActivationRequest()
    {
        if (!_uiReadyForActivation)
        {
            _activationRequestedDuringInit = true;
            return;
        }

        ActivateMainWindow();
    }

    private void ActivateMainWindow()
    {
        var trayService = _host?.Services.GetService<ITrayService>();
        trayService?.ShowMainWindow();
    }

    private void MarkUiReadyForActivation()
    {
        _uiReadyForActivation = true;

        if (_activationRequestedDuringInit)
        {
            _activationRequestedDuringInit = false;
            ActivateMainWindow();
        }
    }

    private void CleanupSingleInstanceResources()
    {
        try
        {
            if (_activationListenerCts is not null)
            {
                _activationListenerCts.Cancel();
                _instanceActivationSignal?.Set();
                _activationListenerCts.Dispose();
                _activationListenerCts = null;
            }
        }
        catch
        {
        }

        _instanceActivationSignal?.Dispose();
        _instanceActivationSignal = null;

        if (_ownsSingleInstanceMutex && _singleInstanceMutex is not null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
            finally
            {
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
                _ownsSingleInstanceMutex = false;
            }
        }
    }
}

