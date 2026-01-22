using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Media;
using WindowsClipboard = System.Windows.Clipboard;
using TidyWindow.App.Services;
using TidyWindow.Core.Maintenance;
using WpfApplication = System.Windows.Application;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;

namespace TidyWindow.App.ViewModels;

public enum EssentialsPivot
{
    Tasks,
    Queue,
    Settings
}

public sealed partial class EssentialsViewModel : ViewModelBase, IDisposable
{
    private readonly EssentialsTaskCatalog _catalog;
    private readonly EssentialsTaskQueue _queue;
    private readonly ActivityLogService _activityLog;
    private readonly MainViewModel _mainViewModel;
    private readonly ISystemRestoreGuardService _restoreGuardService;
    private readonly Dictionary<Guid, EssentialsOperationItemViewModel> _operationLookup = new();
    private readonly Dictionary<Guid, EssentialsQueueOperationSnapshot> _snapshotCache = new();
    private readonly Dictionary<string, EssentialsTaskItemViewModel> _taskLookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _activeTaskCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ICollectionView _tasksView;
    private bool _isDisposed;
    private SystemRestoreGuardPrompt? _activeRestoreGuardPrompt;

    public EssentialsViewModel(
        EssentialsTaskCatalog catalog,
        EssentialsTaskQueue queue,
        ActivityLogService activityLogService,
        MainViewModel mainViewModel,
        EssentialsAutomationViewModel automationViewModel,
        ISystemRestoreGuardService restoreGuardService)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _activityLog = activityLogService ?? throw new ArgumentNullException(nameof(activityLogService));
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        _restoreGuardService = restoreGuardService ?? throw new ArgumentNullException(nameof(restoreGuardService));
        Automation = automationViewModel ?? throw new ArgumentNullException(nameof(automationViewModel));

        Tasks = new ObservableCollection<EssentialsTaskItemViewModel>();
        Operations = new ObservableCollection<EssentialsOperationItemViewModel>();

        foreach (var definition in _catalog.Tasks)
        {
            var vm = new EssentialsTaskItemViewModel(definition);
            Tasks.Add(vm);
            _taskLookup[definition.Id] = vm;
        }

        _tasksView = System.Windows.Data.CollectionViewSource.GetDefaultView(Tasks);
        _tasksView.Filter = FilterTask;

        foreach (var snapshot in _queue.GetSnapshot())
        {
            UpdateTaskState(snapshot);
            var opVm = new EssentialsOperationItemViewModel(snapshot);
            _operationLookup[snapshot.Id] = opVm;
            Operations.Insert(0, opVm);
            _snapshotCache[snapshot.Id] = snapshot;
        }

        if (Operations.Count > 0)
        {
            SelectedOperation = Operations.First();
        }

        UpdateTaskSummaries();
        HasActiveOperations = _activeTaskCounts.Any(pair => pair.Value > 0);

        _queue.OperationChanged += OnQueueOperationChanged;
        _restoreGuardService.PromptRequested += OnRestoreGuardPromptRequested;

