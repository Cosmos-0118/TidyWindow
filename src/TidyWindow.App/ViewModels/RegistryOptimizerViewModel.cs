using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.Services;
using TidyWindow.App.Resources.Strings;
using TidyWindow.Core.Maintenance;

namespace TidyWindow.App.ViewModels;

public sealed partial class RegistryOptimizerViewModel : ViewModelBase
{
    private readonly ActivityLogService _activityLog;
    private readonly MainViewModel _mainViewModel;
    private readonly IRegistryOptimizerService _registryService;
    private readonly IRegistryStateService _registryStateService;
    private readonly RegistryStateWatcher _registryStateWatcher;
    private readonly RegistryPreferenceService _registryPreferenceService;
    private readonly SynchronizationContext? _uiContext;
    private CancellationTokenSource? _stateRefreshCts;
    private bool _isInitialized;
    private bool _isRefreshing;

    public event EventHandler<RegistryRestorePointCreatedEventArgs>? RestorePointCreated;

    public RegistryOptimizerViewModel(
        ActivityLogService activityLogService,
        MainViewModel mainViewModel,
    IRegistryOptimizerService registryService,
    IRegistryStateService registryStateService,
        RegistryStateWatcher registryStateWatcher,
        RegistryPreferenceService registryPreferenceService)
    {
        _activityLog = activityLogService ?? throw new ArgumentNullException(nameof(activityLogService));
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        _registryService = registryService ?? throw new ArgumentNullException(nameof(registryService));
        _registryStateService = registryStateService ?? throw new ArgumentNullException(nameof(registryStateService));
        _registryStateWatcher = registryStateWatcher ?? throw new ArgumentNullException(nameof(registryStateWatcher));
        _registryPreferenceService = registryPreferenceService ?? throw new ArgumentNullException(nameof(registryPreferenceService));
        _uiContext = SynchronizationContext.Current;

        Tweaks = new ObservableCollection<RegistryTweakCardViewModel>();
        Presets = new ObservableCollection<RegistryPresetViewModel>();

        PopulateFromConfiguration();

        SelectedPreset = Presets.FirstOrDefault(preset => preset.IsDefault) ?? Presets.FirstOrDefault();
        foreach (var tweak in Tweaks)
        {
            tweak.CommitSelection();
        }

        UpdatePendingChanges();
        UpdateRestorePointState(_registryService.TryGetLatestRestorePoint());
        UpdateValidationState();
        StartStateInitialization();
        _isInitialized = true;
    }

    public ObservableCollection<RegistryTweakCardViewModel> Tweaks { get; }

    public ObservableCollection<RegistryPresetViewModel> Presets { get; }

    [ObservableProperty]
    private RegistryPresetViewModel? _selectedPreset;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasPendingChanges;

    [ObservableProperty]
    private bool _hasValidationErrors;

    [ObservableProperty]
    private bool _isPresetCustomized;

    [ObservableProperty]
    private string _headline = RegistryOptimizerStrings.PageHeadline;

    [ObservableProperty]
    private string? _lastOperationSummary;

    [ObservableProperty]
    private RegistryRestorePoint? _latestRestorePoint;

    [ObservableProperty]
    private bool _hasRestorePoint;

