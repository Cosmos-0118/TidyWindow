using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace TidyWindow.App.Tests;

internal static class WpfTestHelper
{
    private static readonly Lazy<TaskScheduler> Scheduler = new(CreateStaScheduler, LazyThreadSafetyMode.ExecutionAndPublication);

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
}
