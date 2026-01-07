using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
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
        if (WpfApplication.Current is not null)
        {
            return WpfApplication.Current;
        }

        // If we're already on the WPF dispatcher thread, create directly to avoid deadlocks.
        if (TaskScheduler.Current == Scheduler.Value)
        {
            return WpfApplication.Current ?? new WpfApplication();
        }

        WpfApplication? app = null;
        Run(() => app = WpfApplication.Current ?? new WpfApplication()).GetAwaiter().GetResult();
        return app!;
    }
}
