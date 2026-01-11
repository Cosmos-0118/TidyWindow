using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using TidyWindow.App;
using WpfApplication = System.Windows.Application;

namespace TidyWindow.App.Tests;

internal static class WpfTestHelper
{
    private static readonly Lazy<TaskScheduler> Scheduler = new(CreateStaScheduler, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<WpfApplication> ApplicationInstance = new(CreateApplication, LazyThreadSafetyMode.ExecutionAndPublication);

    public static Task RunAsync(Func<Task> action)
    {
        if (action is null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        return Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.DenyChildAttach, Scheduler.Value).Unwrap();
    }

    public static Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        if (action is null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        return Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.DenyChildAttach, Scheduler.Value).Unwrap();
    }

    public static Task Run(Action action)
    {
        if (action is null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        return Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.DenyChildAttach, Scheduler.Value);
    }

    public static void EnsureApplication()
    {
        _ = ApplicationInstance.Value;
    }

    private static TaskScheduler CreateStaScheduler()
    {
        var completion = new TaskCompletionSource<TaskScheduler>();

        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            completion.SetResult(TaskScheduler.FromCurrentSynchronizationContext());
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "TidyWindow.WpfTestDispatcher"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return completion.Task.GetAwaiter().GetResult();
    }

    private static WpfApplication CreateApplication()
    {
        WpfApplication? app;

        if (WpfApplication.Current is not null)
        {
            app = WpfApplication.Current;
        }
        else if (TaskScheduler.Current == Scheduler.Value)
        {
            // If we're already on the WPF dispatcher thread, create directly to avoid deadlocks.
            app = WpfApplication.Current ?? new WpfApplication();
        }
        else
        {
            app = null;
            Run(() => app = WpfApplication.Current ?? new WpfApplication()).GetAwaiter().GetResult();
        }

        app ??= new WpfApplication();
        app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        // Explicitly set the resource assembly so pack URIs resolve in test runs where the generated App is not created.
        if (WpfApplication.ResourceAssembly is null)
        {
            WpfApplication.ResourceAssembly = typeof(MainWindow).Assembly;
        }
        return app;
    }
}