    partial void OnSelectedPresetChanged(RegistryPresetViewModel? oldValue, RegistryPresetViewModel? newValue)
    {
        if (newValue is null)
        {
            UpdatePresetCustomizationState();
            return;
        }

        ApplyPreset(newValue);
        if (_isInitialized)
        {
            _mainViewModel.SetStatusMessage($"Preset '{newValue.Name}' loaded.");
        }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (!HasPendingChanges)
        {
            return;
        }

        if (HasValidationErrors)
        {
            const string validationMessage = "Resolve invalid registry custom values before applying.";
            LastOperationSummary = validationMessage;
            _activityLog.LogWarning("Registry", validationMessage);
            _mainViewModel.SetStatusMessage("Fix invalid custom values before applying.");
            return;
        }

        var pendingTweaks = Tweaks.Where(t => t.HasPendingChanges).ToList();
        if (pendingTweaks.Count == 0)
        {
            UpdatePendingChanges();
            UpdateValidationState();
            return;
        }

        var selections = pendingTweaks
            .Select(tweak => new RegistrySelection(
                tweak.Id,
                tweak.IsSelected,
                tweak.IsBaselineEnabled,
                tweak.GetTargetParameterOverrides(),
                tweak.GetBaselineParameterOverrides()))
            .ToImmutableArray();

        var plan = _registryService.BuildPlan(selections);
        if (!plan.HasWork)
        {
            foreach (var tweak in pendingTweaks)
            {
                tweak.CommitSelection();
            }

            UpdatePendingChanges();
            UpdateValidationState();
            LastOperationSummary = $"No registry scripts required ({DateTime.Now:t}).";
            _activityLog.LogInformation("Registry", LastOperationSummary);
            _mainViewModel.SetStatusMessage("Registry tweaks already in desired state.");
            return;
        }

        IsBusy = true;
        var refreshAfterApply = false;
        try
        {
            var result = await _registryService.ApplyAsync(plan);

            if (!result.IsSuccess)
            {
                var errors = result.AggregateErrors();
                var errorSummary = $"Encountered {result.FailedCount} issue(s) while applying registry tweaks.";
                LastOperationSummary = errorSummary;
                _activityLog.LogError("Registry", errorSummary, errors);
                _mainViewModel.SetStatusMessage("Registry tweaks completed with warnings.");
                return;
            }

            foreach (var tweak in pendingTweaks)
            {
                tweak.CommitSelection();
                _registryStateService.Invalidate(tweak.Id);
            }

            UpdatePendingChanges();
            UpdateValidationState();
            var appliedCount = pendingTweaks.Count;
            var summary = $"Applied {appliedCount} registry tweak(s) at {DateTime.Now:t}.";
            LastOperationSummary = summary;
            _activityLog.LogSuccess("Registry", summary, result.Executions.SelectMany(exec => exec.Output));
            _mainViewModel.SetStatusMessage("Registry tweaks applied.");
            refreshAfterApply = true;

            try
            {
                var restorePoint = await _registryService.SaveRestorePointAsync(selections, plan);
                if (restorePoint is not null)
                {
                    UpdateRestorePointState(restorePoint);
                    var message = string.Format(CultureInfo.CurrentCulture, RegistryOptimizerStrings.RestorePointCreated, restorePoint.FilePath);
                    _activityLog.LogInformation("Registry", message);
                    OnRestorePointCreated(restorePoint);
                }
            }
            catch (Exception ex)
            {
                _activityLog.LogWarning("Registry", $"Unable to save registry restore point: {ex.Message}");
            }
        }
        finally
        {
            IsBusy = false;
            if (refreshAfterApply)
            {
                StartStateInitialization(triggeredByUser: true);
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanRevertChanges))]
    private void RevertChanges()
    {
        foreach (var tweak in Tweaks)
        {
            tweak.RevertToBaseline();
        }

        UpdatePendingChanges();
        UpdateValidationState();
        LastOperationSummary = $"Selections reverted at {DateTime.Now:t}.";
        _activityLog.LogInformation("Registry", "Selections reverted to last applied values.");
        _mainViewModel.SetStatusMessage("Registry selections reset.");
    }

    [RelayCommand(CanExecute = nameof(CanRefreshState))]
    private void RefreshState()
    {
        StartStateInitialization(triggeredByUser: true);
    }

    private bool CanApply() => HasPendingChanges && !IsBusy && !HasValidationErrors;

    private bool CanRevertChanges() => HasPendingChanges && !IsBusy;

    private bool CanRefreshState() => !IsBusy && !_isRefreshing;

