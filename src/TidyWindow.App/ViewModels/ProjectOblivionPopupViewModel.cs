using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.Services;
using TidyWindow.Core.ProjectOblivion;

namespace TidyWindow.App.ViewModels;

public enum ProjectOblivionStageStatus
{
    Pending,
    Active,
    Completed,
    Failed
}

public enum ProjectOblivionArtifactRemovalState
{
    Pending,
    Removed,
    Failed
}

public enum ProjectOblivionLogLevel
{
    Info,
    Warning,
    Error
}

public sealed partial class ProjectOblivionTimelineStageViewModel : ObservableObject
{
    public ProjectOblivionTimelineStageViewModel(ProjectOblivionStage stage, string title, string description)
    {
        Stage = stage;
        Title = title;
        Description = description;
    }

    public ProjectOblivionStage Stage { get; }

    public string Title { get; }

    public string Description { get; }

    [ObservableProperty]
    private ProjectOblivionStageStatus _status = ProjectOblivionStageStatus.Pending;

    [ObservableProperty]
    private string _detail = string.Empty;
}

public sealed partial class ProjectOblivionArtifactViewModel : ObservableObject
{
    public ProjectOblivionArtifactViewModel(
        string artifactId,
        string group,
        string type,
        string displayName,
        string path,
        long sizeBytes,
        bool requiresElevation,
        bool defaultSelected,
        IReadOnlyDictionary<string, string> metadata)
    {
        ArtifactId = artifactId;
        Group = string.IsNullOrWhiteSpace(group) ? "Other" : group.Trim();
        Type = string.IsNullOrWhiteSpace(type) ? "Unknown" : type.Trim();
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? System.IO.Path.GetFileName(path) : displayName.Trim();
        FullPath = path;
        SizeBytes = Math.Max(0, sizeBytes);
        RequiresElevation = requiresElevation;
        DefaultSelected = defaultSelected;
        Metadata = metadata;
        _isSelected = defaultSelected;
    }

    public string ArtifactId { get; }

    public string Group { get; }

    public string Type { get; }

    public string DisplayName { get; }

    public string FullPath { get; }

    public long SizeBytes { get; }

    public bool RequiresElevation { get; }

    public bool DefaultSelected { get; }

    public IReadOnlyDictionary<string, string> Metadata { get; }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private ProjectOblivionArtifactRemovalState _removalState = ProjectOblivionArtifactRemovalState.Pending;

    [ObservableProperty]
    private string? _failureMessage;

    public event EventHandler? SelectionChanged;

    partial void OnIsSelectedChanged(bool value)
    {
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed partial class ProjectOblivionArtifactGroupViewModel : ObservableObject
{
    public ProjectOblivionArtifactGroupViewModel(string groupName)
    {
        GroupName = string.IsNullOrWhiteSpace(groupName) ? "Other" : groupName.Trim();
        Items = new ObservableCollection<ProjectOblivionArtifactViewModel>();
    }

    public string GroupName { get; }

    public ObservableCollection<ProjectOblivionArtifactViewModel> Items { get; }

    [ObservableProperty]
    private bool _isExpanded = true;

    public int SelectedCount => Items.Count(item => item.IsSelected);

    public long SelectedSizeBytes => Items.Where(item => item.IsSelected).Sum(item => item.SizeBytes);

    public bool AreAllSelected => Items.Count > 0 && Items.All(item => item.IsSelected);

    public void SetAllSelected(bool isSelected)
    {
        foreach (var item in Items)
        {
            item.IsSelected = isSelected;
        }

        NotifySelectionMetricsChanged();
    }

    public void NotifySelectionMetricsChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedSizeBytes));
        OnPropertyChanged(nameof(AreAllSelected));
    }
}

public sealed record ProjectOblivionRunLogEntry(DateTimeOffset Timestamp, ProjectOblivionLogLevel Level, string Message, string? Raw);

public sealed record ProjectOblivionRunSummaryViewModel(int Removed, int Skipped, int FailureCount, long FreedBytes, DateTimeOffset CompletedAt, string? LogPath)
{
    public string FreedDisplay => ProjectOblivionPopupViewModel.FormatSize(FreedBytes);
}

