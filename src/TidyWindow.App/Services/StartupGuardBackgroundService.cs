using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using TidyWindow.Core.Startup;

namespace TidyWindow.App.Services;

/// <summary>
/// Lightweight background loop that re-disables guarded startup items when they are re-enabled externally.
/// </summary>
public sealed class StartupGuardBackgroundService : IDisposable
{
    private static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DefaultScanInterval = TimeSpan.FromMinutes(5);

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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        WeakEventManager<UserPreferencesService, UserPreferencesChangedEventArgs>.RemoveHandler(_preferences, nameof(UserPreferencesService.PreferencesChanged), OnPreferencesChanged);
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
        }
        else
        {
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
            await RunOnceAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await Task.Delay(_scanInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _scanInFlight, 1) == 1)
        {
            return;
        }

        try
        {
            var guardIds = _guard.GetAll();
            if (guardIds.Count == 0)
            {
                return;
            }

            var guardSet = new HashSet<string>(guardIds, StringComparer.OrdinalIgnoreCase);
            var snapshot = await _inventory.GetInventoryAsync(null, cancellationToken).ConfigureAwait(false);
            var candidates = snapshot.Items
                .Where(item => guardSet.Contains(item.Id) && item.IsEnabled)
                .ToList();

            if (candidates.Count == 0)
            {
                return;
            }

            foreach (var item in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var result = await _control.DisableAsync(item, cancellationToken).ConfigureAwait(false);
                    if (result.Succeeded)
                    {
                        _activityLog.LogWarning(
                            "StartupGuard",
                            $"Auto-disabled guarded startup entry: {result.Item.Name}",
                            new object?[] { result.Item.Id, result.Item.SourceKind.ToString(), result.Item.EntryLocation ?? string.Empty });
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