        if (_restoreGuardService.TryConsumePendingPrompt(out var pendingPrompt))
        {
            ActivateRestoreGuardPrompt(pendingPrompt);
        }
    }

    public ObservableCollection<EssentialsTaskItemViewModel> Tasks { get; }

    public ICollectionView TasksView => _tasksView;

    public ObservableCollection<EssentialsOperationItemViewModel> Operations { get; }

    public EssentialsAutomationViewModel Automation { get; }

    [ObservableProperty]
    private EssentialsOperationItemViewModel? _selectedOperation;

    [ObservableProperty]
    private bool _hasActiveOperations;

    [ObservableProperty]
    private string _searchText = string.Empty;

    partial void OnHasActiveOperationsChanged(bool value)
    {
        StopActiveRunCommand.NotifyCanExecuteChanged();
    }

    partial void OnSearchTextChanged(string value)
    {
        _tasksView.Refresh();
    }

    private bool FilterTask(object? candidate)
    {
        if (candidate is not EssentialsTaskItemViewModel task)
        {
            return false;
        }

        var query = SearchText;
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return true;
        }

        var haystack = task.SearchIndex;

        foreach (var word in words)
        {
            if (haystack.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }
        }

        return true;
    }

    [ObservableProperty]
    private EssentialsPivot _currentPivot = EssentialsPivot.Tasks;

    [ObservableProperty]
    private string _headline = GetHeadlineForPivot(EssentialsPivot.Tasks);

    [ObservableProperty]
    private EssentialsTaskItemViewModel? _detailsTask;

    [ObservableProperty]
    private bool _isTaskDetailsVisible;

    [ObservableProperty]
    private EssentialsTaskItemViewModel? _pendingRunTask;

    [ObservableProperty]
    private bool _isRunDialogVisible;

    [ObservableProperty]
    private bool _isAutomationConfigurationMode;

    [ObservableProperty]
    private bool _isRestoreGuardDialogVisible;

    [ObservableProperty]
    private string _restoreGuardHeadline = "Create a restore point before continuing";

    [ObservableProperty]
    private string _restoreGuardBody = "Run System Restore manager from Essentials to capture a checkpoint before launching high-impact automation.";

    [ObservableProperty]
    private string _restoreGuardPrimaryActionLabel = "Open restore manager";

    [ObservableProperty]
    private string _restoreGuardSecondaryActionLabel = "Later";

    [ObservableProperty]
    private bool _isOutputDialogVisible;

    [ObservableProperty]
    private EssentialsOperationItemViewModel? _outputDialogOperation;

    public string OutputDialogTitle => OutputDialogOperation is null
        ? "Operation output"
        : $"{OutputDialogOperation.TaskName} output";

    public string RunDialogPrimaryButtonLabel => IsAutomationConfigurationMode ? "Set" : "Queue run";

    partial void OnIsAutomationConfigurationModeChanged(bool value)
    {
        OnPropertyChanged(nameof(RunDialogPrimaryButtonLabel));
    }

    partial void OnOutputDialogOperationChanged(EssentialsOperationItemViewModel? oldValue, EssentialsOperationItemViewModel? newValue)
    {
        OnPropertyChanged(nameof(OutputDialogTitle));
    }

    partial void OnSelectedOperationChanged(EssentialsOperationItemViewModel? oldValue, EssentialsOperationItemViewModel? newValue)
    {
        // No-op hook reserved for future selection side-effects.
    }

    partial void OnCurrentPivotChanged(EssentialsPivot value)
    {
        Headline = GetHeadlineForPivot(value);
    }

    [RelayCommand]
    private void NavigatePivot(EssentialsPivot pivot)
    {
        CurrentPivot = pivot;
    }

    [RelayCommand]
    private void ShowTaskDetails(EssentialsTaskItemViewModel? task)
    {
        if (task is null)
        {
            return;
        }

        DetailsTask = task;
        IsTaskDetailsVisible = true;
    }

    [RelayCommand]
    private void CloseTaskDetails()
    {
        IsTaskDetailsVisible = false;
        DetailsTask = null;
    }

    [RelayCommand]
    private void PrepareTaskRun(EssentialsTaskItemViewModel? task)
    {
        if (task is null)
        {
            return;
        }

        OpenRunDialog(task, automationMode: false);
    }

    private void OpenRunDialog(EssentialsTaskItemViewModel task, bool automationMode)
    {
        IsAutomationConfigurationMode = automationMode;
        PendingRunTask = task;
        IsRunDialogVisible = true;
    }

    [RelayCommand]
    private void CloseRunDialog()
    {
        IsRunDialogVisible = false;
        PendingRunTask = null;
        IsAutomationConfigurationMode = false;
    }

    [RelayCommand]
    private void ConfirmRunConfiguration()
    {
        if (PendingRunTask is null)
        {
            return;
        }

        if (IsAutomationConfigurationMode)
        {
            var name = PendingRunTask.Definition.Name;
            _activityLog.LogInformation("Essentials", $"Updated automation settings for '{name}'.");
            _mainViewModel.SetStatusMessage($"{name} settings updated.");
            CloseRunDialog();
            return;
        }

        QueueTask(PendingRunTask);
        CloseRunDialog();
    }

    [RelayCommand]
    private void ConfigureAutomationTask(EssentialsAutomationTaskToggleViewModel? automationTask)
    {
        if (automationTask is null)
        {
            return;
        }

        if (!_taskLookup.TryGetValue(automationTask.Id, out var task))
        {
            return;
        }

        OpenRunDialog(task, automationMode: true);
    }

    [RelayCommand]
    private void DismissRestoreGuard()
    {
        _activeRestoreGuardPrompt = null;
        IsRestoreGuardDialogVisible = false;
    }

    [RelayCommand]
    private void LaunchRestoreGuardTask()
    {
        if (!_taskLookup.TryGetValue("restore-manager", out var task))
        {
            DismissRestoreGuard();
            return;
        }

        IsRestoreGuardDialogVisible = false;
        OpenRunDialog(task, automationMode: false);
    }

    [RelayCommand]
    private void QueueTask(EssentialsTaskItemViewModel? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            var parameters = task.BuildParameters();
            var snapshot = _queue.Enqueue(task.Definition, parameters);

            var optionSummary = task.GetOptionSummary();
            if (!string.IsNullOrWhiteSpace(optionSummary))
            {
                _activityLog.LogInformation("Essentials", $"Queued '{task.Definition.Name}' ({optionSummary}).");
                _mainViewModel.SetStatusMessage($"Queued {task.Definition.Name} ({optionSummary}).");
            }
            else
            {
                _activityLog.LogInformation("Essentials", $"Queued '{task.Definition.Name}'.");
                _mainViewModel.SetStatusMessage($"Queued {task.Definition.Name}.");
            }
            UpdateTaskState(snapshot);
            _snapshotCache[snapshot.Id] = snapshot;
        }
        catch (Exception ex)
        {
            _activityLog.LogError("Essentials", $"Failed to queue '{task.Definition.Name}': {ex.Message}");
            _mainViewModel.SetStatusMessage($"Queue failed: {ex.Message}");
        }
    }

    private static string GetHeadlineForPivot(EssentialsPivot pivot)
    {
        return pivot switch
        {
            EssentialsPivot.Tasks => "Run high-impact repair and cleanup flows",
            EssentialsPivot.Queue => "Review queue health and inspect transcripts",
            EssentialsPivot.Settings => "Automate essentials maintenance runs",
            _ => "Essentials operations"
        };
    }

    [RelayCommand]
    private void CancelOperation(EssentialsOperationItemViewModel? operation)
    {
        if (operation is null)
        {
            return;
        }

        var snapshot = _queue.Cancel(operation.Id);
        if (snapshot is null)
        {
            return;
        }

        _activityLog.LogWarning("Essentials", $"Cancellation requested for {snapshot.Task.Name}.");
        _mainViewModel.SetStatusMessage($"Cancelling {snapshot.Task.Name}...");
        UpdateTaskState(snapshot);
        _snapshotCache[snapshot.Id] = snapshot;
    }

    [RelayCommand]
    private void ShowOperationOutput(EssentialsOperationItemViewModel? operation)
    {
        if (operation is null)
        {
            return;
        }

        OutputDialogOperation = operation;
        IsOutputDialogVisible = true;
    }

    [RelayCommand]
    private void CloseOutputDialog()
    {
        IsOutputDialogVisible = false;
        OutputDialogOperation = null;
    }

    [RelayCommand]
    private void CopyOperationOutput()
    {
        if (OutputDialogOperation is null)
        {
            return;
        }

        var lines = OutputDialogOperation.DisplayLines;
        if (lines is null || lines.Count == 0)
        {
            _mainViewModel.SetStatusMessage("No output to copy yet.");
            return;
        }

        try
        {
            WindowsClipboard.SetText(string.Join(Environment.NewLine, lines));
            _mainViewModel.SetStatusMessage("Output copied to clipboard.");
        }
        catch
        {
            _mainViewModel.SetStatusMessage("Unable to access clipboard.");
        }
    }

    [RelayCommand]
    private void ClearCompleted()
    {
        var removed = _queue.ClearCompleted();
        if (removed.Count == 0)
        {
            return;
        }

        foreach (var snapshot in removed)
        {
            _snapshotCache.Remove(snapshot.Id);
            if (_operationLookup.TryGetValue(snapshot.Id, out var vm))
            {
                Operations.Remove(vm);
                _operationLookup.Remove(snapshot.Id);
            }
        }

        if (OutputDialogOperation is not null && removed.Any(snapshot => snapshot.Id == OutputDialogOperation.Id))
        {
            CloseOutputDialog();
        }

        UpdateTaskSummaries();
        _activityLog.LogInformation("Essentials", $"Cleared {removed.Count} completed run(s).");

        if (Operations.Count == 0)
        {
            SelectedOperation = null;
        }
        else if (SelectedOperation is null)
        {
            SelectedOperation = Operations.FirstOrDefault();
        }
    }

    [RelayCommand]
    private void RetryFailed()
    {
        var snapshots = _queue.RetryFailed();
        if (snapshots.Count == 0)
        {
            _mainViewModel.SetStatusMessage("No failed runs to retry.");
            return;
        }

        foreach (var snapshot in snapshots)
        {
            UpdateTaskState(snapshot);
            _snapshotCache[snapshot.Id] = snapshot;
        }

        _activityLog.LogInformation("Essentials", $"Retrying {snapshots.Count} run(s).");
        _mainViewModel.SetStatusMessage($"Retrying {snapshots.Count} run(s)...");
    }

    [RelayCommand(CanExecute = nameof(CanStopActiveRun))]
    private void StopActiveRun()
    {
        var target = Operations.FirstOrDefault(op => op.IsActive);
        if (target is null)
        {
            _mainViewModel.SetStatusMessage("No active essentials runs to stop.");
            return;
        }

        CancelOperation(target);
    }

    private bool CanStopActiveRun()
    {
        return HasActiveOperations;
    }

    private void OnQueueOperationChanged(object? sender, EssentialsQueueChangedEventArgs e)
    {
        if (WpfApplication.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(() => ApplySnapshot(e.Snapshot));
        }
        else
        {
            ApplySnapshot(e.Snapshot);
        }
    }

    private void ApplySnapshot(EssentialsQueueOperationSnapshot snapshot)
    {
        if (_isDisposed)
        {
            return;
        }

        _snapshotCache.TryGetValue(snapshot.Id, out var previous);
        UpdateTaskState(snapshot);
        LogSnapshotChange(snapshot, previous);

        if (!_operationLookup.TryGetValue(snapshot.Id, out var vm))
        {
            vm = new EssentialsOperationItemViewModel(snapshot);
            _operationLookup[snapshot.Id] = vm;
            Operations.Insert(0, vm);
        }

        vm.Update(snapshot);
        _snapshotCache[snapshot.Id] = snapshot;

        if (previous is null)
        {
            SelectedOperation = vm;
        }
        else if (SelectedOperation is null)
        {
            SelectedOperation = vm;
        }
    }

    private void UpdateTaskState(EssentialsQueueOperationSnapshot snapshot)
    {
        if (_snapshotCache.TryGetValue(snapshot.Id, out var previous))
        {
            if (previous.IsActive && !snapshot.IsActive)
            {
                DecrementActive(previous.Task.Id);
            }
            else if (!previous.IsActive && snapshot.IsActive)
            {
                IncrementActive(snapshot.Task.Id);
            }
        }
        else if (snapshot.IsActive)
        {
            IncrementActive(snapshot.Task.Id);
        }

        if (!_taskLookup.TryGetValue(snapshot.Task.Id, out var vm))
        {
            return;
        }

        var activeCount = _activeTaskCounts.TryGetValue(snapshot.Task.Id, out var value) ? value : 0;
        vm.UpdateQueueState(activeCount, snapshot.LastMessage, snapshot.Status);

        HasActiveOperations = _activeTaskCounts.Any(pair => pair.Value > 0);
    }

    private void UpdateTaskSummaries()
    {
        foreach (var task in Tasks)
        {
            var snapshot = _snapshotCache.Values
                .Where(s => string.Equals(s.Task.Id, task.Definition.Id, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.CompletedAt ?? DateTimeOffset.MinValue)
                .ThenByDescending(s => s.StartedAt ?? DateTimeOffset.MinValue)
                .FirstOrDefault();

            var activeCount = _activeTaskCounts.TryGetValue(task.Definition.Id, out var value) ? value : 0;
            task.UpdateQueueState(activeCount, snapshot?.LastMessage, snapshot?.Status);
        }

        HasActiveOperations = _activeTaskCounts.Any(pair => pair.Value > 0);
    }

    private void IncrementActive(string taskId)
    {
        if (!_activeTaskCounts.TryGetValue(taskId, out var value))
        {
            _activeTaskCounts[taskId] = 1;
        }
        else
        {
            _activeTaskCounts[taskId] = value + 1;
        }
    }

    private void DecrementActive(string taskId)
    {
        if (!_activeTaskCounts.TryGetValue(taskId, out var value))
        {
            return;
        }

        value--;
        if (value <= 0)
        {
            _activeTaskCounts.Remove(taskId);
        }
        else
        {
            _activeTaskCounts[taskId] = value;
        }
    }

    private void LogSnapshotChange(EssentialsQueueOperationSnapshot snapshot, EssentialsQueueOperationSnapshot? previous)
    {
        if (previous is not null
            && previous.Status == snapshot.Status
            && previous.AttemptCount == snapshot.AttemptCount
            && string.Equals(previous.LastMessage ?? string.Empty, snapshot.LastMessage ?? string.Empty, StringComparison.Ordinal))
        {
            return;
        }

        switch (snapshot.Status)
        {
            case EssentialsQueueStatus.Pending:
                if (previous is null || previous.Status != EssentialsQueueStatus.Pending)
                {
                    _activityLog.LogInformation("Essentials", $"{snapshot.Task.Name} queued.");
                }
                break;

            case EssentialsQueueStatus.Running:
                if (previous is null || previous.Status != EssentialsQueueStatus.Running)
                {
                    _activityLog.LogInformation("Essentials", $"{snapshot.Task.Name} running (attempt {snapshot.AttemptCount}).");
                }
                break;

            case EssentialsQueueStatus.Succeeded:
                if (previous is null || previous.Status != EssentialsQueueStatus.Succeeded)
                {
                    _activityLog.LogSuccess("Essentials", $"{snapshot.Task.Name} completed.", BuildDetails(snapshot));
                    _mainViewModel.SetStatusMessage($"{snapshot.Task.Name} completed.");
                }
                break;

            case EssentialsQueueStatus.Failed:
                if (previous is null || previous.Status != EssentialsQueueStatus.Failed || previous.AttemptCount != snapshot.AttemptCount)
                {
                    var failure = string.IsNullOrWhiteSpace(snapshot.LastMessage) ? "Execution failed." : snapshot.LastMessage.Trim();
                    _activityLog.LogError("Essentials", $"{snapshot.Task.Name} failed: {failure}", BuildDetails(snapshot));
                    _mainViewModel.SetStatusMessage($"{snapshot.Task.Name} failed.");
                }
                break;

            case EssentialsQueueStatus.Cancelled:
                if (previous is null || previous.Status != EssentialsQueueStatus.Cancelled)
                {
                    var reason = string.IsNullOrWhiteSpace(snapshot.LastMessage) ? "Cancelled." : snapshot.LastMessage.Trim();
                    _activityLog.LogWarning("Essentials", $"{snapshot.Task.Name} cancelled: {reason}");
                    _mainViewModel.SetStatusMessage($"{snapshot.Task.Name} cancelled.");
                }
                break;
        }
    }

    private static IEnumerable<string>? BuildDetails(EssentialsQueueOperationSnapshot snapshot)
    {
        var lines = new List<string>();

        if (!snapshot.Output.IsDefaultOrEmpty && snapshot.Output.Length > 0)
        {
            lines.Add("--- Output ---");
            foreach (var line in snapshot.Output)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    lines.Add(line);
                }
            }
        }

        if (!snapshot.Errors.IsDefaultOrEmpty && snapshot.Errors.Length > 0)
        {
            lines.Add("--- Errors ---");
            foreach (var line in snapshot.Errors)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    lines.Add(line);
                }
            }
        }

        return lines.Count == 0 ? null : lines;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _queue.OperationChanged -= OnQueueOperationChanged;
        _restoreGuardService.PromptRequested -= OnRestoreGuardPromptRequested;
        Automation.Dispose();
    }

    private void OnRestoreGuardPromptRequested(object? sender, SystemRestoreGuardPromptEventArgs e)
    {
        if (e?.Prompt is null)
        {
            return;
        }

        if (WpfApplication.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(() => ActivateRestoreGuardPrompt(e.Prompt));
        }
        else
        {
            ActivateRestoreGuardPrompt(e.Prompt);
        }
    }

    private void ActivateRestoreGuardPrompt(SystemRestoreGuardPrompt prompt)
    {
        _activeRestoreGuardPrompt = prompt;
        RestoreGuardHeadline = prompt.Headline;
        RestoreGuardBody = prompt.Body;
        RestoreGuardPrimaryActionLabel = prompt.PrimaryActionLabel;
        RestoreGuardSecondaryActionLabel = prompt.SecondaryActionLabel;
        IsRestoreGuardDialogVisible = true;
    }
}