    partial void OnIsBusyChanged(bool oldValue, bool newValue)
    {
        ApplyCommand.NotifyCanExecuteChanged();
        RevertChangesCommand.NotifyCanExecuteChanged();
        RestoreLastSnapshotCommand.NotifyCanExecuteChanged();
        RefreshStateCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasPendingChangesChanged(bool oldValue, bool newValue)
    {
        ApplyCommand.NotifyCanExecuteChanged();
        RevertChangesCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasValidationErrorsChanged(bool oldValue, bool newValue)
    {
        ApplyCommand.NotifyCanExecuteChanged();
    }

    partial void OnLatestRestorePointChanged(RegistryRestorePoint? oldValue, RegistryRestorePoint? newValue)
    {
        HasRestorePoint = newValue is not null;
    }

    partial void OnHasRestorePointChanged(bool oldValue, bool newValue)
    {
        RestoreLastSnapshotCommand.NotifyCanExecuteChanged();
    }

    private void PopulateFromConfiguration()
    {
        foreach (var tweakDefinition in _registryService.Tweaks)
        {
            var localizedName = RegistryOptimizerStrings.GetTweakName(tweakDefinition.Id, tweakDefinition.Name);
            var localizedSummary = RegistryOptimizerStrings.GetTweakSummary(tweakDefinition.Id, tweakDefinition.Summary);
            var localizedRisk = RegistryOptimizerStrings.GetTweakRisk(tweakDefinition.Id, tweakDefinition.RiskLevel);

            var tweak = new RegistryTweakCardViewModel(
                tweakDefinition,
                localizedName,
                localizedSummary,
                localizedRisk,
                _registryPreferenceService);

            tweak.PropertyChanged += OnTweakPropertyChanged;
            Tweaks.Add(tweak);
        }

        foreach (var presetDefinition in _registryService.Presets)
        {
            Presets.Add(new RegistryPresetViewModel(presetDefinition));
        }
    }

    private void ApplyPreset(RegistryPresetViewModel preset)
    {
        foreach (var tweak in Tweaks)
        {
            if (preset.TryGetState(tweak.Id, out var state))
            {
                tweak.SetSelection(state);
            }
        }

        UpdatePendingChanges();
        UpdateValidationState();
    }

    private void UpdatePendingChanges()
    {
        var pending = Tweaks.Any(tweak => tweak.HasPendingChanges);
        if (HasPendingChanges != pending)
        {
            HasPendingChanges = pending;
        }

        UpdatePresetCustomizationState();
    }

    private void UpdatePresetCustomizationState()
    {
        if (SelectedPreset is null)
        {
            IsPresetCustomized = Tweaks.Any(tweak => tweak.HasPendingChanges);
            return;
        }

        var isExactMatch = Tweaks.All(tweak =>
        {
            if (!SelectedPreset.TryGetState(tweak.Id, out var presetValue))
            {
                return !tweak.HasPendingChanges;
            }

            return tweak.IsSelected == presetValue;
        });

        IsPresetCustomized = !isExactMatch;
    }

    private void OnTweakPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdatePendingChanges();
        UpdateValidationState();
    }

    private void UpdateValidationState()
    {
        var hasErrors = Tweaks.Any(tweak => tweak.HasValidationError);
        if (HasValidationErrors != hasErrors)
        {
            HasValidationErrors = hasErrors;
        }
    }

    private void StartStateInitialization(bool triggeredByUser = false)
    {
        CancelStateRefresh();

        if (triggeredByUser)
        {
            _isRefreshing = true;
            _mainViewModel.SetStatusMessage("Refreshing registry values...");
            RefreshStateCommand.NotifyCanExecuteChanged();
        }

        if (Tweaks.Count == 0)
        {
            if (triggeredByUser)
            {
                _isRefreshing = false;
                RefreshStateCommand.NotifyCanExecuteChanged();
                _mainViewModel.SetStatusMessage("No registry tweaks to refresh.");
                LastOperationSummary = $"Registry values refreshed at {DateTime.Now:t}.";
            }

            _mainViewModel.CompleteShellLoad();
            return;
        }

        foreach (var tweak in Tweaks)
        {
            tweak.BeginStateRefresh();
        }

        _mainViewModel.BeginShellLoad();

        var requireFreshProbe = triggeredByUser || !_isInitialized;
        var tweakIds = Tweaks.Select(t => t.Id).ToArray();

        var cts = new CancellationTokenSource();
        _stateRefreshCts = cts;
        var token = cts.Token;

        _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var update in _registryStateWatcher.WatchAsync(tweakIds, requireFreshProbe, token).ConfigureAwait(false))
                    {
                        if (token.IsCancellationRequested)
                        {
                            break;
                        }

                        PostToUi(() =>
                        {
                            var tweak = Tweaks.FirstOrDefault(t => string.Equals(t.Id, update.TweakId, StringComparison.OrdinalIgnoreCase));
                            if (tweak is null)
                            {
                                return;
                            }

                            if (update.IsSuccess && update.State is not null)
                            {
                                tweak.UpdateState(update.State);

                                if (update.State.Errors.Length > 0)
                                {
                                    var summary = string.Join("; ", update.State.Errors.Where(static line => !string.IsNullOrWhiteSpace(line)).Take(3));
                                    if (!string.IsNullOrWhiteSpace(summary))
                                    {
                                        _activityLog.LogWarning("Registry", $"Detection issues for '{tweak.Title}': {summary}");
                                    }
                                }
                            }
                            else
                            {
                                var message = string.IsNullOrWhiteSpace(update.ErrorMessage)
                                    ? "Registry detection failed."
                                    : update.ErrorMessage!
                                        .Trim();

                                tweak.ApplyStateFailure(message);
                                _activityLog.LogWarning("Registry", $"Failed to load registry state for '{tweak.Title}': {message}");
                            }

                            UpdatePendingChanges();
                            UpdateValidationState();
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    // ignored
                }
                catch (Exception ex)
                {
                    PostToUi(() => _activityLog.LogWarning("Registry", $"Registry state refresh failed: {ex.Message}"));
                }
                finally
                {
                    PostToUi(() =>
                    {
                        var isCurrent = ReferenceEquals(_stateRefreshCts, cts);

                        if (isCurrent)
                        {
                            foreach (var tweak in Tweaks)
                            {
                                tweak.CompleteStateRefresh();
                            }
                        }

                        if (triggeredByUser && isCurrent)
                        {
                            _isRefreshing = false;
                            LastOperationSummary = $"Registry values refreshed at {DateTime.Now:t}.";
                            _mainViewModel.SetStatusMessage("Registry values refreshed.");
                            RefreshStateCommand.NotifyCanExecuteChanged();
                        }

                        if (isCurrent)
                        {
                            _mainViewModel.CompleteShellLoad();
                        }

                        if (Interlocked.CompareExchange(ref _stateRefreshCts, null, cts) == cts)
                        {
                            cts.Dispose();
                        }
                        else
                        {
                            cts.Dispose();
                        }
                    });
                }
            });
    }

    private void CancelStateRefresh()
    {
        var existing = Interlocked.Exchange(ref _stateRefreshCts, null);
        if (existing is null)
        {
            return;
        }

        try
        {
            existing.Cancel();
        }
        catch
        {
            // ignored
        }
        finally
        {
            existing.Dispose();
        }
    }

