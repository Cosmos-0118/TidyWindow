using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using TidyWindow.App;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels;
using Xunit;

namespace TidyWindow.App.Tests;

public sealed class MainWindowTests
{
    [Fact]
    public async Task MinimizeWhileRunningInBackgroundHidesToTray()
    {
        await WpfTestHelper.RunAsync(async () =>
        {
            EnsureApplication();

            using var scope = await MainWindowTestScope.CreateAsync(runInBackground: true);

            scope.Window.WindowState = WindowState.Minimized;
            InvokeStateChanged(scope.Window);

            Assert.Equal(1, scope.Tray.HideToTrayCalls);
            Assert.True(scope.Tray.LastHideToTrayHint);
        });
    }

    [Fact]
    public async Task ClosingWhileRunningInBackgroundCancelsAndHides()
    {
        await WpfTestHelper.RunAsync(async () =>
        {
            EnsureApplication();

            using var scope = await MainWindowTestScope.CreateAsync(runInBackground: true);

            var args = new CancelEventArgs();
            InvokeClosing(scope.Window, args);

            Assert.True(args.Cancel);
            Assert.Equal(1, scope.Tray.HideToTrayCalls);

            scope.Tray.SetExitRequested(true);
            args = new CancelEventArgs();
            InvokeClosing(scope.Window, args);
            Assert.False(args.Cancel);
        });
    }

    private static void EnsureApplication()
    {
        if (Application.Current is null)
        {
            new Application();
        }
    }

    private static void InvokeStateChanged(MainWindow window)
    {
        var handler = typeof(MainWindow).GetMethod("OnStateChanged", BindingFlags.NonPublic | BindingFlags.Instance);
        handler?.Invoke(window, new object?[] { window, EventArgs.Empty });
    }

    private static void InvokeClosing(MainWindow window, CancelEventArgs args)
    {
        var method = typeof(MainWindow).GetMethod("OnClosing", BindingFlags.NonPublic | BindingFlags.Instance);
        method?.Invoke(window, new object?[] { args });
    }

    private sealed class MainWindowTestScope : IDisposable
    {
        private string? _previousLocalAppData;
        private string _tempLocalAppData = string.Empty;

        private MainWindowTestScope(ServiceProvider provider, MainWindow window, TestTrayService tray)
        {
            Provider = provider;
            Window = window;
            Tray = tray;
        }

        public ServiceProvider Provider { get; }

        public MainWindow Window { get; }

        public TestTrayService Tray { get; }

        public static async Task<MainWindowTestScope> CreateAsync(bool runInBackground)
        {
            var previousLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            var tempLocalAppData = Path.Combine(Path.GetTempPath(), "TidyWindowTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempLocalAppData);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", tempLocalAppData);

            var services = new ServiceCollection();
            services.AddSingleton<ActivityLogService>();
            services.AddSingleton(provider => new NavigationService(provider));
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<UserPreferencesService>();
            services.AddSingleton<ITrayService, TestTrayService>();

            var provider = services.BuildServiceProvider();

            var navigation = provider.GetRequiredService<NavigationService>();
            var viewModel = provider.GetRequiredService<MainViewModel>();
            var preferences = provider.GetRequiredService<UserPreferencesService>();
            var tray = (TestTrayService)provider.GetRequiredService<ITrayService>();

            preferences.SetRunInBackground(runInBackground);

            var window = new MainWindow(viewModel, navigation, tray, preferences);

            await Task.Yield();

            var scope = new MainWindowTestScope(provider, window, tray)
            {
                _previousLocalAppData = previousLocalAppData,
                _tempLocalAppData = tempLocalAppData
            };

            return scope;
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

            Tray.Dispose();
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
}
