using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using TidyWindow.Core.Startup;

namespace TidyWindow.App.Services;

/// <summary>
/// Robust background service that ensures guarded startup items stay disabled.
/// Uses both periodic polling and registry watchers for comprehensive coverage.
/// </summary>
public sealed class StartupGuardBackgroundService : IDisposable
{
    private static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultScanInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RapidScanInterval = TimeSpan.FromSeconds(30);

    private readonly StartupInventoryService _inventory;
    private readonly StartupControlService _control;
    private readonly StartupGuardService _guard;
    private readonly ActivityLogService _activityLog;
    private readonly UserPreferencesService _preferences;
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _scanInterval;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private int _scanInFlight;
    private bool _disposed;
    private int _consecutiveViolations;
    private DateTime _lastScanTime = DateTime.MinValue;

    // Registry watchers for real-time detection
    private readonly List<RegistryMonitor> _registryMonitors = new();

    // FileSystem watchers for StartupFolder instant detection
    private readonly List<FileSystemWatcher> _folderWatchers = new();

    public StartupGuardBackgroundService(
        StartupInventoryService inventory,
        StartupControlService control,
        StartupGuardService guard,
        ActivityLogService activityLog,
        UserPreferencesService preferences)
        : this(inventory, control, guard, activityLog, preferences, null, null)
    {
    }

    internal StartupGuardBackgroundService(
        StartupInventoryService inventory,
        StartupControlService control,
        StartupGuardService guard,
        ActivityLogService activityLog,
        UserPreferencesService preferences,
        TimeSpan? initialDelayOverride,
        TimeSpan? scanIntervalOverride)
    {
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _activityLog = activityLog ?? throw new ArgumentNullException(nameof(activityLog));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _initialDelay = NormalizeDelay(initialDelayOverride ?? DefaultInitialDelay);
        _scanInterval = NormalizeInterval(scanIntervalOverride ?? DefaultScanInterval);

        WeakEventManager<UserPreferencesService, UserPreferencesChangedEventArgs>.AddHandler(_preferences, nameof(UserPreferencesService.PreferencesChanged), OnPreferencesChanged);
        EvaluateLoopState(_preferences.Current);
    }

    /// <summary>
    /// Gets the number of consecutive violations detected. Higher numbers indicate
    /// persistent applications that keep re-enabling themselves.
    /// </summary>
    public int ConsecutiveViolations => _consecutiveViolations;

    /// <summary>
    /// Force an immediate scan for violations, regardless of the regular schedule.
    /// </summary>
    public async Task ForceScanAsync(CancellationToken cancellationToken = default)
    {
        await RunOnceAsync(cancellationToken, terminateProcesses: true).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        WeakEventManager<UserPreferencesService, UserPreferencesChangedEventArgs>.RemoveHandler(_preferences, nameof(UserPreferencesService.PreferencesChanged), OnPreferencesChanged);
        StopFolderWatchers();
        StopRegistryMonitors();
        StopLoop();
    }

    private void OnPreferencesChanged(object? sender, UserPreferencesChangedEventArgs args)
    {
        EvaluateLoopState(args.Preferences);
    }

    private void EvaluateLoopState(UserPreferences preferences)
    {
        if (_disposed)
        {
            return;
        }

        if (ShouldRun(preferences))
        {
            StartLoop();
            StartRegistryMonitors();
            StartFolderWatchers();
        }
        else
        {
            StopFolderWatchers();
            StopRegistryMonitors();
            StopLoop();
        }
    }

    private static bool ShouldRun(UserPreferences preferences)
    {
        // Honor global background toggle so we remain lightweight/off when the user prefers no background work.
        return preferences.RunInBackground && preferences.StartupGuardEnabled;
    }

    private void StartLoop()
    {
        lock (_gate)
        {
            if (_loopTask is not null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
        }
    }

    private void StopLoop()
    {
        CancellationTokenSource? cts;
        Task? loopTask;

        lock (_gate)
        {
            cts = _cts;
            loopTask = _loopTask;
            _cts = null;
            _loopTask = null;
        }

        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
            loopTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Suppress cancellation exceptions during shutdown.
        }
        finally
        {
            cts.Dispose();
        }
    }

