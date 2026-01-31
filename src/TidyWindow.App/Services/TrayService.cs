using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using TidyWindow.App.ViewModels;
using TidyWindow.App.Views;
using WpfApplication = System.Windows.Application;

namespace TidyWindow.App.Services;

/// <summary>
/// Manages the system tray icon with robust recovery mechanisms.
/// </summary>
/// <remarks>
/// Reliability improvements:
/// 1. Automatic icon recreation if it becomes invisible/orphaned
/// 2. Periodic health checks when running in background mode
/// 3. Thread-safe icon operations with proper dispatcher handling
/// 4. Graceful handling of explorer.exe crashes/restarts
/// </remarks>
public sealed class TrayService : ITrayService
{
    private readonly NavigationService _navigationService;
    private readonly UserPreferencesService _preferencesService;
    private readonly ActivityLogService _activityLog;
    private readonly MainViewModel _mainViewModel;
    private readonly IAutomationWorkTracker _workTracker;
    private readonly SmartPageCache _pageCache;
    private readonly object _iconLock = new();

    private NotifyIcon? _notifyIcon;
    private ContextMenuStrip? _contextMenu;
    private Window? _window;
    private bool _explicitExitRequested;
    private bool _hasShownBackgroundHint;
    private PulseGuardNotification? _lastNotification;
    private ToolStripMenuItem? _notificationsMenuItem;
    private bool _disposed;
    private DispatcherTimer? _healthCheckTimer;
    private int _iconCreationAttempts;
    private const int MaxIconRecreationAttempts = 3;
    private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromMinutes(2);

    public TrayService(NavigationService navigationService, UserPreferencesService preferencesService, ActivityLogService activityLog, MainViewModel mainViewModel, SmartPageCache pageCache, IAutomationWorkTracker workTracker)
    {
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        _preferencesService = preferencesService ?? throw new ArgumentNullException(nameof(preferencesService));
        _activityLog = activityLog ?? throw new ArgumentNullException(nameof(activityLog));
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        _pageCache = pageCache ?? throw new ArgumentNullException(nameof(pageCache));
        _workTracker = workTracker ?? throw new ArgumentNullException(nameof(workTracker));

        WeakEventManager<UserPreferencesService, UserPreferencesChangedEventArgs>.AddHandler(_preferencesService, nameof(UserPreferencesService.PreferencesChanged), OnPreferencesChanged);

        // Register for shell restart notifications (explorer.exe crash/restart)
        RegisterShellRestartHandler();
    }

    public bool IsExitRequested => _explicitExitRequested;

    /// <summary>
    /// Gets whether the tray icon is currently healthy and visible.
    /// </summary>
    public bool IsIconHealthy
    {
        get
        {
            lock (_iconLock)
            {
                return _notifyIcon is not null && _notifyIcon.Visible;
            }
        }
    }

