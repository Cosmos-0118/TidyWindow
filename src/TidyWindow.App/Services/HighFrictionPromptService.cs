using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using WpfApplication = System.Windows.Application;

namespace TidyWindow.App.Services;

public sealed class HighFrictionPromptService : IHighFrictionPromptService
{
    private readonly ITrayService _trayService;
    private readonly AppRestartService _restartService;
    private readonly ActivityLogService _activityLog;
    private readonly Dictionary<string, DateTimeOffset> _displayedPrompts = new(StringComparer.Ordinal);
    private static readonly TimeSpan PromptCooldown = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PromptRetention = TimeSpan.FromHours(6);

    public HighFrictionPromptService(ITrayService trayService, AppRestartService restartService, ActivityLogService activityLog)
    {
        _trayService = trayService ?? throw new ArgumentNullException(nameof(trayService));
        _restartService = restartService ?? throw new ArgumentNullException(nameof(restartService));
        _activityLog = activityLog ?? throw new ArgumentNullException(nameof(activityLog));
    }

    public void TryShowPrompt(HighFrictionScenario scenario, ActivityLogEntry entry)
    {
        if (scenario == HighFrictionScenario.None)
        {
            return;
        }

        var key = BuildKey(scenario, entry);
        if (!ShouldDisplayPrompt(key))
        {
            return;
        }

        var metadata = CreateMetadata(scenario, entry);
        var window = new Views.Dialogs.HighFrictionPromptWindow(metadata.Title, metadata.Message, metadata.Suggestion)
        {
            Owner = WpfApplication.Current?.MainWindow,
            WindowStartupLocation = WpfApplication.Current?.MainWindow is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner
        };

        _activityLog.LogWarning("PulseGuard", metadata.LogMessage);

        var dialogResult = window.ShowDialog();
        var result = window.Result;

        switch (result)
        {
            case HighFrictionPromptResult.ViewLogs:
                _activityLog.LogInformation("PulseGuard", "User opened logs from PulseGuard prompt.");
                _trayService.NavigateToLogs();
                break;
            case HighFrictionPromptResult.RestartApp:
                var restarted = _restartService.TryRestart();
                if (!restarted)
                {
                    System.Windows.MessageBox.Show("TidyWindow could not restart automatically. Please close and reopen the app manually.", "PulseGuard", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                break;
            default:
                _activityLog.LogInformation("PulseGuard", "PulseGuard prompt dismissed.");
                break;
        }
    }

    private static string BuildKey(HighFrictionScenario scenario, ActivityLogEntry entry)
    {
        var builder = new StringBuilder();
        builder.Append((int)scenario)
               .Append('|')
               .Append(Normalize(entry.Source));

        if (!string.IsNullOrWhiteSpace(entry.Message))
        {
            builder.Append('|').Append(Normalize(entry.Message));
        }

        foreach (var detail in entry.Details)
        {
            if (!string.IsNullOrWhiteSpace(detail))
            {
                builder.Append('|').Append(Normalize(detail));
            }
        }

        return builder.ToString();
    }

    private bool ShouldDisplayPrompt(string key)
    {
        var now = DateTimeOffset.UtcNow;

        lock (_displayedPrompts)
        {
            PruneExpiredEntriesUnsafe(now);

            if (_displayedPrompts.TryGetValue(key, out var previous) && now - previous < PromptCooldown)
            {
                return false;
            }

            _displayedPrompts[key] = now;
            return true;
        }
    }

    private void PruneExpiredEntriesUnsafe(DateTimeOffset now)
    {
        if (_displayedPrompts.Count == 0)
        {
            return;
        }

        List<string>? expiredKeys = null;
        foreach (var entry in _displayedPrompts)
        {
            if (now - entry.Value > PromptRetention)
            {
                expiredKeys ??= new List<string>(_displayedPrompts.Count);
                expiredKeys.Add(entry.Key);
            }
        }

        if (expiredKeys is null)
        {
            return;
        }

        foreach (var key in expiredKeys)
        {
            _displayedPrompts.Remove(key);
        }
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Trim().ToLowerInvariant();
    }

    private static PromptMetadata CreateMetadata(HighFrictionScenario scenario, ActivityLogEntry entry)
    {
        var details = entry.Message;
        if (string.IsNullOrWhiteSpace(details) && entry.Details.Length > 0)
        {
            details = entry.Details[0];
        }

        details ??= "Review the recent automation log for specifics.";

        return scenario switch
        {
            HighFrictionScenario.LegacyPowerShell => new PromptMetadata(
                Title: "PowerShell upgrade required",
                Message: "Automation halted because the system is still using Windows PowerShell 5.1. Install PowerShell 7 or later, then relaunch TidyWindow.",
                Suggestion: details,
                LogMessage: "PulseGuard detected missing PowerShell 7 support."),
            HighFrictionScenario.AppRestartRequired => new PromptMetadata(
                Title: "App restart recommended",
                Message: "New tooling was added outside the current session. Restart TidyWindow so PulseGuard can rehydrate paths and finish setup.",
                Suggestion: details,
                LogMessage: "PulseGuard detected app restart requirement."),
            _ => new PromptMetadata(
                Title: "PulseGuard notice",
                Message: "PulseGuard found something that needs your attention.",
                Suggestion: details,
                LogMessage: "PulseGuard displayed high-friction prompt.")
        };
    }

    private readonly record struct PromptMetadata(string Title, string Message, string Suggestion, string LogMessage);
}
