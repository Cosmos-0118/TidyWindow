using System;
using System.Windows.Threading;

namespace TidyWindow.App.Infrastructure;

/// <summary>
/// Lightweight UI-thread debouncer for high-frequency inputs (for example, text search boxes).
/// </summary>
public sealed class UiDebounceDispatcher : IDisposable
{
    private readonly DispatcherTimer _timer;
    private Action? _pendingAction;

    public UiDebounceDispatcher(TimeSpan interval)
    {
        _timer = new DispatcherTimer
        {
            Interval = interval
        };
        _timer.Tick += OnTimerTick;
    }

    public TimeSpan Interval
    {
        get => _timer.Interval;
        set => _timer.Interval = value;
    }

    public void Schedule(Action action)
    {
        if (action is null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        _timer.Stop();
        _pendingAction = action;
        _timer.Start();
    }

    public void Flush()
    {
        if (_pendingAction is null)
        {
            _timer.Stop();
            return;
        }

        _timer.Stop();
        var callback = _pendingAction;
        _pendingAction = null;
        callback();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        Flush();
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _pendingAction = null;
    }
}
