using System;
using System.Windows;
using System.Windows.Threading;
using WpfApplication = System.Windows.Application;

namespace TidyWindow.App.Services;

public interface IRelativeTimeTicker
{
    event EventHandler Tick;
}

/// <summary>
/// Lightweight dispatcher timer that raises a shared tick event so view models can refresh
/// relative-time UI strings without duplicating timers.
/// </summary>
public sealed class RelativeTimeTicker : IRelativeTimeTicker, IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly WpfApplication? _application;
    private readonly Dispatcher _dispatcher;
    private Window? _mainWindow;
    private bool _isDisposed;

    public RelativeTimeTicker()
    {
        _application = WpfApplication.Current;
        _dispatcher = _application?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _timer.Tick += OnTick;
        _timer.Start();

        if (_application is not null)
        {
            _application.Exit += OnApplicationExit;
            _dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(AttachToMainWindow));
        }
        else
        {
            AttachToMainWindow();
        }
    }

    public event EventHandler? Tick;

    private void OnTick(object? sender, EventArgs e)
    {
        Tick?.Invoke(this, EventArgs.Empty);
    }

    private void OnApplicationExit(object? sender, ExitEventArgs e)
    {
        StopTimer();
    }

    private void AttachToMainWindow()
    {
        if (_isDisposed)
        {
            return;
        }

        var window = _application?.MainWindow ?? WpfApplication.Current?.MainWindow;
        if (window is null)
        {
            if (_application is not null)
            {
                _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(AttachToMainWindow));
            }
            return;
        }

        if (ReferenceEquals(_mainWindow, window))
        {
            return;
        }

        _mainWindow = window;
        _mainWindow.IsVisibleChanged += OnMainWindowVisibilityChanged;
        _mainWindow.Closed += OnMainWindowClosed;
        UpdateTimerState(_mainWindow.IsVisible);
    }

    private void OnMainWindowVisibilityChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is bool isVisible)
        {
            UpdateTimerState(isVisible);
        }
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        if (_mainWindow is not null)
        {
            _mainWindow.IsVisibleChanged -= OnMainWindowVisibilityChanged;
            _mainWindow.Closed -= OnMainWindowClosed;
            _mainWindow = null;
        }
        StopTimer();
    }

    private void UpdateTimerState(bool shouldRun)
    {
        if (shouldRun)
        {
            if (!_timer.IsEnabled)
            {
                _timer.Start();
                Tick?.Invoke(this, EventArgs.Empty); // Refresh immediately when UI returns.
            }
        }
        else
        {
            StopTimer();
        }
    }

    private void StopTimer()
    {
        if (_timer.IsEnabled)
        {
            _timer.Stop();
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _timer.Tick -= OnTick;
        StopTimer();
        if (_application is not null)
        {
            _application.Exit -= OnApplicationExit;
        }
        if (_mainWindow is not null)
        {
            _mainWindow.IsVisibleChanged -= OnMainWindowVisibilityChanged;
            _mainWindow.Closed -= OnMainWindowClosed;
            _mainWindow = null;
        }
    }
}