public enum ProjectOblivionStage
{
    Kickoff,
    DefaultUninstall,
    ProcessSweep,
    ArtifactDiscovery,
    SelectionHold,
    Cleanup,
    Summary
}

internal sealed record ProjectOblivionStageDefinition(ProjectOblivionStage Stage, string Title, string Description);

public sealed partial class ProjectOblivionPopupViewModel : ViewModelBase, IDisposable
{
    private static readonly IReadOnlyList<ProjectOblivionStageDefinition> StageDefinitions = new List<ProjectOblivionStageDefinition>
    {
        new(ProjectOblivionStage.Kickoff, "Kickoff", "Prepare the uninstall run context."),
        new(ProjectOblivionStage.DefaultUninstall, "Default uninstall", "Run the vendor-provided command."),
        new(ProjectOblivionStage.ProcessSweep, "Process sweep", "Close related processes and services."),
        new(ProjectOblivionStage.ArtifactDiscovery, "Artifact discovery", "Collect leftovers and registry keys."),
        new(ProjectOblivionStage.SelectionHold, "Review selection", "Choose which artifacts to remove."),
        new(ProjectOblivionStage.Cleanup, "Cleanup", "Remove the selected artifacts."),
        new(ProjectOblivionStage.Summary, "Summary", "Review results and freed space.")
    };

    private const string SelectionFilePrefix = "selection";

    private readonly ProjectOblivionRunService _runService;
    private readonly ActivityLogService _activityLog;
    private readonly MainViewModel _mainViewModel;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly Dictionary<ProjectOblivionStage, ProjectOblivionTimelineStageViewModel> _timelineLookup = new();
    private readonly Dictionary<string, ProjectOblivionArtifactViewModel> _artifactLookup = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _runCancellation;
    private ProjectOblivionApp? _targetApp;
    private string? _inventoryPath;
    private string? _selectionFilePath;
    private bool _selectionCommitted;