public sealed partial class EssentialsTaskItemViewModel : ObservableObject
{
    private static readonly SolidColorBrush RunningChipBrush = new(MediaColor.FromRgb(56, 189, 248));
    private static readonly SolidColorBrush WaitingChipBrush = new(MediaColor.FromRgb(250, 204, 21));
    private static readonly SolidColorBrush SuccessChipBrush = new(MediaColor.FromRgb(34, 197, 94));
    private static readonly SolidColorBrush ErrorChipBrush = new(MediaColor.FromRgb(248, 113, 113));
    private static readonly HashSet<string> HighRiskTasks = new(StringComparer.OrdinalIgnoreCase)
    {
        "tpm-bitlocker-secureboot-repair",
        "activation-licensing-repair",
        "device-drivers-pnp-repair",
        "profile-logon-repair",
        "system-health",
        "disk-check"
    };
    private readonly string _searchIndex;
    private readonly EssentialsTaskOptionViewModel? _localeResetOption;
    private readonly ImmutableArray<string> _highlightsShort;

    public EssentialsTaskItemViewModel(EssentialsTaskDefinition definition)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));

        if (definition.Options.IsDefaultOrEmpty || definition.Options.Length == 0)
        {
            Options = Array.Empty<EssentialsTaskOptionViewModel>();
        }
        else
        {
            Options = definition.Options.Select(option => new EssentialsTaskOptionViewModel(option)).ToList();
        }

        _localeResetOption = Options.FirstOrDefault(o => string.Equals(o.Definition.ParameterName, "ApplyLocaleReset", StringComparison.OrdinalIgnoreCase));

        if (_localeResetOption is not null)
        {
            _localeResetOption.PropertyChanged += LocaleResetOptionOnPropertyChanged;
        }

        LocalePresets = BuildLocalePresets();

        if (IsTimeRegionTask && LocalePresets.Count > 0)
        {
            SelectedLocalePreset = LocalePresets[0];
        }

        _highlightsShort = definition.Highlights.Select(h => TruncateForCard(h, 72)).ToImmutableArray();
        _searchIndex = BuildSearchIndex();
    }

    public EssentialsTaskDefinition Definition { get; }

    public string Id => Definition.Id;

    public string Title => Definition.Name;

    public string Summary => Definition.Summary;

    public string Category => Definition.Category;

    public ImmutableArray<string> Highlights => Definition.Highlights;

    public ImmutableArray<string> HighlightsShort => _highlightsShort;

    public string? DurationHint => Definition.DurationHint;

    public string? DurationChipText => TruncateForCard(DurationSummary, 68);

    public string? DetailedDescription => Definition.DetailedDescription;

    public string? DocumentationLink => Definition.DocumentationLink;

    public bool IsRecommendedForAutomation => Definition.IsRecommendedForAutomation;

    public bool IsHighRisk => HighRiskTasks.Contains(Definition.Id);

    public bool IsDefenderTask => string.Equals(Definition.Id, "defender-repair", StringComparison.OrdinalIgnoreCase);

    public bool IsTimeRegionTask => string.Equals(Definition.Id, "time-region-repair", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<EssentialsTaskOptionViewModel> Options { get; }

    public bool HasOptions => Options.Count > 0;

    public IReadOnlyList<LocalePresetOption> LocalePresets { get; }

    [ObservableProperty]
    private LocalePresetOption? _selectedLocalePreset;

    public bool ShouldShowLocalePresetPicker => IsTimeRegionTask && _localeResetOption?.IsEnabled == true;

    public string SearchIndex => _searchIndex;

    [ObservableProperty]
    private bool _useFullScan;

    [ObservableProperty]
    private bool _skipSignatureUpdate;

    [ObservableProperty]
    private bool _skipThreatScan;

    [ObservableProperty]
    private bool _skipServiceHeal;

    [ObservableProperty]
    private bool _skipRealtimeHeal;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isQueued;

    [ObservableProperty]
    private string? _lastStatus;

    [ObservableProperty]
    private MediaBrush? _statusChipBrush;

    [ObservableProperty]
    private string? _statusChipLabel;

    [ObservableProperty]
    private bool _hasStatusChip;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _progressStatusText = "Ready";

    [ObservableProperty]
    private bool _hasProgress;

    public string? DurationSummary
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DurationHint))
            {
                return null;
            }

            var text = DurationHint.Trim();
            if (text.StartsWith("Approx.", StringComparison.OrdinalIgnoreCase))
            {
                text = text[7..].Trim();
            }

            if (text.EndsWith('.') && text.Length > 1)
            {
                text = text.TrimEnd('.').Trim();
            }

            return string.IsNullOrWhiteSpace(text)
                ? null
                : $"Approx. time: {text}";
        }
    }

    private string BuildSearchIndex()
    {
        var builder = new StringBuilder();

        void Append(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }
                builder.Append(value);
            }
        }

        Append(Title);
        Append(Category);
        Append(Summary);
        Append(DurationHint);
        Append(DetailedDescription);

        if (Highlights.Length > 0)
        {
            foreach (var highlight in Highlights)
            {
                Append(highlight);
            }
        }

        return builder.ToString();
    }

    private static string TruncateForCard(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value.Trim();
        if (text.Length <= maxLength)
        {
            return text;
        }

        var trimmed = text[..maxLength].TrimEnd();
        return string.Concat(trimmed, "…");
    }

    public void UpdateQueueState(int activeCount, string? status, EssentialsQueueStatus? queueStatus)
    {
        IsActive = queueStatus == EssentialsQueueStatus.Running || activeCount > 0;
        IsQueued = queueStatus == EssentialsQueueStatus.Pending;

        if (!string.IsNullOrWhiteSpace(status))
        {
            LastStatus = status.Trim();
        }

        UpdateStatusChip(queueStatus);
    }

    private void UpdateStatusChip(EssentialsQueueStatus? queueStatus)
    {
        if (queueStatus is null)
        {
            HasStatusChip = false;
            StatusChipLabel = null;
            StatusChipBrush = null;
            HasProgress = false;
            ProgressValue = 0;
            ProgressStatusText = "Ready";
            return;
        }

        HasStatusChip = true;

        string statusLabel;
        MediaBrush brush;
        double progress;

        switch (queueStatus)
        {
            case EssentialsQueueStatus.Running:
                statusLabel = "Running";
                brush = RunningChipBrush;
                progress = 0.65;
                break;
            case EssentialsQueueStatus.Pending:
                statusLabel = "Waiting";
                brush = WaitingChipBrush;
                progress = 0.3;
                break;
            case EssentialsQueueStatus.Failed:
                statusLabel = "Error";
                brush = ErrorChipBrush;
                progress = 1;
                break;
            case EssentialsQueueStatus.Succeeded:
                statusLabel = "Completed";
                brush = SuccessChipBrush;
                progress = 1;
                break;
            case EssentialsQueueStatus.Cancelled:
                statusLabel = "Cancelled";
                brush = WaitingChipBrush;
                progress = 1;
                break;
            default:
                HasStatusChip = false;
                StatusChipLabel = null;
                StatusChipBrush = null;
                HasProgress = false;
                ProgressValue = 0;
                ProgressStatusText = "Ready";
                return;
        }

        StatusChipLabel = statusLabel;
        StatusChipBrush = brush;
        ProgressValue = progress;
        ProgressStatusText = statusLabel;
        HasProgress = queueStatus is EssentialsQueueStatus.Pending or EssentialsQueueStatus.Running;
    }

    private IReadOnlyList<LocalePresetOption> BuildLocalePresets()
    {
        if (!IsTimeRegionTask)
        {
            return Array.Empty<LocalePresetOption>();
        }

        var presets = new List<LocalePresetOption>
        {
            new("Keep current locale/language", null, null, "Leave existing preferences untouched (recommended).")
        };

        var cultures = CultureInfo.GetCultures(CultureTypes.AllCultures)
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(c => c.EnglishName)
            .ThenBy(c => c.Name);

        foreach (var culture in cultures)
        {
            presets.Add(new LocalePresetOption(
                name: $"{culture.EnglishName} ({culture.Name})",
                locale: culture.Name,
                language: culture.Name,
                description: "Default regional settings"));
        }

        return presets;
    }

    private void LocaleResetOptionOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!IsTimeRegionTask || e.PropertyName != nameof(EssentialsTaskOptionViewModel.IsEnabled))
        {
            return;
        }

        if (_localeResetOption?.IsEnabled == true && SelectedLocalePreset is null && LocalePresets.Count > 0)
        {
            SelectedLocalePreset = LocalePresets[0];
        }

        OnPropertyChanged(nameof(ShouldShowLocalePresetPicker));
    }

    public IReadOnlyDictionary<string, object?>? BuildParameters()
    {
        Dictionary<string, object?>? parameters = null;

        if (HasOptions)
        {
            foreach (var option in Options)
            {
                if (option.TryGetParameter(out var name, out var value))
                {
                    parameters ??= new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    parameters[name] = value;
                }
            }
        }

        if (IsTimeRegionTask && _localeResetOption?.IsEnabled == true && SelectedLocalePreset is { HasOverrides: true })
        {
            parameters ??= new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(SelectedLocalePreset.Locale))
            {
                parameters["Locale"] = SelectedLocalePreset.Locale;
            }

            if (!string.IsNullOrWhiteSpace(SelectedLocalePreset.Language))
            {
                parameters["Language"] = SelectedLocalePreset.Language;
            }
        }

        if (IsDefenderTask)
        {
            parameters ??= new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            if (SkipThreatScan)
            {
                parameters["SkipThreatScan"] = true;
            }
            else if (UseFullScan)
            {
                parameters["FullScan"] = true;
            }

            if (SkipSignatureUpdate)
            {
                parameters["SkipSignatureUpdate"] = true;
            }

            if (SkipServiceHeal)
            {
                parameters["SkipServiceHeal"] = true;
            }

            if (SkipRealtimeHeal)
            {
                parameters["SkipRealtimeHeal"] = true;
            }

            return parameters.Count > 0 ? parameters : null;
        }

        return parameters?.Count > 0 ? parameters : null;
    }

    public string? GetOptionSummary()
    {
        var parts = new List<string>();

        if (HasOptions)
        {
            foreach (var option in Options)
            {
                var summary = option.GetSummaryLabel();
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    parts.Add(summary);
                }
            }
        }

        if (IsDefenderTask)
        {
            if (SkipThreatScan)
            {
                parts.Add("Scan skipped");
            }
            else if (UseFullScan)
            {
                parts.Add("Full scan");
            }

            if (SkipSignatureUpdate)
            {
                parts.Add("Skip signature update");
            }

            if (SkipServiceHeal)
            {
                parts.Add("Skip service repair");
            }

            if (SkipRealtimeHeal)
            {
                parts.Add("Skip real-time heal");
            }
        }

        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

    partial void OnSkipThreatScanChanged(bool oldValue, bool newValue)
    {
        if (newValue && UseFullScan)
        {
            UseFullScan = false;
        }
    }

    partial void OnUseFullScanChanged(bool oldValue, bool newValue)
    {
        if (newValue && SkipThreatScan)
        {
            SkipThreatScan = false;
        }
    }
}

