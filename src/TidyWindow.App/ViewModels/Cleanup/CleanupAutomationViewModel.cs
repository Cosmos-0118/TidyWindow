using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.Services;
using TidyWindow.Core.Cleanup;

namespace TidyWindow.App.ViewModels;

/// <summary>
/// Cleanup automation panel commands and helpers.
/// </summary>
public sealed partial class CleanupViewModel
{
    private void OnCleanupAutomationSettingsChanged(object? sender, CleanupAutomationSettings settings)
    {
        if (settings is null)
        {
            return;
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyAutomationSettingsSnapshot(settings);
        }
        else
        {
            dispatcher.BeginInvoke(new Action(() => ApplyAutomationSettingsSnapshot(settings)));
        }
    }

    private void ApplyAutomationSettingsSnapshot(CleanupAutomationSettings settings)
    {
        if (settings is null)
        {
            return;
        }

        _suspendAutomationStateUpdates = true;

        IsCleanupAutomationEnabled = settings.AutomationEnabled;

        SelectedAutomationInterval = AutomationIntervalOptions.FirstOrDefault(option => option.Minutes == settings.IntervalMinutes)
            ?? AutomationIntervalOptions.FirstOrDefault();

        SelectedAutomationDeletionMode = AutomationDeletionModeOptions.FirstOrDefault(option => option.Mode == settings.DeletionMode)
            ?? AutomationDeletionModeOptions.FirstOrDefault();

        AutomationIncludeDownloads = settings.IncludeDownloads;
        AutomationIncludeBrowserHistory = settings.IncludeBrowserHistory;
        AutomationTopItemCount = settings.TopItemCount;
        AutomationLastRunUtc = settings.LastRunUtc;
        HasAutomationChanges = false;

        _suspendAutomationStateUpdates = false;
        UpdateAutomationStatus();
    }

    private CleanupAutomationSettings BuildAutomationSettingsSnapshot()
    {
        var interval = SelectedAutomationInterval?.Minutes ?? _cleanupAutomationScheduler.CurrentSettings.IntervalMinutes;
        var mode = SelectedAutomationDeletionMode?.Mode ?? CleanupAutomationDeletionMode.SkipLocked;
        var lastRun = _cleanupAutomationScheduler.CurrentSettings.LastRunUtc;
        var topItems = AutomationTopItemCount > 0
            ? AutomationTopItemCount
            : _cleanupAutomationScheduler.CurrentSettings.TopItemCount;

        return new CleanupAutomationSettings(
            IsCleanupAutomationEnabled,
            interval,
            mode,
            AutomationIncludeDownloads,
            AutomationIncludeBrowserHistory,
            topItems,
            lastRun);
    }

    private void UpdateAutomationStatus()
    {
        if (!IsCleanupAutomationEnabled)
        {
            AutomationStatusMessage = "Automation is disabled.";
            return;
        }

        var intervalLabel = SelectedAutomationInterval?.Label
                            ?? FormatInterval(SelectedAutomationInterval?.Minutes ?? _cleanupAutomationScheduler.CurrentSettings.IntervalMinutes);

        var topItems = AutomationTopItemCount > 0
            ? AutomationTopItemCount
            : _cleanupAutomationScheduler.CurrentSettings.TopItemCount;

        var includeParts = new List<string>();
        if (AutomationIncludeDownloads)
        {
            includeParts.Add("downloads");
        }

        if (AutomationIncludeBrowserHistory)
        {
            includeParts.Add("history");
        }

        if (includeParts.Count == 0)
        {
            includeParts.Add("system caches");
        }

        var lastRunText = AutomationLastRunUtc is null
            ? "Hasn't run yet."
            : $"Last run {FormatRelativeTime(AutomationLastRunUtc.Value)}.";

        var sweepText = $"Sweeps top {topItems} items";

        AutomationStatusMessage = $"{intervalLabel} • {sweepText} • Targets {string.Join(" + ", includeParts)} • {lastRunText}";
    }

    private void OnRelativeTimeTick(object? sender, EventArgs e)
    {
        UpdateAutomationStatus();
    }