    private void StartRegistryMonitors()
    {
        lock (_gate)
        {
            if (_registryMonitors.Count > 0)
            {
                return; // Already running
            }

            // Monitor key startup locations for changes
            var monitorPaths = new[]
            {
                (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run"),
                (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
                (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"),
                (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run"),
                (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
                (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run")
            };

            foreach (var (root, path) in monitorPaths)
            {
                try
                {
                    var monitor = new RegistryMonitor(root, path);
                    monitor.RegistryChanged += OnRegistryChanged;
                    monitor.Start();
                    _registryMonitors.Add(monitor);
                }
                catch
                {
                    // Non-fatal: fall back to polling only
                }
            }
        }
    }

    private void StopRegistryMonitors()
    {
        lock (_gate)
        {
            foreach (var monitor in _registryMonitors)
            {
                try
                {
                    monitor.RegistryChanged -= OnRegistryChanged;
                    monitor.Dispose();
                }
                catch
                {
                    // Suppress disposal exceptions
                }
            }

            _registryMonitors.Clear();
        }
    }

    private void StartFolderWatchers()
    {
        lock (_gate)
        {
            if (_folderWatchers.Count > 0)
            {
                return; // Already running
            }

            // Monitor StartupFolder locations for .lnk file changes
            var startupFolders = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Startup), // User startup
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup) // All Users startup
            };

            foreach (var folder in startupFolders)
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                {
                    continue;
                }

                try
                {
                    var watcher = new FileSystemWatcher(folder)
                    {
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                        Filter = "*.lnk",
                        EnableRaisingEvents = true,
                        IncludeSubdirectories = false
                    };

                    watcher.Created += OnStartupFolderChanged;
                    watcher.Changed += OnStartupFolderChanged;
                    watcher.Renamed += OnStartupFolderRenamed;

                    _folderWatchers.Add(watcher);
                }
                catch
                {
                    // Non-fatal: fall back to polling only
                }
            }
        }
    }

    private void StopFolderWatchers()
    {
        lock (_gate)
        {
            foreach (var watcher in _folderWatchers)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Created -= OnStartupFolderChanged;
                    watcher.Changed -= OnStartupFolderChanged;
                    watcher.Renamed -= OnStartupFolderRenamed;
                    watcher.Dispose();
                }
                catch
                {
                    // Suppress disposal exceptions
                }
            }

            _folderWatchers.Clear();
        }
    }

    private void OnStartupFolderChanged(object sender, FileSystemEventArgs e)
    {
        // Startup folder changed - trigger immediate scan
        TriggerImmediateScan();
    }

    private void OnStartupFolderRenamed(object sender, RenamedEventArgs e)
    {
        // Startup folder item renamed - trigger immediate scan
        TriggerImmediateScan();
    }