public sealed class LocalePresetOption
{
    public LocalePresetOption(string name, string? locale, string? language, string description)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "Locale preset" : name.Trim();
        Locale = string.IsNullOrWhiteSpace(locale) ? null : locale.Trim();
        Language = string.IsNullOrWhiteSpace(language) ? null : language.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
    }

    public string Name { get; }

    public string? Locale { get; }

    public string? Language { get; }

    public string? Description { get; }

    public bool HasOverrides => !string.IsNullOrWhiteSpace(Locale) || !string.IsNullOrWhiteSpace(Language);

    public string Caption => HasOverrides ? $"{Locale ?? "(system)"} / {Language ?? "(system)"}" : "Use current settings";
}

public sealed partial class EssentialsTaskOptionViewModel : ObservableObject
{
    public EssentialsTaskOptionViewModel(EssentialsTaskOptionDefinition definition)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _isEnabled = definition.DefaultValue;
    }

    public EssentialsTaskOptionDefinition Definition { get; }

    public string Label => Definition.Label;

    public string? Description => Definition.Description;

    public bool DefaultValue => Definition.DefaultValue;

    [ObservableProperty]
    private bool _isEnabled;

    public bool TryGetParameter(out string parameterName, out object? value)
    {
        parameterName = Definition.ParameterName;
        value = true;

        if (Definition.Mode == EssentialsTaskOptionMode.EmitWhenTrue)
        {
            if (!IsEnabled)
            {
                return false;
            }

            return true;
        }

        if (Definition.Mode == EssentialsTaskOptionMode.EmitWhenFalse)
        {
            if (IsEnabled)
            {
                return false;
            }

            return true;
        }

        return false;
    }

    public string? GetSummaryLabel()
    {
        if (IsEnabled == DefaultValue)
        {
            return null;
        }

        return IsEnabled ? Label : $"Skip {Label}";
    }
}

