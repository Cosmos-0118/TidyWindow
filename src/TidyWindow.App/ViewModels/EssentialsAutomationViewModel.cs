using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.Services;
using TidyWindow.Core.Maintenance;
using WpfApplication = System.Windows.Application;

namespace TidyWindow.App.ViewModels;

public sealed partial class EssentialsAutomationViewModel : ViewModelBase, IDisposable
{
    private readonly EssentialsAutomationScheduler _scheduler;
    private readonly ActivityLogService _activityLog;
    private readonly ObservableCollection<EssentialsAutomationTaskToggleViewModel> _taskOptions = new();
    private readonly ReadOnlyObservableCollection<EssentialsAutomationTaskToggleViewModel> _taskOptionsView;
    private readonly IReadOnlyList<EssentialsAutomationIntervalOption> _intervalOptions = new[]
    {
        new EssentialsAutomationIntervalOption(60, "60 minutes"),
        new EssentialsAutomationIntervalOption(360, "6 hours"),
        new EssentialsAutomationIntervalOption(720, "12 hours"),
        new EssentialsAutomationIntervalOption(1440, "1 day"),
        new EssentialsAutomationIntervalOption(10080, "1 week"),
        new EssentialsAutomationIntervalOption(43200, "1 month")
    };
    private bool _suspendUpdates;
    private bool _disposed;

    public EssentialsAutomationViewModel(EssentialsAutomationScheduler scheduler, EssentialsTaskCatalog catalog, ActivityLogService activityLog)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _activityLog = activityLog ?? throw new ArgumentNullException(nameof(activityLog));

        if (catalog is null)
        {
            throw new ArgumentNullException(nameof(catalog));
        }

        foreach (var definition in catalog.Tasks)
        {
            var option = new EssentialsAutomationTaskToggleViewModel(definition);
            option.PropertyChanged += OnTaskOptionChanged;
            _taskOptions.Add(option);
        }

