using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using TidyWindow.App.ViewModels;
using TidyWindow.App.Views;
using WpfApplication = System.Windows.Application;

namespace TidyWindow.App.Services;

public sealed class TrayService : ITrayService
{
    private readonly NavigationService _navigationService;
    private readonly UserPreferencesService _preferencesService;
    private readonly ActivityLogService _activityLog;
    private readonly MainViewModel _mainViewModel;
    private readonly IAutomationWorkTracker _workTracker;
    private readonly SmartPageCache _pageCache;

    private NotifyIcon? _notifyIcon;
    private Window? _window;
    private bool _explicitExitRequested;
    private bool _hasShownBackgroundHint;
    private PulseGuardNotification? _lastNotification;
    private ToolStripMenuItem? _notificationsMenuItem;
    private bool _disposed;

    public TrayService(NavigationService navigationService, UserPreferencesService preferencesService, ActivityLogService activityLog, MainViewModel mainViewModel, SmartPageCache pageCache, IAutomationWorkTracker workTracker)
    {
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        _preferencesService = preferencesService ?? throw new ArgumentNullException(nameof(preferencesService));
        _activityLog = activityLog ?? throw new ArgumentNullException(nameof(activityLog));
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        _pageCache = pageCache ?? throw new ArgumentNullException(nameof(pageCache));
        _workTracker = workTracker ?? throw new ArgumentNullException(nameof(workTracker));

        WeakEventManager<UserPreferencesService, UserPreferencesChangedEventArgs>.AddHandler(_preferencesService, nameof(UserPreferencesService.PreferencesChanged), OnPreferencesChanged);
    }

    public bool IsExitRequested => _explicitExitRequested;

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

        if (_notifyIcon is not null)
        {
            return;
        }

        _notifyIcon = new NotifyIcon
        {
            Icon = ResolveIcon(),
            Visible = true,
            Text = BuildTooltip(_preferencesService.Current)
        };

        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();
        _notifyIcon.BalloonTipClicked += OnBalloonTipClicked;

        var menu = BuildContextMenu();
        _notifyIcon.ContextMenuStrip = menu;
    }

    public void ShowMainWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Dispatcher.Invoke(() =>
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
        });
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

            // Trim cached pages even if the window was already hidden, as long as no automation is active.
            if (!_workTracker.HasActiveWork)
            {
                // Only remove entries that are already expired to avoid cold-start lag on resume.
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

    public void ShowNotification(PulseGuardNotification notification)
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
        ShowBalloon(notification.Title, notification.Message, icon);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        WeakEventManager<UserPreferencesService, UserPreferencesChangedEventArgs>.RemoveHandler(_preferencesService, nameof(UserPreferencesService.PreferencesChanged), OnPreferencesChanged);

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.BalloonTipClicked -= OnBalloonTipClicked;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
    }

    public void PrepareForExit()
    {
        _explicitExitRequested = true;
        _notifyIcon?.Dispose();
        _notifyIcon = null;
    }

    public void ResetExitRequest()
    {
        _explicitExitRequested = false;
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
        if (_notifyIcon is not null)
        {
            _notifyIcon.Text = tooltip;
        }

        if (_notificationsMenuItem is not null)
        {
            _notificationsMenuItem.Checked = !preferences.NotificationsEnabled;
        }
    }

    private void ShowBalloon(string title, string message, ToolTipIcon icon)
    {
        if (_notifyIcon is null)
        {
            return;
        }

        _window?.Dispatcher.Invoke(() =>
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = message;
            _notifyIcon.BalloonTipIcon = icon;
            _notifyIcon.ShowBalloonTip(5000);
        });
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
}