public sealed partial class EssentialsOperationItemViewModel : ObservableObject
{
    public EssentialsOperationItemViewModel(EssentialsQueueOperationSnapshot snapshot)
    {
        Id = snapshot.Id;
        TaskName = snapshot.Task.Name;
        Update(snapshot);
    }

    public Guid Id { get; }

    public string TaskName { get; }

    [ObservableProperty]
    private string _statusLabel = "Pending";

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private DateTimeOffset? _completedAt;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _hasErrors;

    [ObservableProperty]
    private ImmutableArray<string> _output = ImmutableArray<string>.Empty;

    [ObservableProperty]
    private ImmutableArray<string> _errors = ImmutableArray<string>.Empty;

    [ObservableProperty]
    private bool _isCancellationRequested;

    public string AttemptLabel { get; private set; } = string.Empty;

    public IReadOnlyList<string> DisplayLines
        => !Errors.IsDefaultOrEmpty && Errors.Length > 0 ? Errors : Output;

    public void Update(EssentialsQueueOperationSnapshot snapshot)
    {
        StatusLabel = ResolveStatusLabel(snapshot);

        Message = snapshot.LastMessage;
        CompletedAt = snapshot.CompletedAt?.ToLocalTime();
        IsActive = snapshot.IsActive;
        HasErrors = !snapshot.Errors.IsDefaultOrEmpty && snapshot.Errors.Length > 0;
        Output = snapshot.Output;
        Errors = snapshot.Errors;
        IsCancellationRequested = snapshot.IsCancellationRequested;
        AttemptLabel = snapshot.AttemptCount > 1 ? $"Attempt {snapshot.AttemptCount}" : string.Empty;
        OnPropertyChanged(nameof(AttemptLabel));
        OnPropertyChanged(nameof(CanCancel));

    }

