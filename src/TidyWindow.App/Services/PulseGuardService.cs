using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using WpfApplication = System.Windows.Application;

namespace TidyWindow.App.Services;

public sealed class PulseGuardService : IDisposable
{
    private static readonly TimeSpan SuccessCooldown = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan InsightCooldown = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ActionCooldown = TimeSpan.FromMinutes(1);

    private readonly ActivityLogService _activityLog;
    private readonly UserPreferencesService _preferencesService;
    private readonly ITrayService _trayService;
    private readonly Dispatcher _dispatcher;
    private readonly IHighFrictionPromptService _promptService;
    private readonly Queue<PulseGuardNotification> _pending = new();
    private readonly Dictionary<string, DateTimeOffset> _cooldowns = new(StringComparer.OrdinalIgnoreCase);
    private bool _processing;
    private bool _disposed;
    private UserPreferences _preferencesSnapshot;

    public PulseGuardService(ActivityLogService activityLog, UserPreferencesService preferencesService, ITrayService trayService, IHighFrictionPromptService promptService)
    {
        _activityLog = activityLog ?? throw new ArgumentNullException(nameof(activityLog));
        _preferencesService = preferencesService ?? throw new ArgumentNullException(nameof(preferencesService));
        _trayService = trayService ?? throw new ArgumentNullException(nameof(trayService));
        _promptService = promptService ?? throw new ArgumentNullException(nameof(promptService));
        _dispatcher = WpfApplication.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _preferencesSnapshot = preferencesService.Current;

        _activityLog.EntryAdded += OnEntryAdded;
        WeakEventManager<UserPreferencesService, UserPreferencesChangedEventArgs>.AddHandler(_preferencesService, nameof(UserPreferencesService.PreferencesChanged), OnPreferencesChanged);
    }

    public PendingAutomationDecision PromptPendingAutomation(IReadOnlyList<AutomationWorkItem> workItems)
    {
        if (workItems is null || workItems.Count == 0)
        {
            return PendingAutomationDecision.CloseAnyway;
        }

        PendingAutomationDecision decision = PendingAutomationDecision.WaitAndCloseAfterCompletion;

        _dispatcher.Invoke(() =>
        {
            var window = new Views.Dialogs.PendingAutomationPromptWindow(workItems)
            {
                Owner = WpfApplication.Current?.MainWindow
            };

            window.ShowDialog();
            decision = window.Result;
        });

        var detailLines = workItems
            .Select(item => $"{item.Type}: {item.Description}")
            .ToArray();

        switch (decision)
        {
            case PendingAutomationDecision.WaitAndCloseAfterCompletion:
                _activityLog.LogWarning("PulseGuard", "Exit postponed until active automation finishes.", detailLines);
                break;
            case PendingAutomationDecision.WaitWithoutClosing:
                _activityLog.LogInformation("PulseGuard", "Exit cancelled. Automation will continue while the app stays open.", detailLines);
                break;
            default:
                _activityLog.LogWarning("PulseGuard", "Exit forced while automation was still active.", detailLines);
                break;
        }

        return decision;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _activityLog.EntryAdded -= OnEntryAdded;
        WeakEventManager<UserPreferencesService, UserPreferencesChangedEventArgs>.RemoveHandler(_preferencesService, nameof(UserPreferencesService.PreferencesChanged), OnPreferencesChanged);
    }

    private void OnPreferencesChanged(object? sender, UserPreferencesChangedEventArgs args)
    {
        _preferencesSnapshot = args.Preferences;
    }

