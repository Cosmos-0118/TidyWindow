using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TidyWindow.Core.Cleanup;

namespace TidyWindow.App.Services;

/// <summary>
/// Runs unattended cleanup jobs based on automation preferences.
/// </summary>
public sealed class CleanupAutomationScheduler : IDisposable
{

    private readonly CleanupAutomationSettingsStore _store;
    private readonly CleanupService _cleanupService;
    private readonly ActivityLogService _activityLog;
    private readonly IBrowserCleanupService? _browserCleanupService;
    private readonly IAutomationWorkTracker _workTracker;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private System.Threading.Timer? _timer;
    private CleanupAutomationSettings _settings;
    private bool _disposed;

    public CleanupAutomationScheduler(
        CleanupAutomationSettingsStore store,
        CleanupService cleanupService,
        ActivityLogService activityLog,
        IBrowserCleanupService? browserCleanupService,
        IAutomationWorkTracker workTracker)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _cleanupService = cleanupService ?? throw new ArgumentNullException(nameof(cleanupService));
        _activityLog = activityLog ?? throw new ArgumentNullException(nameof(activityLog));
        _browserCleanupService = browserCleanupService;
        _workTracker = workTracker ?? throw new ArgumentNullException(nameof(workTracker));

        _settings = _store.Get().Normalize();
        ConfigureTimer();
    }

    public CleanupAutomationSettings CurrentSettings => _settings;

    public event EventHandler<CleanupAutomationSettings>? SettingsChanged;

    public async Task<CleanupAutomationRunResult?> ApplySettingsAsync(
        CleanupAutomationSettings settings,
        bool runImmediately,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var normalized = settings.Normalize();
        _settings = normalized;
        _store.Save(normalized);
        ConfigureTimer();
        OnSettingsChanged();

        if (runImmediately && normalized.AutomationEnabled)
        {
            return await RunOnceInternalAsync(isBackground: false, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    public Task<CleanupAutomationRunResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunOnceInternalAsync(isBackground: false, cancellationToken);
    }

    private async Task<CleanupAutomationRunResult> RunOnceInternalAsync(bool isBackground, CancellationToken cancellationToken)
    {
        if (!_settings.AutomationEnabled)
        {
            return CleanupAutomationRunResult.Skipped(DateTimeOffset.UtcNow, "Automation disabled.");
        }

        var timeout = isBackground ? 0 : Timeout.Infinite;
        if (!await _runLock.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
        {
            return CleanupAutomationRunResult.Skipped(DateTimeOffset.UtcNow, "Automation already running.");
        }

        Guid workToken = Guid.Empty;
        try
        {
            workToken = _workTracker.BeginWork(AutomationWorkType.Cleanup, "Scheduled cleanup run");
            var result = await ExecuteRunAsync(_settings, cancellationToken).ConfigureAwait(false);
            LogRunResult(result);
            return result;
        }
        catch (Exception ex)
        {
            var failure = CleanupAutomationRunResult.Skipped(DateTimeOffset.UtcNow, ex.Message);
            _activityLog.LogError("Cleanup automation", "Automation failed", new[] { ex.ToString() });
            return failure;
        }
        finally
        {
            if (workToken != Guid.Empty)
            {
                _workTracker.CompleteWork(workToken);
            }

            _runLock.Release();
        }
    }

    private async Task<CleanupAutomationRunResult> ExecuteRunAsync(CleanupAutomationSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var topItemCount = Math.Clamp(
            settings.TopItemCount,
            CleanupAutomationSettings.MinimumTopItemCount,
            CleanupAutomationSettings.MaximumTopItemCount);

        var report = await _cleanupService.PreviewAsync(
            settings.IncludeDownloads,
            settings.IncludeBrowserHistory,
            topItemCount,
            CleanupItemKind.Both,
            cancellationToken).ConfigureAwait(false);

        var flattened = FlattenTargets(report)
            .OrderByDescending(static tuple => tuple.item.SizeBytes)
            .Take(topItemCount)
            .ToList();

        var requestedBytes = flattened.Sum(static tuple => Math.Max(tuple.item.SizeBytes, 0));
        var requestedItems = flattened.Count;

        if (requestedItems == 0)
        {
            var timestamp = DateTimeOffset.UtcNow;
            UpdateLastRun(timestamp);
            return new CleanupAutomationRunResult(
                timestamp,
                false,
                "No eligible items found.",
                0,
                0,
                new CleanupDeletionResult(Array.Empty<CleanupDeletionEntry>()),
                Array.Empty<string>());
        }

        var historyBatch = await HandleBrowserHistoryAsync(settings.IncludeBrowserHistory, flattened, cancellationToken).ConfigureAwait(false);
        var handledPaths = new HashSet<string>(historyBatch.Entries.Select(static entry => entry.Path), StringComparer.OrdinalIgnoreCase);

        var deleteSet = new List<CleanupPreviewItem>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tuple in flattened)
        {
            if (string.IsNullOrWhiteSpace(tuple.item.FullName))
            {
                continue;
            }

            if (handledPaths.Contains(tuple.item.FullName))
            {
                continue;
            }

            if (seenPaths.Add(tuple.item.FullName))
            {
                deleteSet.Add(tuple.item);
            }
        }

        var deletionOptions = BuildDeletionOptions(settings);
        CleanupDeletionResult deletionResult;
        if (deleteSet.Count == 0)
        {
            deletionResult = historyBatch.Entries.Count == 0
                ? new CleanupDeletionResult(Array.Empty<CleanupDeletionEntry>())
                : new CleanupDeletionResult(historyBatch.Entries);
        }
        else
        {
            var result = await _cleanupService.DeleteAsync(deleteSet, progress: null, deletionOptions, cancellationToken)
                .ConfigureAwait(false);
            deletionResult = historyBatch.Entries.Count == 0
                ? result
                : new CleanupDeletionResult(historyBatch.Entries.Concat(result.Entries));
        }

        var timestampCompleted = DateTimeOffset.UtcNow;
        UpdateLastRun(timestampCompleted);

        var warnings = historyBatch.Warnings.Count == 0
            ? Array.Empty<string>()
            : historyBatch.Warnings.ToArray();

        return new CleanupAutomationRunResult(
            timestampCompleted,
            false,
            deletionResult.ToStatusMessage(),
            requestedItems,
            requestedBytes,
            deletionResult,
            warnings);
    }

    private static List<(CleanupTargetReport target, CleanupPreviewItem item)> FlattenTargets(CleanupReport report)
    {
        var list = new List<(CleanupTargetReport, CleanupPreviewItem)>();
        if (report?.Targets is null)
        {
            return list;
        }

        foreach (var target in report.Targets)
        {
            if (target?.Preview is null)
            {
                continue;
            }

            foreach (var item in target.Preview)
            {
                if (item is null)
                {
                    continue;
                }

                list.Add((target, item));
            }
        }

        return list;
    }

    private async Task<BrowserCleanupBatch> HandleBrowserHistoryAsync(
        bool includeBrowserHistory,
        IReadOnlyList<(CleanupTargetReport target, CleanupPreviewItem item)> items,
        CancellationToken cancellationToken)
    {
        if (!includeBrowserHistory || items is null || items.Count == 0)
        {
            return BrowserCleanupBatch.Empty;
        }

        var grouped = new Dictionary<string, (BrowserProfile Profile, List<CleanupPreviewItem> Items)>(StringComparer.OrdinalIgnoreCase);

        foreach (var tuple in items)
        {
            if (!string.Equals(tuple.target.Classification, "History", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!BrowserHistoryHelper.TryGetBrowserProfile(tuple.item.FullName, out var profile))
            {
                continue;
            }

            if (!grouped.TryGetValue(profile.ProfileDirectory, out var bucket))
            {
                bucket = (profile, new List<CleanupPreviewItem>());
                grouped[profile.ProfileDirectory] = bucket;
            }

            bucket.Items.Add(tuple.item);
            grouped[profile.ProfileDirectory] = bucket;
        }

        if (grouped.Count == 0)
        {
            return BrowserCleanupBatch.Empty;
        }

        if (_browserCleanupService is null)
        {
            return new BrowserCleanupBatch(
                Array.Empty<CleanupDeletionEntry>(),
                new[] { "Browser cleanup service unavailable." });
        }

        var entries = new List<CleanupDeletionEntry>();
        var warnings = new List<string>();

        foreach (var kvp in grouped)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var profile = kvp.Value.Profile;
            var targets = kvp.Value.Items.Select(static item => item.FullName).ToList();
            var result = await _browserCleanupService.ClearHistoryAsync(profile, targets, cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                var dispositionMessage = profile.Kind switch
                {
                    BrowserKind.Edge => "Cleared via Microsoft Edge API.",
                    BrowserKind.Chrome => "Cleared via Chrome history purge.",
                    _ => "Cleared browser history."
                };

                foreach (var item in kvp.Value.Items)
                {
                    entries.Add(new CleanupDeletionEntry(
                        item.FullName,
                        Math.Max(item.SizeBytes, 0),
                        item.IsDirectory,
                        CleanupDeletionDisposition.Deleted,
                        dispositionMessage));
                }
            }
            else
            {
                warnings.Add(result.Message);
            }
        }

        return entries.Count == 0 && warnings.Count == 0
            ? BrowserCleanupBatch.Empty
            : new BrowserCleanupBatch(entries, warnings);
    }

    private static CleanupDeletionOptions BuildDeletionOptions(CleanupAutomationSettings settings)
    {
        return settings.DeletionMode switch
        {
            CleanupAutomationDeletionMode.MoveToRecycleBin => new CleanupDeletionOptions
            {
                PreferRecycleBin = true,
                AllowPermanentDeleteFallback = false,
                SkipLockedItems = true,
                TakeOwnershipOnAccessDenied = false,
                AllowDeleteOnReboot = false
            },
            CleanupAutomationDeletionMode.ForceDelete => new CleanupDeletionOptions
            {
                PreferRecycleBin = false,
                SkipLockedItems = false,
                TakeOwnershipOnAccessDenied = true,
                AllowDeleteOnReboot = true
            },
            _ => new CleanupDeletionOptions
            {
                PreferRecycleBin = false,
                SkipLockedItems = true,
                TakeOwnershipOnAccessDenied = false,
                AllowDeleteOnReboot = false
            }
        };
    }

    private void ConfigureTimer()
    {
        _timer?.Dispose();
        _timer = null;

        if (!_settings.AutomationEnabled)
        {
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Clamp(_settings.IntervalMinutes, CleanupAutomationSettings.MinimumIntervalMinutes, CleanupAutomationSettings.MaximumIntervalMinutes));
        var dueTime = interval;

        if (_settings.LastRunUtc is { } lastRun)
        {
            var elapsed = DateTimeOffset.UtcNow - lastRun;
            dueTime = elapsed >= interval ? TimeSpan.Zero : interval - elapsed;
        }

        _timer = new System.Threading.Timer(OnTimerTick, null, dueTime, interval);
    }

    private void OnTimerTick(object? state)
    {
        _ = RunOnceInternalAsync(isBackground: true, CancellationToken.None);
    }

    private void UpdateLastRun(DateTimeOffset timestamp)
    {
        _settings = _settings.WithLastRun(timestamp);
        _store.Save(_settings);
        OnSettingsChanged();
    }

    private void LogRunResult(CleanupAutomationRunResult result)
    {
        if (result.WasSkipped)
        {
            _activityLog.LogInformation("Cleanup automation", result.Message);
            return;
        }

        var reclaimed = result.DeletionResult.TotalBytesDeleted / 1_048_576d;
        var summary = result.DeletionResult.DeletedCount == 0
            ? "Cleanup automation ran — nothing to delete."
            : $"Cleanup automation deleted {result.DeletionResult.DeletedCount:N0} item(s) ({reclaimed:F2} MB).";

        var details = new List<string>
        {
            $"Requested items: {result.RequestedItemCount:N0}",
            $"Requested size: {result.RequestedBytes / 1_048_576d:F2} MB",
            $"Deleted: {result.DeletionResult.DeletedCount:N0}",
            $"Skipped: {result.DeletionResult.SkippedCount:N0}",
            $"Failed: {result.DeletionResult.FailedCount:N0}"
        };

        if (result.Warnings.Count > 0)
        {
            details.Add("Warnings:");
            details.AddRange(result.Warnings.Select(static warning => "  ↳ " + warning));
        }

        if (result.DeletionResult.Errors.Count > 0)
        {
            details.Add("Errors:");
            details.AddRange(result.DeletionResult.Errors.Take(5));
        }

        if (result.DeletionResult.FailedCount > 0)
        {
            _activityLog.LogWarning("Cleanup automation", summary, details);
            return;
        }

        if (result.DeletionResult.DeletedCount > 0)
        {
            _activityLog.LogSuccess("Cleanup automation", summary, details);
        }
        else
        {
            _activityLog.LogInformation("Cleanup automation", summary, details);
        }
    }

    private void OnSettingsChanged()
    {
        SettingsChanged?.Invoke(this, _settings);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(CleanupAutomationScheduler));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer?.Dispose();
        _runLock.Dispose();
    }

    private sealed record BrowserCleanupBatch(
        IReadOnlyList<CleanupDeletionEntry> Entries,
        IReadOnlyList<string> Warnings)
    {
        public static BrowserCleanupBatch Empty { get; } = new(
            Array.Empty<CleanupDeletionEntry>(),
            Array.Empty<string>());
    }
}

public sealed record CleanupAutomationRunResult(
    DateTimeOffset ExecutedAtUtc,
    bool WasSkipped,
    string Message,
    int RequestedItemCount,
    long RequestedBytes,
    CleanupDeletionResult DeletionResult,
    IReadOnlyList<string> Warnings)
{
    public static CleanupAutomationRunResult Skipped(DateTimeOffset timestamp, string message)
        => new(timestamp, true, message, 0, 0, new CleanupDeletionResult(Array.Empty<CleanupDeletionEntry>()), Array.Empty<string>());
}