    public bool CanCancel => IsActive && !IsCancellationRequested;

    partial void OnIsActiveChanged(bool oldValue, bool newValue)
    {
        OnPropertyChanged(nameof(CanCancel));
    }

    partial void OnIsCancellationRequestedChanged(bool oldValue, bool newValue)
    {
        OnPropertyChanged(nameof(CanCancel));
    }

    private static string ResolveStatusLabel(EssentialsQueueOperationSnapshot snapshot)
    {
        if (snapshot.IsCancellationRequested)
        {
            return snapshot.Status switch
            {
                EssentialsQueueStatus.Pending => "Cancelling",
                EssentialsQueueStatus.Running => "Stopping",
                _ => "Cancelled"
            };
        }

        return snapshot.Status switch
        {
            EssentialsQueueStatus.Pending => "Queued",
            EssentialsQueueStatus.Running => "Running",
            EssentialsQueueStatus.Succeeded => "Completed",
            EssentialsQueueStatus.Failed => "Failed",
            EssentialsQueueStatus.Cancelled => "Cancelled",
            _ => snapshot.Status.ToString()
        };
    }

    partial void OnOutputChanged(ImmutableArray<string> oldValue, ImmutableArray<string> newValue)
    {
        OnPropertyChanged(nameof(DisplayLines));
    }

    partial void OnErrorsChanged(ImmutableArray<string> oldValue, ImmutableArray<string> newValue)
    {
        OnPropertyChanged(nameof(DisplayLines));
    }
}
