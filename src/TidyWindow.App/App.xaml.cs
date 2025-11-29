using System;
using System.Linq;
using System.Security.Principal;
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
using TidyWindow.Core.Processes.AntiSystem;
using TidyWindow.Core.PathPilot;
using TidyWindow.Core.Uninstall;

namespace TidyWindow.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : WpfApplication
{
    private IHost? _host;
    private CrashLogService? _crashLogs;

    protected override async void OnStartup(StartupEventArgs e)
    {
        CaptureOriginalUserSid(e);

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

        var splash = new SplashScreenWindow();
        splash.Show();
        splash.UpdateStatus("Initializing system context...");

        await Task.Delay(200);
        splash.UpdateStatus("Configuring cockpit services...");

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<NavigationService>();
                services.AddSingleton<ActivityLogService>();
                services.AddSingleton<IPrivilegeService, PrivilegeService>();
                services.AddSingleton<UserPreferencesService>();
                services.AddSingleton<AppAutoStartService>();
                services.AddSingleton<AppRestartService>();
                services.AddSingleton<ITrayService, TrayService>();
                services.AddSingleton<IHighFrictionPromptService, HighFrictionPromptService>();
                services.AddSingleton<IAutomationWorkTracker, AutomationWorkTracker>();
                services.AddSingleton<IUserConfirmationService, UserConfirmationService>();
                services.AddSingleton<BackgroundPresenceService>();
                services.AddSingleton<PulseGuardService>();
                services.AddSingleton<InstallQueueWorkObserver>();
                services.AddSingleton<EssentialsQueueWorkObserver>();
                services.AddSingleton<IBrowserCleanupService, BrowserCleanupService>();
                services.AddSingleton<EssentialsAutomationSettingsStore>();
                services.AddSingleton<EssentialsAutomationScheduler>();

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
                services.AddSingleton<ProcessAutoStopEnforcer>();
                services.AddSingleton<IThreatIntelProvider, WindowsDefenderThreatIntelProvider>();
                services.AddSingleton<IThreatIntelProvider, MalwareHashBlocklist>();
                services.AddSingleton<AntiSystemDetectionService>();
                services.AddSingleton<AntiSystemScanService>();

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
                services.AddTransient<EssentialsViewModel>();
                services.AddTransient<RegistryOptimizerViewModel>();
                services.AddTransient<PathPilotViewModel>();
                services.AddTransient<KnownProcessesViewModel>();
                services.AddTransient<AntiSystemViewModel>();

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

                services.AddSingleton<MainWindow>();
            })
            .Build();

        splash.UpdateStatus("Starting background services...");
        await _host.StartAsync();

        splash.UpdateStatus("Preparing interface...");

        var preferences = _host.Services.GetRequiredService<UserPreferencesService>();
        var trayService = _host.Services.GetRequiredService<ITrayService>();
        _ = _host.Services.GetRequiredService<BackgroundPresenceService>();
        _ = _host.Services.GetRequiredService<PulseGuardService>();
        _ = _host.Services.GetRequiredService<IHighFrictionPromptService>();
        _ = _host.Services.GetRequiredService<InstallQueueWorkObserver>();
        _ = _host.Services.GetRequiredService<EssentialsQueueWorkObserver>();
        _ = _host.Services.GetRequiredService<EssentialsAutomationScheduler>();
        _ = _host.Services.GetRequiredService<ProcessAutoStopEnforcer>();

        var launchMinimized = e.Args?.Any(arg => string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase)) == true;
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

        splash.UpdateStatus("Launching cockpit...");
        await splash.CloseWithFadeAsync(TimeSpan.FromMilliseconds(1600));

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
}