    private void OnEntryAdded(object? sender, ActivityLogEventArgs args)
    {
        var entry = args.Entry;
        var preferences = _preferencesSnapshot;

        if (!preferences.PulseGuardEnabled)
        {
            return;
        }

        if (string.Equals(entry.Source, "PulseGuard", StringComparison.OrdinalIgnoreCase))
        {
            // Prevent PulseGuard-generated entries from triggering duplicate prompts.
            return;
        }

        var scenario = PulseGuardHeuristics.ResolveHighFrictionScenario(entry);
        if (scenario != HighFrictionScenario.None)
        {
            _ = _dispatcher.InvokeAsync(() => _promptService.TryShowPrompt(scenario, entry));
        }

        if (entry.Level == ActivityLogLevel.Information)
        {
            if (string.Equals(entry.Source, "Status", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.Equals(entry.Source, "BackgroundMode", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.Equals(entry.Message, "Ready", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        if (PulseGuardHeuristics.ShouldSuppressNotification(entry))
        {
            return;
        }

        var kind = PulseGuardHeuristics.ResolveKind(entry);
        if (kind is PulseGuardNotificationKind.SuccessDigest && !preferences.PulseGuardShowSuccessSummaries)
        {
            return;
        }

        if (kind is PulseGuardNotificationKind.ActionRequired && !preferences.PulseGuardShowActionAlerts)
        {
            return;
        }

        if (!preferences.NotificationsEnabled)
        {
            return;
        }

        if (preferences.NotifyOnlyWhenInactive && IsMainWindowActive())
        {
            return;
        }

        if (!ShouldDispatch(entry, kind))
        {
            return;
        }

        var notification = CreateNotification(entry, kind);
        Enqueue(notification);
    }

    private bool IsMainWindowActive()
    {
        if (_dispatcher.CheckAccess())
        {
            return IsMainWindowActiveOnUiThread();
        }

        return _dispatcher.Invoke(IsMainWindowActiveOnUiThread);
    }

    private static bool IsMainWindowActiveOnUiThread()
    {
        var window = WpfApplication.Current?.MainWindow;
        if (window is null)
        {
            return false;
        }

        if (!window.IsVisible || window.WindowState == WindowState.Minimized)
        {
            return false; // Treat hidden/minimized as inactive so notifications still fire.
        }

        if (window.IsActive || window.IsKeyboardFocusWithin)
        {
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var interop = new WindowInteropHelper(window);
        var handle = interop.Handle;

        if (handle == IntPtr.Zero && window.IsLoaded)
        {
            handle = interop.EnsureHandle();
        }

        if (handle == IntPtr.Zero)
        {
            return false;
        }

        return NativeWindowHelper.IsProcessWindowInForeground(handle);
    }

    private bool ShouldDispatch(ActivityLogEntry entry, PulseGuardNotificationKind kind)
    {
        string cooldownKey = $"{kind}:{entry.Source}";
        var now = DateTimeOffset.UtcNow;
        var window = kind switch
        {
            PulseGuardNotificationKind.SuccessDigest => SuccessCooldown,
            PulseGuardNotificationKind.ActionRequired => ActionCooldown,
            _ => InsightCooldown
        };

        lock (_cooldowns)
        {
            if (_cooldowns.TryGetValue(cooldownKey, out var previous) && now - previous < window)
            {
                return false;
            }

            _cooldowns[cooldownKey] = now;
        }

        return true;
    }

    private PulseGuardNotification CreateNotification(ActivityLogEntry entry, PulseGuardNotificationKind kind)
    {
        var title = kind switch
        {
            PulseGuardNotificationKind.SuccessDigest => "PulseGuard • Task complete",
            PulseGuardNotificationKind.ActionRequired => "PulseGuard • Action required",
            _ => "PulseGuard • Heads-up"
        };

        var message = string.IsNullOrWhiteSpace(entry.Message)
            ? "Review the latest automation activity."
            : entry.Message;

        if (message.Length > 180)
        {
            message = message[..180] + "…";
        }

        return new PulseGuardNotification(kind, title, message, entry);
    }

    private void Enqueue(PulseGuardNotification notification)
    {
        lock (_pending)
        {
            _pending.Enqueue(notification);
            if (_processing)
            {
                return;
            }

            _processing = true;
        }

        _ = ProcessQueueAsync();
    }

    private async Task ProcessQueueAsync()
    {
        while (true)
        {
            PulseGuardNotification? next;
            lock (_pending)
            {
                if (_pending.Count == 0)
                {
                    _processing = false;
                    return;
                }

                next = _pending.Dequeue();
            }

            await _dispatcher.InvokeAsync(() => _trayService.ShowNotification(next));
            await Task.Delay(500);
        }
    }
}