    public void Attach(Window window)
    {
        if (window is null)
        {
            throw new ArgumentNullException(nameof(window));
        }

        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TrayService));
        }

        _window = window;
        EnsureTrayIconCreated();
    }

    /// <summary>
    /// Ensures the tray icon exists and is visible.
    /// Safe to call multiple times - will recreate if needed.
    /// </summary>
    public void EnsureTrayIconCreated()
    {
        lock (_iconLock)
        {
            if (_disposed)
            {
                return;
            }

            if (_notifyIcon is not null && _notifyIcon.Visible)
            {
                return; // Icon already exists and is healthy
            }

            // Clean up any existing icon before recreating
            CleanupIconUnsafe();

            try
            {
                _notifyIcon = new NotifyIcon
                {
                    Icon = ResolveIcon(),
                    Visible = true,
                    Text = BuildTooltip(_preferencesService.Current)
                };

                _notifyIcon.DoubleClick += OnNotifyIconDoubleClick;
                _notifyIcon.BalloonTipClicked += OnBalloonTipClicked;

                _contextMenu = BuildContextMenu();
                _notifyIcon.ContextMenuStrip = _contextMenu;

                _iconCreationAttempts = 0; // Reset on success
            }
            catch (Exception ex)
            {
                _iconCreationAttempts++;
                _activityLog.LogWarning("TrayIcon", $"Failed to create tray icon (attempt {_iconCreationAttempts}): {ex.Message}");

                if (_iconCreationAttempts < MaxIconRecreationAttempts)
                {
                    // Schedule a retry after a short delay
                    ScheduleIconRetry();
                }
            }
        }
    }

    private void ScheduleIconRetry()
    {
        _window?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Thread.Sleep(500); // Brief delay before retry
            EnsureTrayIconCreated();
        }));
    }

    private void OnNotifyIconDoubleClick(object? sender, EventArgs e)
    {
        ShowMainWindow();
    }

    public void ShowMainWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Dispatcher.Invoke(() =>
        {
            try
            {
                if (!_window.IsVisible)
                {
                    _window.Show();
                }

                if (_window.WindowState == WindowState.Minimized)
                {
                    _window.WindowState = WindowState.Normal;
                }

                _window.Activate();

                // Bring to foreground even if another window has focus
                SetForegroundWindowEx(_window);
            }
            catch (Exception ex)
            {
                _activityLog.LogWarning("TrayIcon", $"Failed to show main window: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Attempts to bring the window to the foreground, handling edge cases.
    /// </summary>
    private static void SetForegroundWindowEx(Window window)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(window);
            var hwnd = helper.Handle;
            if (hwnd != IntPtr.Zero)
            {
                // Flash the taskbar button briefly to get permission to steal focus
                NativeMethods.SetForegroundWindow(hwnd);
            }
        }
        catch
        {
            // Non-critical failure
        }
    }

    public void HideToTray(bool showHint)
    {
        if (_window is null)
        {
            return;
        }

        _window.Dispatcher.Invoke(() =>
        {
            var wasVisible = _window.IsVisible;

            if (wasVisible)
            {
                _window.Hide();
            }

            // Ensure tray icon is healthy when hiding to tray
            EnsureTrayIconCreated();
            StartHealthCheckTimer();

            // Trim cached pages only when they are expired and no automation is active.
            if (!_workTracker.HasActiveWork)
            {
                _pageCache.SweepExpired();
            }

            if (showHint && wasVisible)
            {
                _activityLog.LogInformation("BackgroundMode", "TidyWindow continues running from the system tray.");

                if (!_hasShownBackgroundHint)
                {
                    _hasShownBackgroundHint = true;
                    ShowBalloon("TidyWindow is still running", "PulseGuard will keep watching automation logs while the app stays in the tray.", ToolTipIcon.Info);
                }
            }
        });
    }

    private void StartHealthCheckTimer()
    {
        if (_healthCheckTimer is not null)
        {
            return;
        }

        _healthCheckTimer = new DispatcherTimer
        {
            Interval = HealthCheckInterval
        };
        _healthCheckTimer.Tick += OnHealthCheckTick;
        _healthCheckTimer.Start();
    }

    private void StopHealthCheckTimer()
    {
        if (_healthCheckTimer is null)
        {
            return;
        }

        _healthCheckTimer.Stop();
        _healthCheckTimer.Tick -= OnHealthCheckTick;
        _healthCheckTimer = null;
    }

    private void OnHealthCheckTick(object? sender, EventArgs e)
    {
        // If the window is visible, we don't need health checks
        if (_window?.IsVisible == true)
        {
            StopHealthCheckTimer();
            return;
        }

        // Check if tray icon is still healthy
        if (!IsIconHealthy)
        {
            _activityLog.LogWarning("TrayIcon", "Tray icon became unhealthy, attempting recovery...");
            _iconCreationAttempts = 0; // Reset attempts for recovery
            EnsureTrayIconCreated();
        }
    }

    public void ShowNotification(PulseGuardNotification notification)
    {
        // Ensure icon exists before trying to show notification
        EnsureTrayIconCreated();

        lock (_iconLock)
        {
            if (_notifyIcon is null)
            {
                return;
            }

            var icon = notification.Kind switch
            {
                PulseGuardNotificationKind.ActionRequired => ToolTipIcon.Warning,
                PulseGuardNotificationKind.SuccessDigest => ToolTipIcon.Info,
                _ => ToolTipIcon.None
            };

            _lastNotification = notification;
            ShowBalloonInternal(notification.Title, notification.Message, icon);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        StopHealthCheckTimer();
        WeakEventManager<UserPreferencesService, UserPreferencesChangedEventArgs>.RemoveHandler(_preferencesService, nameof(UserPreferencesService.PreferencesChanged), OnPreferencesChanged);

        lock (_iconLock)
        {
            CleanupIconUnsafe();
        }
    }

    /// <summary>
    /// Cleans up icon resources. Must be called while holding _iconLock.
    /// </summary>
    private void CleanupIconUnsafe()
    {
        if (_contextMenu is not null)
        {
            _contextMenu.Dispose();
            _contextMenu = null;
        }

        if (_notifyIcon is not null)
        {
            try
            {
                _notifyIcon.Visible = false;
                _notifyIcon.DoubleClick -= OnNotifyIconDoubleClick;
                _notifyIcon.BalloonTipClicked -= OnBalloonTipClicked;
                _notifyIcon.Dispose();
            }
            catch
            {
                // Best effort cleanup
            }

            _notifyIcon = null;
        }

        _notificationsMenuItem = null;
    }

    public void PrepareForExit()
    {
        _explicitExitRequested = true;
        StopHealthCheckTimer();

        lock (_iconLock)
        {
            CleanupIconUnsafe();
        }
    }

    public void ResetExitRequest()
    {
        _explicitExitRequested = false;
    }

    /// <summary>
    /// Registers for shell restart notifications so we can recreate the tray icon
    /// if explorer.exe crashes and restarts.
    /// </summary>
    private void RegisterShellRestartHandler()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            // Register the TaskbarCreated message which Windows broadcasts when explorer restarts
            NativeMethods.TaskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        }
        catch
        {
            // Non-critical; we still have the health check timer as backup
        }
    }

    /// <summary>
    /// Called when the shell (explorer.exe) restarts. Recreates the tray icon.
    /// </summary>
    public void OnShellRestarted()
    {
        _activityLog.LogInformation("TrayIcon", "Shell restarted, recreating tray icon...");
        _iconCreationAttempts = 0;

        lock (_iconLock)
        {
            CleanupIconUnsafe();
        }

        EnsureTrayIconCreated();
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();

        var openItem = new ToolStripMenuItem("Open TidyWindow");
        openItem.Click += (_, _) => ShowMainWindow();
        menu.Items.Add(openItem);

        var logsItem = new ToolStripMenuItem("View Logs");
        logsItem.Click += (_, _) => NavigateToLogs();
        menu.Items.Add(logsItem);

        menu.Items.Add(new ToolStripSeparator());

        _notificationsMenuItem = new ToolStripMenuItem("Pause PulseGuard notifications")
        {
            Checked = !_preferencesService.Current.NotificationsEnabled,
            CheckOnClick = false
        };
        _notificationsMenuItem.Click += (_, _) => ToggleNotifications();
        menu.Items.Add(_notificationsMenuItem);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit TidyWindow");
        exitItem.Click += (_, _) => RequestExit();
        menu.Items.Add(exitItem);

        return menu;
    }

    private void ToggleNotifications()
    {
        var current = _preferencesService.Current;
        var enable = !current.NotificationsEnabled;
        _preferencesService.SetNotificationsEnabled(enable);
        _activityLog.LogInformation("PulseGuard", enable ? "Notifications resumed from the tray." : "Notifications paused from the tray.");
    }

    private void RequestExit()
    {
        if (_window is null)
        {
            WpfApplication.Current?.Shutdown();
            return;
        }

        _window.Dispatcher.Invoke(() =>
        {
            _explicitExitRequested = true;
            WpfApplication.Current?.Shutdown();
        });
    }

    private void OnPreferencesChanged(object? sender, UserPreferencesChangedEventArgs args)
    {
        if (_notifyIcon is null)
        {
            return;
        }

        var tooltip = BuildTooltip(args.Preferences);

        if (_window is not null)
        {
            _window.Dispatcher.Invoke(() => UpdateTrayState(tooltip, args.Preferences));
        }
        else
        {
            UpdateTrayState(tooltip, args.Preferences);
        }
    }

    private void OnBalloonTipClicked(object? sender, EventArgs e)
    {
        if (_lastNotification is null)
        {
            return;
        }

        var notification = _lastNotification;
        _lastNotification = null;

        if (notification.NavigateToLogs)
        {
            NavigateToLogs();
        }
    }

    public void NavigateToLogs()
    {
        ShowMainWindow();
        if (_navigationService.IsInitialized)
        {
            _mainViewModel.NavigateTo(typeof(LogsPage));
        }
    }

    private void UpdateTrayState(string tooltip, UserPreferences preferences)
    {
        lock (_iconLock)
        {
            if (_notifyIcon is not null)
            {
                try
                {
                    _notifyIcon.Text = tooltip;
                }
                catch
                {
                    // Icon may have become invalid
                }
            }

            if (_notificationsMenuItem is not null)
            {
                _notificationsMenuItem.Checked = !preferences.NotificationsEnabled;
            }
        }
    }

    private void ShowBalloon(string title, string message, ToolTipIcon icon)
    {
        lock (_iconLock)
        {
            ShowBalloonInternal(title, message, icon);
        }
    }

    /// <summary>
    /// Shows a balloon notification. Must be called while holding _iconLock or from within lock scope.
    /// </summary>
    private void ShowBalloonInternal(string title, string message, ToolTipIcon icon)
    {
        if (_notifyIcon is null)
        {
            return;
        }

        try
        {
            if (_window is not null)
            {
                _window.Dispatcher.Invoke(() =>
                {
                    _notifyIcon.BalloonTipTitle = title;
                    _notifyIcon.BalloonTipText = message;
                    _notifyIcon.BalloonTipIcon = icon;
                    _notifyIcon.ShowBalloonTip(5000);
                });
            }
            else
            {
                _notifyIcon.BalloonTipTitle = title;
                _notifyIcon.BalloonTipText = message;
                _notifyIcon.BalloonTipIcon = icon;
                _notifyIcon.ShowBalloonTip(5000);
            }
        }
        catch (Exception ex)
        {
            _activityLog.LogWarning("TrayIcon", $"Failed to show balloon notification: {ex.Message}");
        }
    }

    private static Icon ResolveIcon()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var path = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var icon = Icon.ExtractAssociatedIcon(path);
                if (icon is not null)
                {
                    return icon;
                }
            }
        }
        catch
        {
        }

        return SystemIcons.Application;
    }

    private static string BuildTooltip(UserPreferences preferences)
    {
        var text = preferences switch
        {
            { PulseGuardEnabled: true, NotificationsEnabled: true } => "TidyWindow — PulseGuard standing watch",
            { PulseGuardEnabled: true, NotificationsEnabled: false } => "TidyWindow — PulseGuard muted",
            { PulseGuardEnabled: false } => "TidyWindow — PulseGuard paused",
            _ => "TidyWindow"
        };

        return text.Length <= 63 ? text : text[..63];
    }

    /// <summary>
    /// Native methods for window and shell operations.
    /// </summary>
    private static class NativeMethods
    {
        /// <summary>
        /// Cached TaskbarCreated message ID, set during initialization.
        /// </summary>
        public static uint TaskbarCreatedMessage { get; set; }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern uint RegisterWindowMessage(string lpString);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