    public ProjectOblivionPopupViewModel(ProjectOblivionRunService runService, ActivityLogService activityLogService, MainViewModel mainViewModel)
    {
        _runService = runService ?? throw new ArgumentNullException(nameof(runService));
        _activityLog = activityLogService ?? throw new ArgumentNullException(nameof(activityLogService));
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));

        Timeline = new ObservableCollection<ProjectOblivionTimelineStageViewModel>();
        foreach (var definition in StageDefinitions)
        {
            var vm = new ProjectOblivionTimelineStageViewModel(definition.Stage, definition.Title, definition.Description);
            _timelineLookup[definition.Stage] = vm;
            Timeline.Add(vm);
        }

        ArtifactGroups = new ObservableCollection<ProjectOblivionArtifactGroupViewModel>();
        RunLog = new ObservableCollection<ProjectOblivionRunLogEntry>();
    }

    public ObservableCollection<ProjectOblivionTimelineStageViewModel> Timeline { get; }

    public ObservableCollection<ProjectOblivionArtifactGroupViewModel> ArtifactGroups { get; }

    public ObservableCollection<ProjectOblivionRunLogEntry> RunLog { get; }

    public ProjectOblivionApp? TargetApp => _targetApp;

    public string TargetAppDisplay => _targetApp?.Name ?? "Select an app";

    public string SelectedArtifactSizeDisplay => FormatSize(SelectedArtifactSizeBytes);

    public bool HasArtifacts => ArtifactGroups.Any(group => group.Items.Count > 0);

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isAwaitingSelection;

    [ObservableProperty]
    private bool _autoAdvanceSelection = true;

    [ObservableProperty]
    private bool _isDryRun;

    [ObservableProperty]
    private string _statusMessage = "Prepare to uninstall selected applications.";

    [ObservableProperty]
    private int _selectedArtifactCount;

    [ObservableProperty]
    private long _selectedArtifactSizeBytes;

    [ObservableProperty]
    private int _removedArtifactCount;

    [ObservableProperty]
    private int _failedArtifactCount;

    [ObservableProperty]
    private ProjectOblivionRunSummaryViewModel? _summary;

    [ObservableProperty]
    private bool _hasSummary;

    [ObservableProperty]
    private string? _runLogPath;

    partial void OnIsBusyChanged(bool value)
    {
        StartRunCommand.NotifyCanExecuteChanged();
        CancelRunCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsAwaitingSelectionChanged(bool value)
    {
        CommitSelectionCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedArtifactSizeBytesChanged(long value)
    {
        OnPropertyChanged(nameof(SelectedArtifactSizeDisplay));
    }

    partial void OnRunLogPathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasRunLog));
        ViewRunLogCommand.NotifyCanExecuteChanged();
    }

    public bool HasRunLog => !string.IsNullOrWhiteSpace(RunLogPath) && File.Exists(RunLogPath!);

    public void Prepare(ProjectOblivionApp app, string inventoryPath)
    {
        _targetApp = app ?? throw new ArgumentNullException(nameof(app));
        _inventoryPath = inventoryPath ?? throw new ArgumentNullException(nameof(inventoryPath));
        ResetState();
        StatusMessage = $"Ready to uninstall {app.Name}.";
        IsOpen = true;
        StartRunCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanStartRun))]
    private async Task StartRunAsync()
    {
        if (_targetApp is null || string.IsNullOrWhiteSpace(_inventoryPath))
        {
            StatusMessage = "Select an app and refresh inventory first.";
            return;
        }

        ResetRunVisuals();
        _runCancellation?.Dispose();
        _runCancellation = new CancellationTokenSource();
        _selectionCommitted = false;
        _selectionFilePath = BuildSelectionFilePath();
        DeleteSelectionFile();

        IsBusy = true;
        StatusMessage = "Starting uninstall run...";
        Log(ProjectOblivionLogLevel.Info, $"Starting Project Oblivion run for {_targetApp.Name}.");

        var request = new ProjectOblivionRunRequest(
            _targetApp.AppId,
            InventoryPath: _inventoryPath,
            SelectionPath: _selectionFilePath,
            AutoSelectAll: false,
            WaitForSelection: true,
            SelectionTimeoutSeconds: 900,
            DryRun: IsDryRun);

        try
        {
            await foreach (var runEvent in _runService.RunAsync(request, _runCancellation.Token))
            {
                await ProcessRunEventAsync(runEvent, _runCancellation.Token).ConfigureAwait(true);
            }

            StatusMessage = "Run completed.";
            _mainViewModel.SetStatusMessage($"Project Oblivion run complete for {_targetApp.Name}.");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Run cancelled.";
            Log(ProjectOblivionLogLevel.Warning, "Project Oblivion run cancelled by user.");
        }
        catch (Exception ex)
        {
            StatusMessage = string.IsNullOrWhiteSpace(ex.Message) ? "Run failed." : ex.Message;
            Log(ProjectOblivionLogLevel.Error, StatusMessage, ex.ToString());
            SetActiveStageFailed(StatusMessage);
            _mainViewModel.SetStatusMessage(StatusMessage);
        }
        finally
        {
            _runCancellation?.Dispose();
            _runCancellation = null;
            IsBusy = false;
            CancelRunCommand.NotifyCanExecuteChanged();
            StartRunCommand.NotifyCanExecuteChanged();
            CommitSelectionCommand.NotifyCanExecuteChanged();
            DeleteSelectionFile();
        }
    }

    private bool CanStartRun() => _targetApp is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancelRun))]
    private void CancelRun()
    {
        if (_runCancellation is null)
        {
            return;
        }

        try
        {
            _runCancellation.Cancel();
        }
        catch
        {
            // Ignore cancellation races.
        }
    }

    private bool CanCancelRun() => _runCancellation is not null && !IsAwaitingSelection;

    [RelayCommand(CanExecute = nameof(CanCommitSelection))]
    private async Task CommitSelectionAsync()
    {
        if (_runCancellation is null)
        {
            return;
        }

        await CommitSelectionInternalAsync(_runCancellation.Token).ConfigureAwait(true);
    }

    private bool CanCommitSelection() => IsAwaitingSelection && !_selectionCommitted;

    [RelayCommand]
    private void SelectAllArtifacts()
    {
        foreach (var group in ArtifactGroups)
        {
            group.SetAllSelected(true);
        }
        UpdateSelectionStats();
    }

    [RelayCommand]
    private void SelectNoArtifacts()
    {
        foreach (var group in ArtifactGroups)
        {
            group.SetAllSelected(false);
        }
        UpdateSelectionStats();
    }

    [RelayCommand]
    private void ClosePopup()
    {
        CancelRun();
        IsOpen = false;
        DeleteSelectionFile();
    }

    [RelayCommand(CanExecute = nameof(CanViewRunLog))]
    private void ViewRunLog()
    {
        if (string.IsNullOrWhiteSpace(RunLogPath))
        {
            return;
        }

        if (!File.Exists(RunLogPath))
        {
            _mainViewModel.SetStatusMessage("Run log is no longer available.");
            RunLogPath = null;
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = RunLogPath,
                UseShellExecute = true
            };

            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            _mainViewModel.SetStatusMessage("Opening the run log failed.");
            Log(ProjectOblivionLogLevel.Error, "Failed to open run log.", ex.ToString());
        }
    }

    private bool CanViewRunLog() => HasRunLog;

    public void Dispose()
    {
        _runCancellation?.Dispose();
    }

    private async Task ProcessRunEventAsync(ProjectOblivionRunEvent runEvent, CancellationToken token)
    {
        switch (runEvent.Type)
        {
            case "kickoff":
                SetStageCompleted(ProjectOblivionStage.Kickoff, "Context loaded.");
                Log(ProjectOblivionLogLevel.Info, "Kickoff complete.", runEvent.Raw);
                break;
            case "stage":
                await HandleStageEventAsync(runEvent.Payload).ConfigureAwait(true);
                break;
            case "artifacts":
                await HandleArtifactsEventAsync(runEvent.Payload, token).ConfigureAwait(true);
                break;
            case "selection":
                HandleSelectionEvent(runEvent.Payload);
                break;
            case "artifactResult":
                HandleArtifactResult(runEvent.Payload);
                break;
            case "summary":
                HandleSummaryEvent(runEvent.Payload);
                break;
            case "awaitingSelection":
                IsAwaitingSelection = true;
                StatusMessage = "Waiting for artifact selection...";
                Log(ProjectOblivionLogLevel.Info, "Awaiting artifact selection.", runEvent.Raw);
                break;
            default:
                Log(ProjectOblivionLogLevel.Info, $"Event: {runEvent.Type}", runEvent.Raw);
                break;
        }
    }

    private Task HandleStageEventAsync(JsonObject? payload)
    {
        var stageName = GetString(payload, "stage");
        var status = GetString(payload, "status");
        var stage = MapStage(stageName);
        if (!_timelineLookup.TryGetValue(stage, out var stageVm))
        {
            return Task.CompletedTask;
        }

        if (string.Equals(status, "started", StringComparison.OrdinalIgnoreCase))
        {
            stageVm.Status = ProjectOblivionStageStatus.Active;
            stageVm.Detail = string.Empty;
            StatusMessage = stageVm.Description;
            Log(ProjectOblivionLogLevel.Info, $"Stage started: {stageVm.Title}");
        }
        else if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            var exitCode = GetInt(payload, "exitCode");
            if (exitCode.HasValue && exitCode.Value != 0)
            {
                stageVm.Status = ProjectOblivionStageStatus.Failed;
                stageVm.Detail = $"Exit code {exitCode}";
                StatusMessage = $"{stageVm.Title} failed.";
                Log(ProjectOblivionLogLevel.Error, $"Stage failed: {stageVm.Title}", stageVm.Detail);
            }
            else
            {
                stageVm.Status = ProjectOblivionStageStatus.Completed;
                stageVm.Detail = BuildStageDetail(stage, payload);
                StatusMessage = $"{stageVm.Title} complete.";
                Log(ProjectOblivionLogLevel.Info, $"Stage complete: {stageVm.Title}", stageVm.Detail);

                if (stage == ProjectOblivionStage.Cleanup)
                {
                    IsAwaitingSelection = false;
                }
            }
        }

        return Task.CompletedTask;
    }

    private async Task HandleArtifactsEventAsync(JsonObject? payload, CancellationToken token)
    {
        var items = ExtractArtifacts(payload);
        ArtifactGroups.Clear();
        _artifactLookup.Clear();

        foreach (var group in items.GroupBy(item => item.Group, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var groupVm = new ProjectOblivionArtifactGroupViewModel(group.Key);
            foreach (var artifact in group.OrderByDescending(item => item.SizeBytes))
            {
                var metadata = artifact.Metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var artifactVm = new ProjectOblivionArtifactViewModel(
                    artifact.ArtifactId,
                    artifact.Group,
                    artifact.Type,
                    artifact.DisplayName,
                    artifact.FullPath,
                    artifact.SizeBytes,
                    artifact.RequiresElevation,
                    artifact.DefaultSelected,
                    metadata);
                artifactVm.SelectionChanged += (s, _) =>
                {
                    groupVm.NotifySelectionMetricsChanged();
                    OnArtifactSelectionChanged(s, EventArgs.Empty);
                };
                groupVm.Items.Add(artifactVm);
                _artifactLookup[artifact.ArtifactId] = artifactVm;
            }
            ArtifactGroups.Add(groupVm);
        }

        UpdateSelectionStats();
        StatusMessage = "Artifacts ready for review.";
        SetStageCompleted(ProjectOblivionStage.ArtifactDiscovery, $"Found {items.Count} artifact(s).");
        SetStageActive(ProjectOblivionStage.SelectionHold);
        IsAwaitingSelection = true;
        _selectionCommitted = false;

        if (AutoAdvanceSelection)
        {
            await CommitSelectionInternalAsync(token).ConfigureAwait(true);
        }
    }

    private void HandleSelectionEvent(JsonObject? payload)
    {
        var selected = GetInt(payload, "selected") ?? 0;
        var total = GetInt(payload, "total") ?? selected;
        var detail = total == 0 ? "No artifacts available." : $"Selected {selected} of {total}.";
        SetStageCompleted(ProjectOblivionStage.SelectionHold, detail);
        StatusMessage = detail;
        Log(ProjectOblivionLogLevel.Info, detail);
    }

    private void HandleArtifactResult(JsonObject? payload)
    {
        var artifactId = GetString(payload, "artifactId");
        if (string.IsNullOrWhiteSpace(artifactId) || !_artifactLookup.TryGetValue(artifactId, out var artifact))
        {
            return;
        }

        var success = GetBool(payload, "success") ?? false;
        var error = GetString(payload, "error");
        var previousState = artifact.RemovalState;

        if (success)
        {
            artifact.RemovalState = ProjectOblivionArtifactRemovalState.Removed;
            artifact.FailureMessage = null;
            if (previousState != ProjectOblivionArtifactRemovalState.Removed)
            {
                RemovedArtifactCount++;
                if (previousState == ProjectOblivionArtifactRemovalState.Failed && FailedArtifactCount > 0)
                {
                    FailedArtifactCount--;
                }
            }
        }
        else
        {
            artifact.RemovalState = ProjectOblivionArtifactRemovalState.Failed;
            artifact.FailureMessage = error;
            if (previousState != ProjectOblivionArtifactRemovalState.Failed)
            {
                FailedArtifactCount++;
                if (previousState == ProjectOblivionArtifactRemovalState.Removed && RemovedArtifactCount > 0)
                {
                    RemovedArtifactCount--;
                }
            }
        }
    }

    private void HandleSummaryEvent(JsonObject? payload)
    {
        var removed = GetInt(payload, "removed") ?? RemovedArtifactCount;
        var skipped = GetInt(payload, "skipped") ?? 0;
        var failures = GetInt(payload, "failures") ?? FailedArtifactCount;
        var freed = GetLong(payload, "freedBytes") ?? 0;
        var timestamp = GetDateTimeOffset(payload, "timestamp") ?? DateTimeOffset.UtcNow;
        var logPath = NormalizeLogPath(GetString(payload, "logPath"));
        RunLogPath = logPath;
        Summary = new ProjectOblivionRunSummaryViewModel(removed, skipped, failures, freed, timestamp, RunLogPath);
        HasSummary = true;
        SetStageCompleted(ProjectOblivionStage.Summary, $"Removed {removed}, skipped {skipped}.");
        Log(ProjectOblivionLogLevel.Info, $"Summary ready • Removed {removed}, skipped {skipped}, failures {failures}.", payload?.ToJsonString());
        PersistTelemetry(payload);

        var toast = failures > 0
            ? $"Project Oblivion completed with {failures} failure(s)."
            : $"Project Oblivion removed {removed} artifact(s).";
        _mainViewModel.SetStatusMessage(toast);
        StatusMessage = toast;
    }

    private async Task CommitSelectionInternalAsync(CancellationToken token)
    {
        if (_selectionFilePath is null)
        {
            return;
        }

        var selectedIds = ArtifactGroups.SelectMany(group => group.Items).Where(item => item.IsSelected).Select(item => item.ArtifactId).ToList();
        var payload = new Dictionary<string, object>
        {
            ["selectedIds"] = selectedIds
        };

        var directory = Path.GetDirectoryName(_selectionFilePath);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(_selectionFilePath, JsonSerializer.Serialize(payload, _jsonOptions), Encoding.UTF8, token).ConfigureAwait(false);
        _selectionCommitted = true;
        StatusMessage = "Selection saved. Continuing cleanup...";
        Log(ProjectOblivionLogLevel.Info, $"Committed {selectedIds.Count} artifact(s).", _selectionFilePath);
        CommitSelectionCommand.NotifyCanExecuteChanged();
    }

    private void OnArtifactSelectionChanged(object? sender, EventArgs e)
    {
        UpdateSelectionStats();
        _selectionCommitted = false;
        CommitSelectionCommand.NotifyCanExecuteChanged();
    }

    private void UpdateSelectionStats()
    {
        SelectedArtifactCount = ArtifactGroups.Sum(group => group.SelectedCount);
        SelectedArtifactSizeBytes = ArtifactGroups.Sum(group => group.SelectedSizeBytes);
        OnPropertyChanged(nameof(HasArtifacts));
    }

    private void ResetState()
    {
        ResetRunVisuals();
        ArtifactGroups.Clear();
        _artifactLookup.Clear();
        RunLog.Clear();
        Summary = null;
        HasSummary = false;
        RunLogPath = null;
        SelectedArtifactCount = 0;
        SelectedArtifactSizeBytes = 0;
        RemovedArtifactCount = 0;
        FailedArtifactCount = 0;
        foreach (var stageVm in Timeline)
        {
            stageVm.Status = ProjectOblivionStageStatus.Pending;
            stageVm.Detail = string.Empty;
        }
    }

    private void ResetRunVisuals()
    {
        IsAwaitingSelection = false;
        StatusMessage = _targetApp is null ? "Select an app to begin." : $"Ready to uninstall {_targetApp.Name}.";
        _selectionCommitted = false;
        HasSummary = false;
    }

    private void SetStageCompleted(ProjectOblivionStage stage, string detail)
    {
        if (_timelineLookup.TryGetValue(stage, out var vm))
        {
            vm.Status = ProjectOblivionStageStatus.Completed;
            vm.Detail = detail;
        }
    }

    private void SetStageActive(ProjectOblivionStage stage)
    {
        if (_timelineLookup.TryGetValue(stage, out var vm))
        {
            vm.Status = ProjectOblivionStageStatus.Active;
            vm.Detail = vm.Description;
        }
    }

    private void SetActiveStageFailed(string message)
    {
        var active = Timeline.FirstOrDefault(stage => stage.Status == ProjectOblivionStageStatus.Active);
        if (active is null)
        {
            return;
        }

        active.Status = ProjectOblivionStageStatus.Failed;
        active.Detail = message;
    }

    private static ProjectOblivionStage MapStage(string? stage)
    {
        return stage?.Trim() switch
        {
            "DefaultUninstall" => ProjectOblivionStage.DefaultUninstall,
            "ProcessSweep" => ProjectOblivionStage.ProcessSweep,
            "ArtifactDiscovery" => ProjectOblivionStage.ArtifactDiscovery,
            "Cleanup" => ProjectOblivionStage.Cleanup,
            _ => ProjectOblivionStage.Kickoff
        };
    }

    private static string BuildStageDetail(ProjectOblivionStage stage, JsonObject? payload)
    {
        return stage switch
        {
            ProjectOblivionStage.ProcessSweep => BuildProcessSweepDetail(payload),
            ProjectOblivionStage.ArtifactDiscovery => BuildArtifactDiscoveryDetail(payload),
            ProjectOblivionStage.Cleanup => BuildCleanupDetail(payload),
            _ => "Completed"
        };
    }

    private static string BuildProcessSweepDetail(JsonObject? payload)
    {
        var detected = GetInt(payload, "detected") ?? 0;
        var stopped = GetInt(payload, "stopped") ?? 0;
        return detected == 0 ? "No active processes." : $"Stopped {stopped} of {detected} processes.";
    }

    private static string BuildArtifactDiscoveryDetail(JsonObject? payload)
    {
        var count = GetInt(payload, "count") ?? 0;
        return count == 0 ? "No artifacts detected." : $"Found {count} artifact(s).";
    }

    private static string BuildCleanupDetail(JsonObject? payload)
    {
        var removed = GetInt(payload, "removed") ?? 0;
        var failures = GetInt(payload, "failures") ?? 0;
        if (removed == 0 && failures == 0)
        {
            return "No artifacts removed.";
        }

        return failures == 0
            ? $"Removed {removed} artifact(s)."
            : $"Removed {removed} artifact(s), {failures} failed.";
    }

    private static List<ProjectOblivionArtifactModel> ExtractArtifacts(JsonObject? payload)
    {
        var results = new List<ProjectOblivionArtifactModel>();
        if (payload is null || !payload.TryGetPropertyValue("items", out var itemsNode))
        {
            return results;
        }

        if (itemsNode is not JsonArray array)
        {
            return results;
        }

        foreach (var item in array.OfType<JsonObject>())
        {
            var id = GetString(item, "id");
            var path = GetString(item, "path");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var group = GetString(item, "group") ?? "Other";
            var type = GetString(item, "type") ?? "Unknown";
            var display = GetString(item, "displayName") ?? System.IO.Path.GetFileName(path);
            var size = GetLong(item, "sizeBytes") ?? 0;
            var requiresElevation = GetBool(item, "requiresElevation") ?? false;
            var defaultSelected = GetBool(item, "defaultSelected") ?? true;
            var metadata = ExtractMetadata(item);

            var resolvedDisplay = string.IsNullOrWhiteSpace(display) ? path : display;
            results.Add(new ProjectOblivionArtifactModel(id, group, type, resolvedDisplay, path, size, requiresElevation, defaultSelected, metadata));
        }

        return results;
    }

    private static IReadOnlyDictionary<string, string> ExtractMetadata(JsonObject item)
    {
        if (!item.TryGetPropertyValue("metadata", out var metadataNode) || metadataNode is not JsonObject metadataObject)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in metadataObject)
        {
            if (property.Value is null)
            {
                continue;
            }

            var value = property.Value.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                dict[property.Key] = value;
            }
        }

        return dict;
    }

    private static string? GetString(JsonObject? obj, string property)
    {
        if (obj is null || !obj.TryGetPropertyValue(property, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        return node.ToString();
    }

    private static int? GetInt(JsonObject? obj, string property)
    {
        if (obj is null || !obj.TryGetPropertyValue(property, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var intValue))
            {
                return intValue;
            }

            if (value.TryGetValue<long>(out var longValue))
            {
                return (int)longValue;
            }

            if (value.TryGetValue<double>(out var doubleValue))
            {
                return (int)Math.Round(doubleValue);
            }
        }

        return int.TryParse(node.ToString(), out var parsed) ? parsed : (int?)null;
    }

    private static long? GetLong(JsonObject? obj, string property)
    {
        if (obj is null || !obj.TryGetPropertyValue(property, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out var longValue))
            {
                return longValue;
            }

            if (value.TryGetValue<double>(out var doubleValue))
            {
                return (long)Math.Round(doubleValue);
            }

            if (value.TryGetValue<int>(out var intValue))
            {
                return intValue;
            }
        }

        return long.TryParse(node.ToString(), out var parsed) ? parsed : (long?)null;
    }

    private static bool? GetBool(JsonObject? obj, string property)
    {
        if (obj is null || !obj.TryGetPropertyValue(property, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue<bool>(out var boolValue))
        {
            return boolValue;
        }

        return bool.TryParse(node.ToString(), out var parsed) ? parsed : (bool?)null;
    }

    private static DateTimeOffset? GetDateTimeOffset(JsonObject? obj, string property)
    {
        var text = GetString(obj, property);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return DateTimeOffset.TryParse(text, out var dto) ? dto : null;
    }

    private static string? NormalizeLogPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path);
            if (File.Exists(expanded))
            {
                return expanded;
            }
        }
        catch
        {
            // Ignore expansion failures.
        }

        return File.Exists(path) ? path : null;
    }

    private static string BuildSelectionFilePath()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWindow", "ProjectOblivion");
        Directory.CreateDirectory(root);
        return Path.Combine(root, $"{SelectionFilePrefix}-{Guid.NewGuid():N}.json");
    }

    private void DeleteSelectionFile()
    {
        if (string.IsNullOrWhiteSpace(_selectionFilePath))
        {
            return;
        }

        try
        {
            if (File.Exists(_selectionFilePath))
            {
                File.Delete(_selectionFilePath);
            }
        }
        catch
        {
            // Ignore cleanup failures.
        }
    }

    private void Log(ProjectOblivionLogLevel level, string message, string? raw = null)
    {
        var entry = new ProjectOblivionRunLogEntry(DateTimeOffset.Now, level, message, raw);
        RunLog.Add(entry);
        if (RunLog.Count > 500)
        {
            RunLog.RemoveAt(0);
        }
    }

    private void PersistTelemetry(JsonObject? payload)
    {
        if (payload is null || _targetApp is null)
        {
            return;
        }

        try
        {
            var baseDirectory = AppContext.BaseDirectory;
            if (string.IsNullOrWhiteSpace(baseDirectory))
            {
                baseDirectory = Environment.CurrentDirectory;
            }

            var appDirectory = SanitizeForPath(_targetApp.AppId);
            var cleanupRoot = Path.Combine(baseDirectory, "data", "cleanup", appDirectory);
            Directory.CreateDirectory(cleanupRoot);

            var telemetry = new JsonObject
            {
                ["appId"] = _targetApp.AppId,
                ["generatedAt"] = DateTimeOffset.UtcNow.ToString("o"),
                ["summary"] = payload.DeepClone()
            };

            if (RunLog.Count > 0)
            {
                var eventsArray = new JsonArray();
                foreach (var entry in RunLog)
                {
                    var node = new JsonObject
                    {
                        ["timestamp"] = entry.Timestamp.ToString("o"),
                        ["level"] = entry.Level.ToString(),
                        ["message"] = entry.Message
                    };

                    if (!string.IsNullOrWhiteSpace(entry.Raw))
                    {
                        node["raw"] = entry.Raw;
                    }

                    eventsArray.Add(node);
                }

                telemetry["events"] = eventsArray;
            }

            var outputPath = Path.Combine(cleanupRoot, "oblivion-run.json");
            File.WriteAllText(outputPath, telemetry.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log(ProjectOblivionLogLevel.Warning, "Unable to persist run telemetry.", ex.ToString());
        }
    }

    private static string SanitizeForPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "app";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        return builder.ToString();
    }

    internal static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 MB";
        }

        var megabytes = bytes / 1_048_576d;
        if (megabytes >= 1024)
        {
            return $"{megabytes / 1024d:0.##} GB";
        }

        return $"{megabytes:0.##} MB";
    }

    private sealed record ProjectOblivionArtifactModel(
        string ArtifactId,
        string Group,
        string Type,
        string DisplayName,
        string FullPath,
        long SizeBytes,
        bool RequiresElevation,
        bool DefaultSelected,
        IReadOnlyDictionary<string, string> Metadata);
}
