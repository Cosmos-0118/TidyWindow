using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.Services;
using TidyWindow.Core.Maintenance;
using WpfApplication = System.Windows.Application;

namespace TidyWindow.App.ViewModels;

public sealed partial class EssentialsViewModel : ViewModelBase, IDisposable
{
    private readonly EssentialsTaskCatalog _catalog;
    private readonly EssentialsTaskQueue _queue;
    private readonly ActivityLogService _activityLog;
    private readonly MainViewModel _mainViewModel;
    private readonly Dictionary<Guid, EssentialsOperationItemViewModel> _operationLookup = new();
    private readonly Dictionary<Guid, EssentialsQueueOperationSnapshot> _snapshotCache = new();
    private readonly Dictionary<string, EssentialsTaskItemViewModel> _taskLookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _activeTaskCounts = new(StringComparer.OrdinalIgnoreCase);
    private bool _isDisposed;

    public EssentialsViewModel(EssentialsTaskCatalog catalog, EssentialsTaskQueue queue, ActivityLogService activityLogService, MainViewModel mainViewModel)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _activityLog = activityLogService ?? throw new ArgumentNullException(nameof(activityLogService));
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));

        Tasks = new ObservableCollection<EssentialsTaskItemViewModel>();
        Operations = new ObservableCollection<EssentialsOperationItemViewModel>();

        foreach (var definition in _catalog.Tasks)
        {
            var vm = new EssentialsTaskItemViewModel(definition);
            Tasks.Add(vm);
            _taskLookup[definition.Id] = vm;
        }

        foreach (var snapshot in _queue.GetSnapshot())
        {
            _snapshotCache[snapshot.Id] = snapshot;
            UpdateTaskState(snapshot);
            var opVm = new EssentialsOperationItemViewModel(snapshot);
            _operationLookup[snapshot.Id] = opVm;
            Operations.Insert(0, opVm);
        }

        if (Operations.Count > 0)
        {
            SelectedOperation = Operations.First();
        }

        UpdateTaskSummaries();
        HasActiveOperations = _activeTaskCounts.Any(pair => pair.Value > 0);

        _queue.OperationChanged += OnQueueOperationChanged;
    }

    public ObservableCollection<EssentialsTaskItemViewModel> Tasks { get; }

    public ObservableCollection<EssentialsOperationItemViewModel> Operations { get; }

    [ObservableProperty]
    private EssentialsOperationItemViewModel? _selectedOperation;

    [ObservableProperty]
    private bool _hasActiveOperations;

    [ObservableProperty]
    private string _headline = "Run high-impact repair and cleanup flows";

    partial void OnSelectedOperationChanged(EssentialsOperationItemViewModel? oldValue, EssentialsOperationItemViewModel? newValue)
    {
        // No-op hook reserved for future selection side-effects.
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
        }
        catch (Exception ex)
        {
            _activityLog.LogError("Essentials", $"Failed to queue '{task.Definition.Name}': {ex.Message}");
            _mainViewModel.SetStatusMessage($"Queue failed: {ex.Message}");
        }
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
        _snapshotCache[snapshot.Id] = snapshot;
        UpdateTaskState(snapshot);
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
            _snapshotCache[snapshot.Id] = snapshot;
            UpdateTaskState(snapshot);
        }

        _activityLog.LogInformation("Essentials", $"Retrying {snapshots.Count} run(s).");
        _mainViewModel.SetStatusMessage($"Retrying {snapshots.Count} run(s)...");
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
        vm.UpdateQueueState(activeCount, snapshot.LastMessage);

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
            task.UpdateQueueState(activeCount, snapshot?.LastMessage);
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
    }
}

