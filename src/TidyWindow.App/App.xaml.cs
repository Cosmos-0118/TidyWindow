using System;
using System.Windows;
using WpfApplication = System.Windows.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels;
using TidyWindow.App.Views;
using TidyWindow.Core.Automation;
using TidyWindow.Core.Cleanup;
using TidyWindow.Core.PackageManagers;
using TidyWindow.Core.Diagnostics;
using TidyWindow.Core.Install;
using TidyWindow.Core.Maintenance;

namespace TidyWindow.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : WpfApplication
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (!EnsureElevated())
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<NavigationService>();
                services.AddSingleton<ActivityLogService>();
                services.AddSingleton<IPrivilegeService, PrivilegeService>();

                services.AddSingleton<PowerShellInvoker>();
                services.AddSingleton<PackageManagerDetector>();
                services.AddSingleton<PackageManagerInstaller>();
                services.AddSingleton<CleanupService>();
                services.AddSingleton<DeepScanService>();
                services.AddSingleton<InstallCatalogService>();
                services.AddSingleton<InstallQueue>();
                services.AddSingleton<BundlePresetService>();
                services.AddSingleton<PackageInventoryService>();
                services.AddSingleton<PackageMaintenanceService>();

                services.AddSingleton<MainViewModel>();
                services.AddTransient<BootstrapViewModel>();
                services.AddTransient<CleanupViewModel>();
                services.AddTransient<DeepScanViewModel>();
                services.AddTransient<SettingsViewModel>();
                services.AddTransient<InstallHubViewModel>();
                services.AddTransient<PackageMaintenanceViewModel>();
                services.AddTransient<LogsViewModel>();

                services.AddTransient<BootstrapPage>();
                services.AddTransient<CleanupPage>();
                services.AddTransient<DeepScanPage>();
                services.AddTransient<SettingsPage>();
                services.AddTransient<InstallHubPage>();
                services.AddTransient<PackageMaintenancePage>();
                services.AddTransient<LogsPage>();

                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
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

        base.OnExit(e);
    }
}

