using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using TidyWindow.Core.Processes;
using TidyWindow.Core.Processes.ThreatWatch;

namespace TidyWindow.App.Services;

/// <summary>
/// Runs Threat Watch scans on a gentle cadence so PulseGuard can surface alerts even when the UI is hidden.
/// </summary>
public sealed class ThreatWatchBackgroundScanner : IDisposable
{
    private static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultScanInterval = TimeSpan.FromMinutes(45);

    private readonly ThreatWatchScanService? _scanService;
    private readonly Func<CancellationToken, Task<ThreatWatchDetectionResult>> _scanInvoker;
    private readonly ActivityLogService _activityLog;
    private readonly UserPreferencesService _preferences;
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _scanInterval;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private int _scanInFlight;
    private bool _disposed;

    public ThreatWatchBackgroundScanner(ThreatWatchScanService scanService, ActivityLogService activityLog, UserPreferencesService preferences)
        : this(scanService, activityLog, preferences, null, null, null)
    {
    }

    internal ThreatWatchBackgroundScanner(
        ThreatWatchScanService? scanService,
        ActivityLogService activityLog,
        UserPreferencesService preferences,
        TimeSpan? initialDelayOverride,
        TimeSpan? scanIntervalOverride,
        Func<CancellationToken, Task<ThreatWatchDetectionResult>>? scanInvokerOverride)
    {
        if (scanService is null && scanInvokerOverride is null)
        {
            throw new ArgumentNullException(nameof(scanService));
        }

        _scanService = scanService;
        _activityLog = activityLog ?? throw new ArgumentNullException(nameof(activityLog));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _scanInvoker = scanInvokerOverride ?? (token => (_scanService ?? throw new InvalidOperationException()).RunScanAsync(token));
        _initialDelay = NormalizeInitialDelay(initialDelayOverride ?? DefaultInitialDelay);
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
        return preferences.RunInBackground && preferences.PulseGuardEnabled;
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
            // Swallow cancellation exceptions; shutdown is already in progress.
        }
        finally
        {
            cts.Dispose();
        }
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        try
        {
            if (_initialDelay > TimeSpan.Zero)
            {
                await Task.Delay(_initialDelay, token).ConfigureAwait(false);
            }

            while (!token.IsCancellationRequested)
            {
                await ExecuteScanAsync(token).ConfigureAwait(false);
                await Task.Delay(_scanInterval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when shutting down.
        }
    }

    private async Task ExecuteScanAsync(CancellationToken token)
    {
        if (!ShouldRun(_preferences.Current))
        {
            return;
        }

        if (Interlocked.Exchange(ref _scanInFlight, 1) == 1)
        {
            return;
        }

        try
        {
            var result = await _scanInvoker(token).ConfigureAwait(false);
            PublishResult(result);
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellations that bubble up during shutdown or preference changes.
        }
        catch (Exception ex)
        {
            _activityLog.LogError("Threat Watch", $"Background scan failed: {ex.Message}", new[] { ex.ToString() });
        }
        finally
        {
            Interlocked.Exchange(ref _scanInFlight, 0);
        }
    }

    private void PublishResult(ThreatWatchDetectionResult result)
    {
        if (result is null)
        {
            return;
        }

        if (result.Hits.Count == 0)
        {
            _activityLog.LogSuccess("Threat Watch", "Background scan is clear.");
            return;
        }

        var hasCritical = result.Hits.Any(static hit => hit.Level == SuspicionLevel.Red);
        var summary = result.Hits.Count == 1
            ? "Threat Watch flagged 1 suspicious process while running quietly in the tray."
            : $"Threat Watch flagged {result.Hits.Count} suspicious processes while running quietly in the tray.";
        var details = BuildHitDetails(result.Hits, 6);

        if (hasCritical)
        {
            _activityLog.LogError("Threat Watch", summary, details);
        }
        else
        {
            _activityLog.LogWarning("Threat Watch", summary, details);
        }
    }

    private static IEnumerable<string> BuildHitDetails(IEnumerable<SuspiciousProcessHit> hits, int limit)
    {
        return hits
            .OrderByDescending(static hit => hit.Level)
            .ThenBy(static hit => hit.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(static hit => $"{hit.Level}: {hit.ProcessName} — {hit.FilePath}");
    }

    private static TimeSpan NormalizeInitialDelay(TimeSpan value)
    {
        return value <= TimeSpan.Zero ? TimeSpan.Zero : value;
    }

    private static TimeSpan NormalizeInterval(TimeSpan value)
    {
        // Keep at least one minute between background scans if the caller passes zero/negative values.
        return value <= TimeSpan.Zero ? TimeSpan.FromMinutes(1) : value;
    }
}