    private void MarkAutomationStateDirty()
    {
        if (_suspendAutomationStateUpdates)
        {
            return;
        }

        HasAutomationChanges = true;
        UpdateAutomationStatus();
    }

    private static string FormatInterval(int intervalMinutes)
    {
        if (intervalMinutes <= 0)
        {
            return "Custom interval";
        }

        var span = TimeSpan.FromMinutes(intervalMinutes);
        if (span.TotalDays >= 30)
        {
            return "Every month";
        }

        if (span.TotalDays >= 7)
        {
            return "Every week";
        }

        if (span.TotalDays >= 1)
        {
            return span.TotalDays == 1 ? "Every day" : $"Every {span.TotalDays:F0} days";
        }

        if (span.TotalHours >= 1)
        {
            return span.TotalHours == 1 ? "Every hour" : $"Every {span.TotalHours:F0} hours";
        }

        return span.TotalMinutes == 1 ? "Every minute" : $"Every {span.TotalMinutes:F0} minutes";
    }

    private static string FormatRelativeTime(DateTimeOffset timestamp)
    {
        var delta = DateTimeOffset.UtcNow - timestamp;
        if (delta < TimeSpan.Zero)
        {
            delta = TimeSpan.Zero;
        }

        if (delta < TimeSpan.FromMinutes(1))
        {
            return "moments ago";
        }

        if (delta < TimeSpan.FromHours(1))
        {
            return $"{Math.Max(1, (int)Math.Round(delta.TotalMinutes))} min ago";
        }

        if (delta < TimeSpan.FromDays(1))
        {
            return $"{Math.Max(1, (int)Math.Round(delta.TotalHours))} hr ago";
        }

        if (delta < TimeSpan.FromDays(30))
        {
            return $"{Math.Max(1, (int)Math.Round(delta.TotalDays))} day(s) ago";
        }

        return timestamp.ToLocalTime().ToString("g");
    }

    private IReadOnlyList<string> BuildAutomationSettingsDetails(CleanupAutomationSettings settings)
    {
        var details = new List<string>
        {
            $"Cadence: {ResolveAutomationIntervalLabel(settings.IntervalMinutes)}",
            $"Deletion mode: {ResolveAutomationDeletionModeLabel(settings.DeletionMode)}",
            $"Scope: {BuildScopeSummary(settings.IncludeDownloads, settings.IncludeBrowserHistory)}"
        };

        if (settings.LastRunUtc is DateTimeOffset lastRun)
        {
            details.Add($"Last run: {FormatRelativeTime(lastRun)}");
        }
        else
        {
            details.Add("Last run: not yet");
        }

        return details;
    }

    private IReadOnlyList<string> BuildAutomationRunDetails(CleanupAutomationRunResult result)
    {
        var details = new List<string>
        {
            $"Requested items: {result.RequestedItemCount:N0}",
            $"Requested size: {FormatSize(result.RequestedBytes / 1_048_576d)}",
            $"Deleted: {result.DeletionResult.DeletedCount:N0} ({FormatSize(result.DeletionResult.TotalBytesDeleted / 1_048_576d)})",
            $"Skipped: {result.DeletionResult.SkippedCount:N0} ({FormatSize(result.DeletionResult.TotalBytesSkipped / 1_048_576d)})",
            $"Failed: {result.DeletionResult.FailedCount:N0} ({FormatSize(result.DeletionResult.TotalBytesFailed / 1_048_576d)})"
        };

        if (result.Warnings.Count > 0)
        {
            details.Add("Warnings:");
            foreach (var warning in result.Warnings.Take(5))
            {
                details.Add(" • " + warning);
            }
        }

        if (result.DeletionResult.HasErrors)
        {
            details.Add("Errors:");
            foreach (var error in result.DeletionResult.Errors.Take(5))
            {
                details.Add(" • " + error);
            }
        }

        return details;
    }

    private string ResolveAutomationIntervalLabel(int minutes)
    {
        var label = AutomationIntervalOptions.FirstOrDefault(option => option.Minutes == minutes)?.Label;
        return string.IsNullOrWhiteSpace(label) ? FormatInterval(minutes) : label!;
    }

