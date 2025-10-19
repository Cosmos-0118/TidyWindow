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
using TidyWindow.Core.Updates;

namespace TidyWindow.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : WpfApplication
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<NavigationService>();

                services.AddSingleton<PowerShellInvoker>();
                services.AddSingleton<PackageManagerDetector>();
                services.AddSingleton<PackageManagerInstaller>();
                services.AddSingleton<CleanupService>();
                services.AddSingleton<DeepScanService>();
                services.AddSingleton<RuntimeCatalogService>();

                services.AddSingleton<MainViewModel>();
                services.AddTransient<BootstrapViewModel>();
                services.AddTransient<DashboardViewModel>();
                services.AddTransient<CleanupViewModel>();
                services.AddTransient<DeepScanViewModel>();
                services.AddTransient<RuntimeUpdatesViewModel>();
                services.AddTransient<TasksViewModel>();
                services.AddTransient<SettingsViewModel>();

                services.AddTransient<BootstrapPage>();
                services.AddTransient<DashboardPage>();
                services.AddTransient<CleanupPage>();
                services.AddTransient<DeepScanPage>();
                services.AddTransient<RuntimeUpdatesPage>();
                services.AddTransient<TasksPage>();
                services.AddTransient<SettingsPage>();

                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
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

