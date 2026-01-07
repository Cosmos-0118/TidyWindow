using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using WpfApplication = System.Windows.Application;
using Microsoft.Extensions.DependencyInjection;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels;
using Xunit;

namespace TidyWindow.App.Tests;

public sealed class TrayServiceTests
{
    [Fact]
    public async Task HideToTray_ClearsPageCache_WhenNoActiveWork()
    {
        await WpfTestHelper.RunAsync(async () =>
        {
            EnsureApplication();

            using var scope = await TrayTestScope.CreateAsync();

            var page = new DisposablePage();
            scope.Cache.StorePage(typeof(DisposablePage), page, PageCachePolicy.KeepAlive);

            scope.TrayService.Attach(scope.Window);
            scope.Window.Show();
            scope.TrayService.HideToTray(showHint: true);

            await Task.Yield();

            Assert.True(page.IsDisposed);
        });
    }

    [Fact]
    public async Task HideToTray_SkipsPageCacheClear_WhenActiveWork()
    {
        await WpfTestHelper.RunAsync(async () =>
        {
            EnsureApplication();

            using var scope = await TrayTestScope.CreateAsync();

            var page = new DisposablePage();
            scope.Cache.StorePage(typeof(DisposablePage), page, PageCachePolicy.KeepAlive);

            var token = scope.WorkTracker.BeginWork(AutomationWorkType.Maintenance, "Queued automation");

            scope.TrayService.Attach(scope.Window);
            scope.Window.Show();
            scope.TrayService.HideToTray(showHint: true);

            await Task.Yield();

            Assert.False(page.IsDisposed);

            scope.WorkTracker.CompleteWork(token);
        });
    }

    private static void EnsureApplication()
    {
        if (WpfApplication.Current is null)
        {
            new WpfApplication();
        }
    }

    private sealed class TrayTestScope : IDisposable
    {
        private string? _previousLocalAppData;
        private string _tempLocalAppData = string.Empty;

        private TrayTestScope(ServiceProvider provider, TrayService trayService, SmartPageCache cache, AutomationWorkTracker workTracker, Window window)
        {
            Provider = provider;
            TrayService = trayService;
            Cache = cache;
            WorkTracker = workTracker;
            Window = window;
        }

        public ServiceProvider Provider { get; }
        public TrayService TrayService { get; }
        public SmartPageCache Cache { get; }
        public AutomationWorkTracker WorkTracker { get; }
        public Window Window { get; }

        public static async Task<TrayTestScope> CreateAsync()
        {
            var previousLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            var tempLocalAppData = Path.Combine(Path.GetTempPath(), "TidyWindowTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempLocalAppData);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", tempLocalAppData);

            var services = new ServiceCollection();
            services.AddSingleton<ActivityLogService>();
            services.AddSingleton<SmartPageCache>();
            services.AddSingleton<UserPreferencesService>();
            services.AddSingleton<IAutomationWorkTracker, AutomationWorkTracker>();
            services.AddSingleton(provider =>
            {
                var activity = provider.GetRequiredService<ActivityLogService>();
                var cache = provider.GetRequiredService<SmartPageCache>();
                return new NavigationService(provider, activity, cache);
            });
            services.AddSingleton<MainViewModel>();

            var provider = services.BuildServiceProvider();

            var cache = (SmartPageCache)provider.GetRequiredService<SmartPageCache>();
            var activity = provider.GetRequiredService<ActivityLogService>();
            var navigation = provider.GetRequiredService<NavigationService>();
            var mainVm = provider.GetRequiredService<MainViewModel>();
            var prefs = provider.GetRequiredService<UserPreferencesService>();
            var workTracker = (AutomationWorkTracker)provider.GetRequiredService<IAutomationWorkTracker>();

            var trayService = new TrayService(navigation, prefs, activity, mainVm, cache, workTracker);
            var window = new Window();

            await Task.Yield();

            return new TrayTestScope(provider, trayService, cache, workTracker, window)
            {
                _previousLocalAppData = previousLocalAppData,
                _tempLocalAppData = tempLocalAppData
            };
        }

        public void Dispose()
        {
            try
            {
                Window.Close();
            }
            catch
            {
            }

            Provider.Dispose();
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

    private sealed class DisposablePage : Page, IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }
}