    private string ResolveAutomationDeletionModeLabel(CleanupAutomationDeletionMode mode)
    {
        return AutomationDeletionModeOptions.FirstOrDefault(option => option.Mode == mode)?.Label
            ?? mode.ToString();
    }

    private static string BuildScopeSummary(bool includeDownloads, bool includeBrowserHistory)
    {
        if (includeDownloads && includeBrowserHistory)
        {
            return "Downloads + history";
        }

        if (includeDownloads)
        {
            return "Downloads only";
        }

        if (includeBrowserHistory)
        {
            return "History only";
        }

        return "System caches only";
    }

    [RelayCommand]
    private void ToggleAutomationPanel()
    {
        IsAutomationPanelVisible = !IsAutomationPanelVisible;
    }

    [RelayCommand]
    private void CloseAutomationPanel()
    {
        if (IsAutomationPanelVisible)
        {
            IsAutomationPanelVisible = false;
        }
    }

    [RelayCommand]
    private async Task ApplyCleanupAutomationAsync()
    {
        if (IsAutomationBusy)
        {
            return;
        }

        try
        {
            IsAutomationBusy = true;
            _mainViewModel.SetStatusMessage("Saving cleanup automation...");
            var snapshot = BuildAutomationSettingsSnapshot();
            await _cleanupAutomationScheduler.ApplySettingsAsync(snapshot, runImmediately: false);
            HasAutomationChanges = false;

            var status = snapshot.AutomationEnabled
                ? $"Cleanup automation enabled ({FormatInterval(snapshot.IntervalMinutes)})."
                : "Cleanup automation disabled.";
            var level = snapshot.AutomationEnabled ? ActivityLogLevel.Success : ActivityLogLevel.Information;
            _mainViewModel.LogActivity(level, "Cleanup automation", status, BuildAutomationSettingsDetails(snapshot));
        }
        catch (Exception ex)
        {
            _mainViewModel.LogActivity(ActivityLogLevel.Error, "Cleanup automation", "Failed to save automation settings.", new[] { ex.Message });
        }
        finally
        {
            IsAutomationBusy = false;
            _mainViewModel.SetStatusMessage("Ready");
        }
    }

    [RelayCommand]
    private async Task RunCleanupAutomationNowAsync()
    {
        if (IsAutomationBusy || !IsCleanupAutomationEnabled)
        {
            return;
        }

        try
        {
            IsAutomationBusy = true;
            _mainViewModel.SetStatusMessage("Running cleanup automation...");
            var result = await _cleanupAutomationScheduler.RunOnceAsync();
            var logLevel = result.WasSkipped ? ActivityLogLevel.Warning : ActivityLogLevel.Success;
            var message = result.WasSkipped
                ? $"Automation run skipped — {result.Message}"
                : $"Automation run complete — {result.Message}";
            _mainViewModel.LogActivity(logLevel, "Cleanup automation", message, BuildAutomationRunDetails(result));
        }
        catch (Exception ex)
        {
            _mainViewModel.LogActivity(ActivityLogLevel.Error, "Cleanup automation", "Failed to run automation.", new[] { ex.Message });
        }
        finally
        {
            IsAutomationBusy = false;
            _mainViewModel.SetStatusMessage("Ready");
        }
    }

    partial void OnIsCleanupAutomationEnabledChanged(bool value)
    {
        MarkAutomationStateDirty();
    }

    partial void OnSelectedAutomationIntervalChanged(CleanupAutomationIntervalOption? value)
    {
        MarkAutomationStateDirty();
    }

    partial void OnSelectedAutomationDeletionModeChanged(CleanupAutomationDeletionModeOption? value)
    {
        MarkAutomationStateDirty();
    }

    partial void OnAutomationIncludeDownloadsChanged(bool value)
    {
        MarkAutomationStateDirty();
    }

    partial void OnAutomationIncludeBrowserHistoryChanged(bool value)
    {
        MarkAutomationStateDirty();
    }

    partial void OnAutomationTopItemCountChanged(int value)
    {
        OnPropertyChanged(nameof(AutomationTopItemCountDisplay));
        MarkAutomationStateDirty();
    }

    partial void OnAutomationLastRunUtcChanged(DateTimeOffset? value)
    {
        UpdateAutomationStatus();
    }
}
