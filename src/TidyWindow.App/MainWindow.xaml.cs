using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels;
using TidyWindow.App.Views;

namespace TidyWindow.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly NavigationService _navigationService;
    private readonly MainViewModel _viewModel;
    private readonly ITrayService _trayService;
    private readonly UserPreferencesService _preferences;
    private readonly PulseGuardService _pulseGuard;
    private readonly IAutomationWorkTracker _workTracker;
    private System.Windows.Navigation.NavigationService? _frameNavigationService;
    private bool _contentDetached;
    private CancellationTokenSource? _idleTrimCts;
    private bool _initialNavigationCompleted;
    private bool _autoCloseArmed;
    private DispatcherTimer? _traySweepTimer;
    private static readonly TimeSpan TraySweepIntervalMinimum = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TraySweepIntervalMaximum = TimeSpan.FromMinutes(10);

    public MainWindow(MainViewModel viewModel, NavigationService navigationService, ITrayService trayService, UserPreferencesService preferences, PulseGuardService pulseGuard, IAutomationWorkTracker workTracker)
    {
        InitializeComponent();
        _navigationService = navigationService;
        _viewModel = viewModel;
        _trayService = trayService ?? throw new ArgumentNullException(nameof(trayService));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _pulseGuard = pulseGuard ?? throw new ArgumentNullException(nameof(pulseGuard));
        _workTracker = workTracker ?? throw new ArgumentNullException(nameof(workTracker));
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ToggleLoadingOverlay(_viewModel.IsShellLoading);
        Loaded += OnLoaded;
        StateChanged += OnStateChanged;
        _trayService.Attach(this);
        UpdateMaximizeVisualState();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _viewModel.BeginShellLoad();
        _navigationService.Initialize(ContentFrame);
        _frameNavigationService = ContentFrame.NavigationService;
        if (_frameNavigationService is not null)
        {
            _frameNavigationService.LoadCompleted += OnInitialNavigationCompleted;
        }
        _viewModel.Activate();
    }

    private async void OnInitialNavigationCompleted(object? sender, System.Windows.Navigation.NavigationEventArgs e)
    {
        if (_initialNavigationCompleted)
        {
            return;
        }

        _initialNavigationCompleted = true;

        if (_frameNavigationService is not null)
        {
            _frameNavigationService.LoadCompleted -= OnInitialNavigationCompleted;
        }

        await Task.Delay(250);
        _viewModel.CompleteShellLoad();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsShellLoading))
        {
            ToggleLoadingOverlay(_viewModel.IsShellLoading);
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        UpdateMaximizeVisualState();

        if (_contentDetached && WindowState != WindowState.Minimized)
        {
            CancelIdleTrim();
            RestoreContentAfterBackground();
        }

        if (WindowState == WindowState.Minimized && _preferences.Current.RunInBackground)
        {
            StartTraySweepTimerIfNeeded();
        }
        else
        {
            StopTraySweepTimer();
        }
    }

    private void ToggleLoadingOverlay(bool show)
    {
        if (LoadingOverlay is null)
        {
            return;
        }

        LoadingOverlay.BeginAnimation(OpacityProperty, null);

        var duration = TimeSpan.FromMilliseconds(260);
        var animation = new DoubleAnimation
        {
            To = show ? 1d : 0d,
            Duration = new Duration(duration),
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseInOut }
        };

        if (show)
        {
            LoadingOverlay.Opacity = 0d;
            LoadingOverlay.Visibility = Visibility.Visible;
            LoadingOverlay.IsHitTestVisible = true;
            animation.From = 0d;
            LoadingOverlay.BeginAnimation(OpacityProperty, animation);
        }
        else
        {
            animation.Completed += (_, _) =>
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                LoadingOverlay.IsHitTestVisible = false;
            };

            LoadingOverlay.BeginAnimation(OpacityProperty, animation);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        CancelAutoCloseSubscription();
        base.OnClosed(e);

        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        if (_frameNavigationService is not null)
        {
            _frameNavigationService.LoadCompleted -= OnInitialNavigationCompleted;
            _frameNavigationService = null;
        }

        StateChanged -= OnStateChanged;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_trayService.IsExitRequested && _preferences.Current.RunInBackground)
        {
            e.Cancel = true;
            _trayService.HideToTray(showHint: true);

            ScheduleIdleTrimIfSafe();
            StartTraySweepTimerIfNeeded();
            return;
        }

        if (!_preferences.Current.RunInBackground)
        {
            var activeWork = _workTracker.GetActiveWork();
            if (activeWork.Count > 0)
            {
                var decision = _pulseGuard.PromptPendingAutomation(activeWork);
                if (decision == PendingAutomationDecision.WaitForCompletion)
                {
                    e.Cancel = true;
                    ArmAutoClose();
                    return;
                }

                CancelAutoCloseSubscription();
            }
        }

        _trayService.PrepareForExit();
        base.OnClosing(e);
    }

    private void ArmAutoClose()
    {
        if (_autoCloseArmed)
        {
            return;
        }

        _autoCloseArmed = true;
        _workTracker.ActiveWorkChanged += OnActiveWorkChanged;
        _viewModel.SetStatusMessage("Waiting for automation to finish before closing...");
    }

    private void CancelAutoCloseSubscription()
    {
        if (!_autoCloseArmed)
        {
            return;
        }

        _workTracker.ActiveWorkChanged -= OnActiveWorkChanged;
        _autoCloseArmed = false;
    }

    private async void OnActiveWorkChanged(object? sender, EventArgs e)
    {
        if (!_autoCloseArmed)
        {
            return;
        }

        if (_workTracker.HasActiveWork)
        {
            return;
        }

        await Task.Delay(400).ConfigureAwait(false);

        if (_workTracker.HasActiveWork)
        {
            return;
        }

        CancelAutoCloseSubscription();

        Dispatcher.Invoke(() =>
        {
            _trayService.PrepareForExit();
            Close();
        });
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            e.Handled = true;
            ToggleWindowState();
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // Swallow drag exceptions that can happen during state transitions.
            }
        }
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void DetachContentForBackground()
    {
        if (_contentDetached)
        {
            return;
        }

        if (_workTracker.HasActiveWork)
        {
            return; // Do not clear UI while automation is active.
        }

        if (ContentFrame is null)
        {
            return;
        }

        if (_navigationService.IsInitialized)
        {
            _viewModel.NavigateTo(typeof(BootstrapPage));
        }

        var nav = ContentFrame.NavigationService;
        if (nav is not null)
        {
            while (nav.RemoveBackEntry() is not null) { }
            nav.Content = null;
        }

        ContentFrame.Content = null; // Release the visual tree so memory can drop while in the tray.
        _navigationService.ClearCache();

        // Hint the GC so large visuals are reclaimed promptly; does not affect automation services.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        TrimWorkingSet();
        _contentDetached = true;
    }

    private void RestoreContentAfterBackground()
    {
        if (!_contentDetached)
        {
            return;
        }

        CancelIdleTrim();
        _contentDetached = false;
        _viewModel.Activate(); // Re-navigate to the selected item (or default) to rebuild the UI.
    }

    private void ScheduleIdleTrimIfSafe()
    {
        CancelIdleTrim();

        if (!_preferences.Current.RunInBackground)
        {
            return;
        }

        if (WindowState != WindowState.Minimized)
        {
            return;
        }

        if (_workTracker.HasActiveWork)
        {
            return;
        }

        _idleTrimCts = new CancellationTokenSource();
        _ = RunIdleTrimAsync(_idleTrimCts.Token);
    }

    private async Task RunIdleTrimAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(5), token);

            if (token.IsCancellationRequested)
            {
                return;
            }

            if (WindowState != WindowState.Minimized || !_preferences.Current.RunInBackground)
            {
                return;
            }

            if (_workTracker.HasActiveWork)
            {
                return; // Skip if any automation is running.
            }

            _navigationService.SweepCache();
            _viewModel.LogActivityInformation("PulseGuard", "Idle tray cache sweep executed.");
            TrimWorkingSet();
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation.
        }
    }

    private void StartTraySweepTimerIfNeeded()
    {
        if (!_preferences.Current.RunInBackground || WindowState != WindowState.Minimized)
        {
            StopTraySweepTimer();
            return;
        }

        if (_traySweepTimer is null)
        {
            _traySweepTimer = new DispatcherTimer();
            _traySweepTimer.Tick += OnTraySweepTick;
        }

        ScheduleNextTraySweep();
    }

    private void StopTraySweepTimer()
    {
        if (_traySweepTimer is null)
        {
            return;
        }

        _traySweepTimer.Stop();
        _traySweepTimer.Tick -= OnTraySweepTick;
        _traySweepTimer = null;
    }

    private void OnTraySweepTick(object? sender, EventArgs e)
    {
        if (!_preferences.Current.RunInBackground || WindowState != WindowState.Minimized)
        {
            StopTraySweepTimer();
            return;
        }

        if (_workTracker.HasActiveWork)
        {
            ScheduleNextTraySweep();
            return;
        }

        _navigationService.SweepCache();
        ScheduleNextTraySweep();
    }

    private void ScheduleNextTraySweep()
    {
        if (_traySweepTimer is null)
        {
            return;
        }

        _navigationService.SweepCache();

        var nextExpiry = _navigationService.GetNextCacheExpiryUtc();
        if (nextExpiry is null)
        {
            StopTraySweepTimer();
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var delay = nextExpiry.Value - now;
        if (delay < TraySweepIntervalMinimum)
        {
            delay = TraySweepIntervalMinimum;
        }
        else if (delay > TraySweepIntervalMaximum)
        {
            delay = TraySweepIntervalMaximum;
        }

        _traySweepTimer.Interval = delay;
        if (!_traySweepTimer.IsEnabled)
        {
            _traySweepTimer.Start();
        }
    }

    private void CancelIdleTrim()
    {
        _idleTrimCts?.Cancel();
        _idleTrimCts?.Dispose();
        _idleTrimCts = null;
    }

    private static void TrimWorkingSet()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var process = Process.GetCurrentProcess();
            _ = EmptyWorkingSet(process.Handle);
            _ = SetProcessWorkingSetSize(process.Handle, new IntPtr(-1), new IntPtr(-1));
        }
        catch
        {
            // Best-effort trim; ignore failures.
        }
    }

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

    private void MaximizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        ToggleWindowState();
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateMaximizeVisualState();
    }

    private void UpdateMaximizeVisualState()
    {
        if (MaximizeGlyph is null)
        {
            return;
        }

        MaximizeGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        MaximizeGlyph.ToolTip = WindowState == WindowState.Maximized ? "Restore Down" : "Maximize";
    }
}