using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels;
using TidyWindow.App.Views;
using TidyWindow.Core.Automation;
using TidyWindow.Core.PackageManagers;

namespace TidyWindow.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
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

                services.AddSingleton<MainViewModel>();
                services.AddTransient<BootstrapViewModel>();
                services.AddTransient<DashboardViewModel>();
                services.AddTransient<TasksViewModel>();
                services.AddTransient<SettingsViewModel>();

                services.AddTransient<BootstrapPage>();
                services.AddTransient<DashboardPage>();
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