    private void TriggerImmediateScan()
    {
        if (_cts is null || _cts.Token.IsCancellationRequested)
        {
            return;
        }

        // Debounce: don't trigger if we just scanned
        if (DateTime.UtcNow - _lastScanTime < TimeSpan.FromSeconds(5))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RunOnceAsync(_cts?.Token ?? CancellationToken.None, terminateProcesses: true).ConfigureAwait(false);
            }
            catch
            {
                // Suppress
            }
        });
    }

    private void OnRegistryChanged(object? sender, EventArgs e)
    {
        // Registry changed - trigger immediate scan if not already in flight
        TriggerImmediateScan();
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_initialDelay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var violationsFound = await RunOnceAsync(cancellationToken, terminateProcesses: true).ConfigureAwait(false);

            // If violations keep occurring, use faster scan interval
            var interval = _consecutiveViolations > 2 ? RapidScanInterval : _scanInterval;

            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<bool> RunOnceAsync(CancellationToken cancellationToken, bool terminateProcesses = false)
    {
        if (Interlocked.Exchange(ref _scanInFlight, 1) == 1)
        {
            return false;
        }

        var foundViolations = false;

        try
        {
            _lastScanTime = DateTime.UtcNow;

            var guardIds = _guard.GetAll();
            if (guardIds.Count == 0)
            {
                _consecutiveViolations = 0;
                return false;
            }

            var guardSet = new HashSet<string>(guardIds, StringComparer.OrdinalIgnoreCase);
            var snapshot = await _inventory.GetInventoryAsync(null, cancellationToken).ConfigureAwait(false);
            var candidates = snapshot.Items
                .Where(item => guardSet.Contains(item.Id) && item.IsEnabled)
                .ToList();

            if (candidates.Count == 0)
            {
                _consecutiveViolations = 0;
                return false;
            }

            foundViolations = true;
            _consecutiveViolations++;

            foreach (var item in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // Use the new overload that can terminate running processes
                    var result = await _control.DisableAsync(item, terminateProcesses, cancellationToken).ConfigureAwait(false);

                    if (result.Succeeded)
                    {
                        var severity = _consecutiveViolations > 3 ? "critical" : "warning";
                        _activityLog.LogWarning(
                            "StartupGuard",
                            $"Auto-disabled guarded startup entry ({severity}): {result.Item.Name} (violation #{_consecutiveViolations})",
                            new object?[]
                            {
                                result.Item.Id,
                                result.Item.SourceKind.ToString(),
                                result.Item.EntryLocation ?? string.Empty,
                                _consecutiveViolations,
                                terminateProcesses ? "processes terminated" : "processes not terminated"
                            });
                    }
                    else
                    {
                        _activityLog.LogWarning(
                            "StartupGuard",
                            $"Failed to auto-disable guarded entry {item.Name}: {result.ErrorMessage ?? "Unknown error"}",
                            new object?[] { item.Id, item.SourceKind.ToString(), item.EntryLocation ?? string.Empty });
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _activityLog.LogError(
                        "StartupGuard",
                        $"Error auto-disabling guarded entry {item.Name}: {ex.Message}",
                        new object?[] { item.Id, item.SourceKind.ToString(), item.EntryLocation ?? string.Empty, ex });
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _scanInFlight, 0);
        }

        return foundViolations;
    }

    private static TimeSpan NormalizeDelay(TimeSpan delay)
    {
        return delay <= TimeSpan.Zero ? TimeSpan.FromSeconds(10) : delay;
    }

    private static TimeSpan NormalizeInterval(TimeSpan interval)
    {
        return interval <= TimeSpan.Zero ? TimeSpan.FromMinutes(2) : interval;
    }
}

/// <summary>
/// Simple registry change monitor using RegNotifyChangeKeyValue.
/// </summary>
internal sealed class RegistryMonitor : IDisposable
{
    private readonly RegistryKey _rootKey;
    private readonly string _subKeyPath;
    private readonly ManualResetEvent _stopEvent = new(false);
    private readonly AutoResetEvent _changeEvent = new(false);
    private Thread? _monitorThread;
    private RegistryKey? _openedKey;
    private bool _disposed;

    public event EventHandler? RegistryChanged;

    public RegistryMonitor(RegistryKey rootKey, string subKeyPath)
    {
        _rootKey = rootKey ?? throw new ArgumentNullException(nameof(rootKey));
        _subKeyPath = subKeyPath ?? throw new ArgumentNullException(nameof(subKeyPath));
    }

    public void Start()
    {
        if (_monitorThread is not null)
        {
            return;
        }

        _openedKey = _rootKey.OpenSubKey(_subKeyPath, writable: false);
        if (_openedKey is null)
        {
            return; // Key doesn't exist, nothing to monitor
        }

        _monitorThread = new Thread(MonitorThread)
        {
            IsBackground = true,
            Name = $"RegistryMonitor_{_subKeyPath}"
        };
        _monitorThread.Start();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopEvent.Set();
        _monitorThread?.Join(TimeSpan.FromSeconds(2));
        _openedKey?.Dispose();
        _stopEvent.Dispose();
        _changeEvent.Dispose();
    }

    private void MonitorThread()
    {
        var handles = new WaitHandle[] { _stopEvent, _changeEvent };

        while (!_disposed)
        {
            try
            {
                if (_openedKey is null)
                {
                    return;
                }

                // Use P/Invoke to call RegNotifyChangeKeyValue
                var result = NativeMethods.RegNotifyChangeKeyValue(
                    _openedKey.Handle,
                    watchSubtree: true,
                    notifyFilter: NativeMethods.REG_NOTIFY_CHANGE_LAST_SET | NativeMethods.REG_NOTIFY_CHANGE_NAME,
                    eventHandle: _changeEvent.SafeWaitHandle,
                    asynchronous: true);

                if (result != 0)
                {
                    return; // Failed to register, exit thread
                }

                var waitResult = WaitHandle.WaitAny(handles, TimeSpan.FromSeconds(30));

                if (waitResult == 0)
                {
                    return; // Stop event signaled
                }

                if (waitResult == 1)
                {
                    // Registry changed
                    RegistryChanged?.Invoke(this, EventArgs.Empty);
                }

                // Timeout or spurious wake - just loop again
            }
            catch
            {
                return; // Exit on any error
            }
        }
    }

    private static class NativeMethods
    {
        public const int REG_NOTIFY_CHANGE_NAME = 0x00000001;
        public const int REG_NOTIFY_CHANGE_LAST_SET = 0x00000004;

        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
        public static extern int RegNotifyChangeKeyValue(
            Microsoft.Win32.SafeHandles.SafeRegistryHandle hKey,
            bool watchSubtree,
            int notifyFilter,
            Microsoft.Win32.SafeHandles.SafeWaitHandle eventHandle,
            bool asynchronous);
    }
}