public sealed partial class EssentialsTaskItemViewModel : ObservableObject
{
    public EssentialsTaskItemViewModel(EssentialsTaskDefinition definition)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
    }

    public EssentialsTaskDefinition Definition { get; }

    public string Id => Definition.Id;

    public string Title => Definition.Name;

    public string Summary => Definition.Summary;

    public string Category => Definition.Category;

    public ImmutableArray<string> Highlights => Definition.Highlights;

    public string? DurationHint => Definition.DurationHint;

    public string? DetailedDescription => Definition.DetailedDescription;

    public string? DocumentationLink => Definition.DocumentationLink;

    public bool IsDefenderTask => string.Equals(Definition.Id, "defender-repair", StringComparison.OrdinalIgnoreCase);

    public bool IsSpoolerTask => string.Equals(Definition.Id, "print-spooler-recovery", StringComparison.OrdinalIgnoreCase);

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
    private bool _includeServiceReset = true;

    [ObservableProperty]
    private bool _includeQueuePurge = true;

    [ObservableProperty]
    private bool _includeDriverCleanup = true;

    [ObservableProperty]
    private bool _includeDllRegistration = true;

    [ObservableProperty]
    private bool _includeIsolationPolicyReset = true;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isQueued;

    [ObservableProperty]
    private string? _lastStatus;

    [ObservableProperty]
    private bool _isDetailsVisible;

    public void UpdateQueueState(int activeCount, string? status)
    {
        IsActive = activeCount > 0;
        IsQueued = activeCount > 0;

        if (!string.IsNullOrWhiteSpace(status))
        {
            LastStatus = status.Trim();
        }
    }

    public IReadOnlyDictionary<string, object?>? BuildParameters()
    {
        if (IsDefenderTask)
        {
            var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

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

        if (IsSpoolerTask)
        {
            var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            if (!IncludeServiceReset)
            {
                parameters["SkipServiceReset"] = true;
            }

            if (!IncludeQueuePurge)
            {
                parameters["SkipSpoolPurge"] = true;
            }

            if (!IncludeDriverCleanup)
            {
                parameters["SkipDriverRefresh"] = true;
            }

            if (!IncludeDllRegistration)
            {
                parameters["SkipDllRegistration"] = true;
            }

            if (!IncludeIsolationPolicyReset)
            {
                parameters["SkipPrintIsolationPolicy"] = true;
            }

            return parameters.Count > 0 ? parameters : null;
        }

        return null;
    }

    public string? GetOptionSummary()
    {
        if (IsDefenderTask)
        {
            var parts = new List<string>();

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

            return parts.Count > 0 ? string.Join(", ", parts) : null;
        }

        if (IsSpoolerTask)
        {
            var parts = new List<string>();

            if (!IncludeServiceReset)
            {
                parts.Add("Skip service restart");
            }

            if (!IncludeQueuePurge)
            {
                parts.Add("Skip queue purge");
            }

            if (!IncludeDriverCleanup)
            {
                parts.Add("Skip driver cleanup");
            }

            if (!IncludeDllRegistration)
            {
                parts.Add("Skip DLL re-registration");
            }

            if (!IncludeIsolationPolicyReset)
            {
                parts.Add("Skip isolation reset");
            }

            return parts.Count > 0 ? string.Join(", ", parts) : null;
        }

        return null;
    }

    [RelayCommand]
    private void ToggleDetails()
    {
        if (string.IsNullOrWhiteSpace(DetailedDescription))
        {
            return;
        }

        IsDetailsVisible = !IsDetailsVisible;
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

    public string AttemptLabel { get; private set; } = string.Empty;

    public IReadOnlyList<string> DisplayLines
        => !Errors.IsDefaultOrEmpty && Errors.Length > 0 ? Errors : Output;

    public void Update(EssentialsQueueOperationSnapshot snapshot)
    {
        StatusLabel = snapshot.Status switch
        {
            EssentialsQueueStatus.Pending => "Queued",
            EssentialsQueueStatus.Running => "Running",
            EssentialsQueueStatus.Succeeded => "Completed",
            EssentialsQueueStatus.Failed => "Failed",
            EssentialsQueueStatus.Cancelled => "Cancelled",
            _ => snapshot.Status.ToString()
        };

        Message = snapshot.LastMessage;
        CompletedAt = snapshot.CompletedAt?.ToLocalTime();
        IsActive = snapshot.IsActive;
        HasErrors = !snapshot.Errors.IsDefaultOrEmpty && snapshot.Errors.Length > 0;
        Output = snapshot.Output;
        Errors = snapshot.Errors;
        AttemptLabel = snapshot.AttemptCount > 1 ? $"Attempt {snapshot.AttemptCount}" : string.Empty;
        OnPropertyChanged(nameof(AttemptLabel));

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