        _taskOptionsView = new ReadOnlyObservableCollection<EssentialsAutomationTaskToggleViewModel>(_taskOptions);
        LoadFromSettings(_scheduler.CurrentSettings);
        _scheduler.SettingsChanged += OnSchedulerSettingsChanged;
    }

    public ReadOnlyObservableCollection<EssentialsAutomationTaskToggleViewModel> TaskOptions => _taskOptionsView;

    public IReadOnlyList<EssentialsAutomationIntervalOption> IntervalOptions => _intervalOptions;

    [ObservableProperty]
    private bool _isAutomationEnabled;

    [ObservableProperty]
    private int _intervalMinutes = EssentialsAutomationSettings.MinimumIntervalMinutes;

    [ObservableProperty]
    private DateTimeOffset? _lastRunUtc;

    [ObservableProperty]
    private bool _hasPendingChanges;

    [ObservableProperty]
    private bool _hasSelectedTasks;

    [ObservableProperty]
    private string _selectionSummary = "No tasks selected.";

    [ObservableProperty]
    private string _statusMessage = "Automation is disabled.";

    [ObservableProperty]
    private bool _isBusy;

    partial void OnIsAutomationEnabledChanged(bool value)
    {
        if (_suspendUpdates)
        {
            return;
        }

        HasPendingChanges = true;
        UpdateStatusMessage();
        RunAutomationNowCommand.NotifyCanExecuteChanged();
    }

    partial void OnIntervalMinutesChanged(int value)
    {
        if (_suspendUpdates)
        {
            return;
        }

        var clamped = Math.Clamp(value, EssentialsAutomationSettings.MinimumIntervalMinutes, EssentialsAutomationSettings.MaximumIntervalMinutes);
        if (clamped != value)
        {
            _suspendUpdates = true;
            IntervalMinutes = clamped;
            _suspendUpdates = false;
            return;
        }

        HasPendingChanges = true;
        UpdateStatusMessage();
    }

    partial void OnLastRunUtcChanged(DateTimeOffset? value)
    {
        UpdateStatusMessage();
    }

    partial void OnHasSelectedTasksChanged(bool value)
    {
        RunAutomationNowCommand.NotifyCanExecuteChanged();
        UpdateStatusMessage();
    }

    partial void OnIsBusyChanged(bool value)
    {
        RunAutomationNowCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task ApplyAutomationAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var snapshot = BuildSnapshot();
            await _scheduler.ApplySettingsAsync(snapshot, queueRunImmediately: true);
            HasPendingChanges = false;
        }
        catch (Exception ex)
        {
            _activityLog.LogError("Essentials automation", $"Failed to apply automation settings: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunNow))]
    private async Task RunAutomationNowAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var result = await _scheduler.RunOnceAsync();
            if (!result.WasSkipped && result.TargetCount == 0)
            {
                _activityLog.LogInformation("Essentials automation", "Run completed but no tasks were queued.");
            }
        }
        catch (Exception ex)
        {
            _activityLog.LogError("Essentials automation", $"Failed to run automation: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRunNow()
    {
        return !IsBusy && IsAutomationEnabled && HasSelectedTasks;
    }

    private EssentialsAutomationSettings BuildSnapshot()
    {
        var selected = _taskOptions.Where(option => option.IsSelected).Select(option => option.Id).ToArray();
        var enabled = IsAutomationEnabled && selected.Length > 0;
        return new EssentialsAutomationSettings(enabled, IntervalMinutes, LastRunUtc, selected);
    }

    private void LoadFromSettings(EssentialsAutomationSettings settings)
    {
        _suspendUpdates = true;
        IsAutomationEnabled = settings.AutomationEnabled;
        IntervalMinutes = settings.IntervalMinutes;
        LastRunUtc = settings.LastRunUtc;

        var selected = new HashSet<string>(settings.TaskIds, StringComparer.OrdinalIgnoreCase);
        foreach (var option in _taskOptions)
        {
            option.IsSelected = selected.Contains(option.Id);
        }

        HasPendingChanges = false;
        _suspendUpdates = false;
        UpdateSelectionState();
        UpdateStatusMessage();
    }

    private void ApplySchedulerSettingsUpdate(EssentialsAutomationSettings settings)
    {
        LastRunUtc = settings.LastRunUtc;

        if (HasPendingChanges)
        {
            return;
        }

        LoadFromSettings(settings);
    }

    private void OnTaskOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(EssentialsAutomationTaskToggleViewModel.IsSelected), StringComparison.Ordinal))
        {
            return;
        }

        if (_suspendUpdates)
        {
            return;
        }

        HasPendingChanges = true;
        UpdateSelectionState();
    }

    private void UpdateSelectionState()
    {
        var count = _taskOptions.Count(option => option.IsSelected);
        HasSelectedTasks = count > 0;
        SelectionSummary = count switch
        {
            0 => "No tasks selected.",
            1 => "1 task will run.",
            _ => $"{count} tasks will run."
        };
    }

    private void UpdateStatusMessage()
    {
        if (!HasSelectedTasks)
        {
            StatusMessage = IsAutomationEnabled
                ? "Automation enabled but no tasks selected. Select at least one task."
                : "Select the tasks you want to queue automatically.";
            return;
        }

        if (!IsAutomationEnabled)
        {
            StatusMessage = "Automation is disabled. Turn it on to queue selected tasks automatically.";
            return;
        }

        var intervalLabel = FormatInterval(IntervalMinutes);
        var lastRunLabel = LastRunUtc is null
            ? "First run has not happened yet."
            : $"Last queued {FormatRelative(LastRunUtc.Value)}.";
        StatusMessage = $"Runs every {intervalLabel}. {lastRunLabel}";
    }

    private void OnSchedulerSettingsChanged(object? sender, EssentialsAutomationSettings settings)
    {
        if (_disposed)
        {
            return;
        }

        if (WpfApplication.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(new Action(() => ApplySchedulerSettingsUpdate(settings)));
            return;
        }

        ApplySchedulerSettingsUpdate(settings);
    }

    private static string FormatInterval(int minutes)
    {
        if (minutes >= 43200 && minutes % 43200 == 0)
        {
            var months = minutes / 43200;
            return months == 1 ? "1 month" : $"{months} months";
        }

        if (minutes >= 10080 && minutes % 10080 == 0)
        {
            var weeks = minutes / 10080;
            return weeks == 1 ? "1 week" : $"{weeks} weeks";
        }

        if (minutes >= 1440 && minutes % 1440 == 0)
        {
            var days = minutes / 1440;
            return days == 1 ? "1 day" : $"{days} days";
        }

        if (minutes % 60 == 0 && minutes >= 60)
        {
            var hours = minutes / 60;
            return hours == 1 ? "1 hour" : $"{hours} hours";
        }

        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
    }

    private static string FormatRelative(DateTimeOffset timestamp)
    {
        var delta = DateTimeOffset.UtcNow - timestamp;
        if (delta < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (delta < TimeSpan.FromHours(1))
        {
            var minutes = Math.Max(1, (int)Math.Round(delta.TotalMinutes));
            return minutes == 1 ? "1 minute ago" : $"{minutes} minutes ago";
        }

        if (delta < TimeSpan.FromDays(1))
        {
            var hours = Math.Max(1, (int)Math.Round(delta.TotalHours));
            return hours == 1 ? "1 hour ago" : $"{hours} hours ago";
        }

        var days = Math.Max(1, (int)Math.Round(delta.TotalDays));
        return days == 1 ? "1 day ago" : $"{days} days ago";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _scheduler.SettingsChanged -= OnSchedulerSettingsChanged;
        foreach (var option in _taskOptions)
        {
            option.PropertyChanged -= OnTaskOptionChanged;
        }
    }
}

public sealed partial class EssentialsAutomationTaskToggleViewModel : ObservableObject
{
    public EssentialsAutomationTaskToggleViewModel(EssentialsTaskDefinition definition)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
    }

    public EssentialsTaskDefinition Definition { get; }

    public string Id => Definition.Id;

    public string Title => Definition.Name;

    public string Category => Definition.Category;

    public string Summary => Definition.Summary;

    public string? DurationHint => Definition.DurationHint;

    [ObservableProperty]
    private bool _isSelected;
}

public sealed record EssentialsAutomationIntervalOption(int Minutes, string Label);