    private void PostToUi(Action action)
    {
        if (action is null)
        {
            return;
        }

        if (_uiContext is not null)
        {
            _uiContext.Post(_ => action(), null);
        }
        else
        {
            action();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRestoreSnapshot))]
    private async Task RestoreLastSnapshotAsync()
    {
        var restorePoint = LatestRestorePoint;
        if (restorePoint is null)
        {
            return;
        }

        await RevertRestorePointAsync(restorePoint, false);
    }

    private bool CanRestoreSnapshot() => HasRestorePoint && !IsBusy;

    public async Task<bool> RevertRestorePointAsync(RegistryRestorePoint restorePoint, bool triggeredByTimeout)
    {
        if (restorePoint is null)
        {
            throw new ArgumentNullException(nameof(restorePoint));
        }

        if (IsBusy)
        {
            return false;
        }

        IsBusy = true;
        try
        {
            var result = await _registryService.ApplyRestorePointAsync(restorePoint);
            if (!result.IsSuccess)
            {
                var errors = result.AggregateErrors();
                var message = string.Format(CultureInfo.CurrentCulture, "Failed to apply registry restore point '{0}'.", restorePoint.Id);
                _activityLog.LogError("Registry", message, errors);
                _mainViewModel.SetStatusMessage("Registry restore point failed.");
                LastOperationSummary = RegistryOptimizerStrings.RestorePointFailed;
                return false;
            }

            foreach (var selection in restorePoint.Selections)
            {
                var tweak = Tweaks.FirstOrDefault(t => string.Equals(t.Id, selection.TweakId, StringComparison.OrdinalIgnoreCase));
                if (tweak is null)
                {
                    continue;
                }

                tweak.SetSelection(selection.PreviousState);
                tweak.CommitSelection();
            }

            UpdatePendingChanges();
            var summary = triggeredByTimeout
                ? $"Restore point auto-reverted at {DateTime.Now:t}."
                : $"Restore point applied at {DateTime.Now:t}.";

            LastOperationSummary = summary;
            _activityLog.LogSuccess("Registry", RegistryOptimizerStrings.RestorePointApplied, result.Executions.SelectMany(exec => exec.Output));
            _mainViewModel.SetStatusMessage("Registry restore point applied.");

            _registryService.DeleteRestorePoint(restorePoint);
            UpdateRestorePointState(_registryService.TryGetLatestRestorePoint());
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateRestorePointState(RegistryRestorePoint? restorePoint)
    {
        LatestRestorePoint = restorePoint;
    }

    private void OnRestorePointCreated(RegistryRestorePoint restorePoint)
    {
        RestorePointCreated?.Invoke(this, new RegistryRestorePointCreatedEventArgs(restorePoint));
    }
}

public sealed partial class RegistryTweakCardViewModel : ObservableObject
{
    private readonly RegistryTweakDefinition _definition;
    private readonly RegistryPreferenceService _preferences;
    private bool _baselineState;
    private string? _baselineCustomValueRaw;
    private object? _baselineCustomValuePayload;
    private object? _customValuePayload;
    private string? _customParameterName;
    private bool _customValueInitialized;
    private string? _pendingPersistedCustomValue;
    private DateTimeOffset _lastObservedAt;
    private readonly ObservableCollection<string> _currentValueLines;
    private readonly ReadOnlyObservableCollection<string> _currentValueLinesView;
    private readonly ObservableCollection<RegistrySnapshotDisplay> _snapshotEntries;
    private readonly ReadOnlyObservableCollection<RegistrySnapshotDisplay> _snapshotEntriesView;

    public RegistryTweakCardViewModel(RegistryTweakDefinition definition, string title, string summary, string riskLabel, RegistryPreferenceService preferences)
    {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));

        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(title));
        }

        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(summary));
        }

        Id = _definition.Id;
        Title = title;
        Summary = summary;
        RiskLevel = _definition.RiskLevel;
        RiskLabel = string.IsNullOrWhiteSpace(riskLabel)
            ? RegistryOptimizerStrings.GetTweakRisk(_definition.Id, RiskLevel)
            : riskLabel;

        _baselineState = _definition.DefaultState;
        _isSelected = _definition.DefaultState;
        _pendingPersistedCustomValue = _preferences.GetCustomValue(_definition.Id);

        _currentValueLines = new ObservableCollection<string>();
        _currentValueLinesView = new ReadOnlyObservableCollection<string>(_currentValueLines);
        _snapshotEntries = new ObservableCollection<RegistrySnapshotDisplay>();
        _snapshotEntriesView = new ReadOnlyObservableCollection<RegistrySnapshotDisplay>(_snapshotEntries);
        _isStatePending = true;
        _currentValueLines.Add(RegistryOptimizerStrings.ValueNotAvailable);
        CurrentValue = RegistryOptimizerStrings.ValueNotAvailable;
        RecommendedValue = RegistryOptimizerStrings.ValueRecommendationUnavailable;
    }

    public string Id { get; }

    public string Title { get; }

    public string Summary { get; }

    public string RiskLevel { get; }

    public string RiskLabel { get; }

    public string Icon => string.IsNullOrWhiteSpace(_definition.Icon) ? "🧰" : _definition.Icon;

    public string Category => _definition.Category;

    public string? DocumentationLink => _definition.DocumentationLink;

    public RegistryTweakConstraints? Constraints => _definition.Constraints;

    public bool HasPendingChanges => (IsSelected != _baselineState) || HasCustomValueChanges;

    public bool IsBaselineEnabled => _baselineState;

    public string DefaultStateLabel => _baselineState
        ? RegistryOptimizerStrings.DefaultEnabled
        : RegistryOptimizerStrings.DefaultDisabled;

    public bool HasCustomValueChanges => SupportsCustomValue && !IsCustomValueBaseline;

    public bool IsCustomValueBaseline => SupportsCustomValue ? ArePayloadsEqual(_customValuePayload, _baselineCustomValuePayload) : true;

    public bool HasValidationError => SupportsCustomValue && !CustomValueIsValid;

    public string CustomValueInfoText => BuildCustomValueInfoText();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingChanges))]
    private bool _isSelected;

    [ObservableProperty]
    private string? _currentValue;

    [ObservableProperty]
    private string? _recommendedValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingChanges))]
    [NotifyPropertyChangedFor(nameof(HasCustomValueChanges))]
    [NotifyPropertyChangedFor(nameof(IsCustomValueBaseline))]
    private string? _customValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationError))]
    private bool _customValueIsValid = true;

    [ObservableProperty]
    private string? _customValueError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingChanges))]
    [NotifyPropertyChangedFor(nameof(HasCustomValueChanges))]
    [NotifyPropertyChangedFor(nameof(IsCustomValueBaseline))]
    [NotifyPropertyChangedFor(nameof(HasValidationError))]
    private bool _supportsCustomValue;

    public ReadOnlyObservableCollection<string> CurrentValueLines => _currentValueLinesView;

    public ReadOnlyObservableCollection<RegistrySnapshotDisplay> SnapshotEntries => _snapshotEntriesView;

    public bool HasSnapshots => SnapshotEntries.Count > 0;

    public bool IsStateLoaded => !IsStatePending && _lastObservedAt != default;

    public bool HasStateError => !string.IsNullOrWhiteSpace(StateError);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStateLoaded))]
    private bool _isStatePending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStateError))]
    private string? _stateError;

    public void SetSelection(bool value)
    {
        IsSelected = value;
    }

    public void CommitSelection()
    {
        _baselineState = IsSelected;
        SetBaselineToCurrentCustomValue();
        OnPropertyChanged(nameof(IsBaselineEnabled));
        OnPropertyChanged(nameof(DefaultStateLabel));
    }

    public void RevertToBaseline()
    {
        IsSelected = _baselineState;

        if (SupportsCustomValue)
        {
            if (_baselineCustomValueRaw is null)
            {
                CustomValue = null;
            }
            else if (!string.Equals(CustomValue, _baselineCustomValueRaw, StringComparison.Ordinal))
            {
                CustomValue = _baselineCustomValueRaw;
            }

            ValidateCustomValue();
        }
    }

    public void BeginStateRefresh()
    {
        IsStatePending = true;
        StateError = null;
    }

    public void CompleteStateRefresh()
    {
        if (IsStatePending)
        {
            IsStatePending = false;
        }
    }

    public void ApplyStateFailure(string message)
    {
        IsStatePending = false;
        StateError = string.IsNullOrWhiteSpace(message) ? RegistryOptimizerStrings.ValueNotAvailable : message;

        if (_currentValueLines.Count == 0)
        {
            _currentValueLines.Add(RegistryOptimizerStrings.ValueNotAvailable);
            CurrentValue = _currentValueLines[0];
        }
    }

    public void UpdateState(RegistryTweakState state)
    {
        if (state is null)
        {
            throw new ArgumentNullException(nameof(state));
        }

        if (state.ObservedAt < _lastObservedAt)
        {
            return;
        }

        _lastObservedAt = state.ObservedAt;
        IsStatePending = false;

        var values = state.Values;
        var primaryValue = values.FirstOrDefault(v => v.SupportsCustomValue) ?? values.FirstOrDefault();
        var detectionError = ResolveDetectionError(state, primaryValue);

        PopulateCurrentValueLines(primaryValue, detectionError);
        RecommendedValue = ResolveRecommendedValueText(primaryValue);
        UpdateSnapshotEntries(primaryValue);
        StateError = string.IsNullOrWhiteSpace(detectionError) ? null : detectionError;

        var supportsCustom = (_definition.Constraints is not null) || values.Any(v => v.SupportsCustomValue);
        SupportsCustomValue = supportsCustom && ResolveCustomParameterName() is not null;

        if (!SupportsCustomValue)
        {
            return;
        }

        var initialCustomValue = DetermineInitialCustomValue(primaryValue);
        var shouldUpdateCustom = !_customValueInitialized || string.IsNullOrWhiteSpace(CustomValue);

        if (shouldUpdateCustom)
        {
            _customValueInitialized = true;
            if (!string.IsNullOrWhiteSpace(_pendingPersistedCustomValue))
            {
                CustomValue = _pendingPersistedCustomValue;
                _pendingPersistedCustomValue = null;
            }
            else
            {
                CustomValue = initialCustomValue;
            }
        }
        else if (!string.IsNullOrWhiteSpace(_pendingPersistedCustomValue))
        {
            CustomValue = _pendingPersistedCustomValue;
            _pendingPersistedCustomValue = null;
        }

        if (_baselineCustomValueRaw is null)
        {
            SetBaselineToCurrentCustomValue();
        }

        OnPropertyChanged(nameof(CustomValueInfoText));
    }

    private void PopulateCurrentValueLines(RegistryValueState? primaryValue, string? detectionError)
    {
        _currentValueLines.Clear();

        if (primaryValue is not null)
        {
            foreach (var line in primaryValue.CurrentDisplay)
            {
                var trimmed = line?.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    _currentValueLines.Add(trimmed);
                }
            }

            if (_currentValueLines.Count == 0 && primaryValue.CurrentValue is not null)
            {
                var formatted = FormatValue(primaryValue.CurrentValue);
                if (!string.IsNullOrWhiteSpace(formatted))
                {
                    _currentValueLines.Add(formatted);
                }
            }

            if (_currentValueLines.Count == 0)
            {
                var snapshotDisplay = ResolveSnapshotDisplay(primaryValue);
                if (!string.IsNullOrWhiteSpace(snapshotDisplay))
                {
                    _currentValueLines.Add(snapshotDisplay);
                }
            }
        }

        if (_currentValueLines.Count == 0 && !string.IsNullOrWhiteSpace(detectionError))
        {
            _currentValueLines.Add(detectionError);
        }

        if (_currentValueLines.Count == 0)
        {
            _currentValueLines.Add(RegistryOptimizerStrings.ValueNotAvailable);
        }

        CurrentValue = _currentValueLines[0];
    }

    private string ResolveRecommendedValueText(RegistryValueState? primaryValue)
    {
        if (primaryValue is null)
        {
            return RegistryOptimizerStrings.ValueRecommendationUnavailable;
        }

        var recommended = FormatRecommended(primaryValue);
        return string.IsNullOrWhiteSpace(recommended)
            ? RegistryOptimizerStrings.ValueRecommendationUnavailable
            : recommended!;
    }

    private void UpdateSnapshotEntries(RegistryValueState? primaryValue)
    {
        _snapshotEntries.Clear();

        if (primaryValue is not null)
        {
            foreach (var snapshot in primaryValue.Snapshots)
            {
                var path = string.IsNullOrWhiteSpace(snapshot.Path)
                    ? primaryValue.RegistryPathPattern
                    : snapshot.Path!.Trim();

                var display = string.IsNullOrWhiteSpace(snapshot.Display)
                    ? FormatValue(snapshot.Value)
                    : snapshot.Display.Trim();

                if (string.IsNullOrWhiteSpace(display))
                {
                    continue;
                }

                var resolvedPath = string.IsNullOrWhiteSpace(path)
                    ? primaryValue.RegistryPathPattern
                    : path;

                _snapshotEntries.Add(new RegistrySnapshotDisplay(resolvedPath ?? string.Empty, display));
            }
        }

        OnPropertyChanged(nameof(HasSnapshots));
    }

    public IReadOnlyDictionary<string, object?>? GetTargetParameterOverrides()
    {
        if (!SupportsCustomValue || !CustomValueIsValid || _customValuePayload is null)
        {
            return null;
        }

        var parameterName = ResolveCustomParameterName();
        if (string.IsNullOrWhiteSpace(parameterName))
        {
            return null;
        }

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [parameterName] = ConvertPayloadForScript(_customValuePayload)
        };
    }

    public IReadOnlyDictionary<string, object?>? GetBaselineParameterOverrides()
    {
        if (!SupportsCustomValue || _baselineCustomValuePayload is null)
        {
            return null;
        }

        var parameterName = ResolveCustomParameterName();
        if (string.IsNullOrWhiteSpace(parameterName))
        {
            return null;
        }

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [parameterName] = ConvertPayloadForScript(_baselineCustomValuePayload)
        };
    }

    partial void OnCustomValueChanged(string? value)
    {
        _customValueInitialized = true;
        ValidateCustomValue();
    }

    partial void OnSupportsCustomValueChanged(bool oldValue, bool newValue)
    {
        if (!newValue)
        {
            _customValuePayload = null;
            _baselineCustomValuePayload = null;
            _baselineCustomValueRaw = null;
            _customParameterName = null;
            _pendingPersistedCustomValue = _preferences.GetCustomValue(_definition.Id);
            CustomValueIsValid = true;
            CustomValueError = null;
            OnPropertyChanged(nameof(HasPendingChanges));
            OnPropertyChanged(nameof(HasCustomValueChanges));
            OnPropertyChanged(nameof(IsCustomValueBaseline));
            OnPropertyChanged(nameof(HasValidationError));
            OnPropertyChanged(nameof(CustomValueInfoText));
        }
        else
        {
            _pendingPersistedCustomValue ??= _preferences.GetCustomValue(_definition.Id);
            ValidateCustomValue();
            OnPropertyChanged(nameof(CustomValueInfoText));
        }
    }

    private void SetBaselineToCurrentCustomValue()
    {
        if (!SupportsCustomValue || !CustomValueIsValid)
        {
            return;
        }

        _baselineCustomValueRaw = CustomValue;
        _baselineCustomValuePayload = _customValuePayload;
        PersistBaseline();
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(HasCustomValueChanges));
        OnPropertyChanged(nameof(IsCustomValueBaseline));
        OnPropertyChanged(nameof(CustomValueInfoText));
    }

    private void ValidateCustomValue()
    {
        if (!SupportsCustomValue)
        {
            _customValuePayload = null;
            CustomValueIsValid = true;
            CustomValueError = null;
            OnPropertyChanged(nameof(HasPendingChanges));
            OnPropertyChanged(nameof(HasCustomValueChanges));
            OnPropertyChanged(nameof(IsCustomValueBaseline));
            OnPropertyChanged(nameof(CustomValueInfoText));
            return;
        }

        if (!TryParseCustomValue(CustomValue, out var payload, out var error))
        {
            _customValuePayload = null;
            CustomValueIsValid = false;
            CustomValueError = error;
            OnPropertyChanged(nameof(HasPendingChanges));
            OnPropertyChanged(nameof(HasCustomValueChanges));
            OnPropertyChanged(nameof(IsCustomValueBaseline));
            OnPropertyChanged(nameof(CustomValueInfoText));
            return;
        }

        _customValuePayload = payload;
        CustomValueIsValid = true;
        CustomValueError = null;
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(HasCustomValueChanges));
        OnPropertyChanged(nameof(IsCustomValueBaseline));
        OnPropertyChanged(nameof(CustomValueInfoText));
    }

    partial void OnRecommendedValueChanged(string? oldValue, string? newValue)
    {
        OnPropertyChanged(nameof(CustomValueInfoText));
    }

    private string BuildCustomValueInfoText()
    {
        if (!SupportsCustomValue)
        {
            return RegistryOptimizerStrings.CustomValueNotSupported;
        }

        var recommended = string.IsNullOrWhiteSpace(RecommendedValue)
            ? RegistryOptimizerStrings.ValueRecommendationUnavailable
            : RecommendedValue!;

        var constraints = _definition.Constraints;
        if (constraints is not null && constraints.Min.HasValue && constraints.Max.HasValue)
        {
            var minText = FormatNumeric(constraints.Min.Value);
            var maxText = FormatNumeric(constraints.Max.Value);
            var defaultText = constraints.Default.HasValue
                ? FormatNumeric(constraints.Default.Value)
                : recommended;

            return string.Format(
                CultureInfo.CurrentCulture,
                RegistryOptimizerStrings.CustomValueInfoRange,
                minText,
                maxText,
                defaultText,
                recommended);
        }

        return string.Format(
            CultureInfo.CurrentCulture,
            RegistryOptimizerStrings.CustomValueInfoGeneral,
            recommended);
    }

    private static string FormatNumeric(double value)
    {
        return value.ToString("0.##", CultureInfo.CurrentCulture);
    }

    private void PersistBaseline()
    {
        if (_preferences is null)
        {
            return;
        }

        if (!SupportsCustomValue)
        {
            return;
        }

        _preferences.SetCustomValue(Id, string.IsNullOrWhiteSpace(_baselineCustomValueRaw) ? null : _baselineCustomValueRaw);
    }

    private string? ResolveCustomParameterName()
    {
        if (_customParameterName is not null)
        {
            return _customParameterName;
        }

        var parameters = _definition.EnableOperation?.Parameters;
        if (parameters is null || parameters.Count == 0)
        {
            return null;
        }

        foreach (var pair in parameters)
        {
            if (pair.Value is bool)
            {
                continue;
            }

            _customParameterName = pair.Key;
            return _customParameterName;
        }

        return null;
    }

    private string? DetermineInitialCustomValue(RegistryValueState? state)
    {
        if (state is not null)
        {
            if (state.CurrentValue is not null)
            {
                return FormatEditableValue(state.CurrentValue);
            }

            if (state.RecommendedValue is not null)
            {
                return FormatEditableValue(state.RecommendedValue);
            }
        }

        var @default = _definition.Constraints?.Default;
        return @default.HasValue ? @default.Value.ToString(CultureInfo.InvariantCulture) : null;
    }

    private static string? FormatEditableValue(object value)
    {
        return value switch
        {
            null => null,
            string s => s,
            double d => d.ToString("0.##", CultureInfo.InvariantCulture),
            float f => f.ToString("0.##", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
    }

    private static string? ResolveDetectionError(RegistryTweakState state, RegistryValueState? primaryValue)
    {
        if (primaryValue is not null)
        {
            var valueError = primaryValue.Errors.FirstOrDefault(static e => !string.IsNullOrWhiteSpace(e));
            if (!string.IsNullOrWhiteSpace(valueError))
            {
                return valueError;
            }
        }

        var stateError = state.Errors.FirstOrDefault(static e => !string.IsNullOrWhiteSpace(e));
        return string.IsNullOrWhiteSpace(stateError) ? null : stateError;
    }

    private static string? FormatDisplayValue(ImmutableArray<string> display, object? fallback)
    {
        if (!display.IsDefaultOrEmpty)
        {
            var candidate = display.FirstOrDefault(static line => !string.IsNullOrWhiteSpace(line));
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return FormatValue(fallback);
    }

    private static string? FormatRecommended(RegistryValueState state)
    {
        if (!string.IsNullOrWhiteSpace(state.RecommendedDisplay))
        {
            return state.RecommendedDisplay;
        }

        return FormatValue(state.RecommendedValue);
    }

    private static string? FormatValue(object? value)
    {
        return value switch
        {
            null => null,
            string s => string.IsNullOrWhiteSpace(s) ? null : s,
            bool b => b.ToString(),
            double d => d.ToString("0.##", CultureInfo.CurrentCulture),
            float f => f.ToString("0.##", CultureInfo.CurrentCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.CurrentCulture),
            IEnumerable enumerable => string.Join(", ", enumerable.Cast<object?>().Select(FormatValue).Where(static v => !string.IsNullOrWhiteSpace(v))),
            _ => value.ToString()
        };
    }

    private static string? ResolveSnapshotDisplay(RegistryValueState value)
    {
        foreach (var snapshot in value.Snapshots)
        {
            if (!string.IsNullOrWhiteSpace(snapshot.Display))
            {
                return snapshot.Display.Trim();
            }

            if (snapshot.Value is not null)
            {
                var formatted = FormatValue(snapshot.Value);
                if (!string.IsNullOrWhiteSpace(formatted))
                {
                    return formatted;
                }
            }
        }

        var fallback = FormatValue(value.CurrentValue) ?? FormatValue(value.RecommendedValue);
        return string.IsNullOrWhiteSpace(fallback) ? null : fallback.Trim();
    }

    private bool TryParseCustomValue(string? input, out object? payload, out string? error)
    {
        payload = null;
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Value is required.";
            return false;
        }

        var trimmed = input.Trim();
        var constraints = _definition.Constraints;
        var type = constraints?.Type?.ToLowerInvariant();

        if (string.Equals(type, "range", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "number", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseDouble(trimmed, out var numeric))
            {
                error = "Enter a valid number.";
                return false;
            }

            if (constraints?.Min is double min && numeric < min)
            {
                error = string.Format(CultureInfo.CurrentCulture, "Value must be at least {0}.", min);
                return false;
            }

            if (constraints?.Max is double max && numeric > max)
            {
                error = string.Format(CultureInfo.CurrentCulture, "Value must be at most {0}.", max);
                return false;
            }

            payload = numeric;
            return true;
        }

        payload = trimmed;
        return true;
    }

    private static bool TryParseDouble(string text, out double value)
    {
        return double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value)
            || double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value);
    }

    private static object? ConvertPayloadForScript(object? payload)
    {
        if (payload is double numeric)
        {
            if (Math.Abs(numeric - Math.Round(numeric)) < 0.0001)
            {
                return (int)Math.Round(numeric);
            }

            return numeric;
        }

        return payload;
    }

    private static bool ArePayloadsEqual(object? left, object? right)
    {
        if (left is null && right is null)
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        if (left is double leftDouble && right is double rightDouble)
        {
            return Math.Abs(leftDouble - rightDouble) < 0.0001;
        }

        return string.Equals(Convert.ToString(left, CultureInfo.InvariantCulture), Convert.ToString(right, CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record RegistrySnapshotDisplay(string Path, string Display);

public sealed class RegistryPresetViewModel
{
    private readonly RegistryPresetDefinition _definition;

    public RegistryPresetViewModel(RegistryPresetDefinition definition)
    {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
    }

    public string Id => _definition.Id;

    public string Name => _definition.Name;

    public string Description => _definition.Description;

    public string Icon => string.IsNullOrWhiteSpace(_definition.Icon) ? "🧰" : _definition.Icon;

    public bool IsDefault => _definition.IsDefault;

    public bool TryGetState(string tweakId, out bool value) => _definition.TryGetState(tweakId, out value);
}

public sealed class RegistryRestorePointCreatedEventArgs : EventArgs
{
    public RegistryRestorePointCreatedEventArgs(RegistryRestorePoint restorePoint)
    {
        RestorePoint = restorePoint ?? throw new ArgumentNullException(nameof(restorePoint));
    }

    public RegistryRestorePoint RestorePoint { get; }
}
