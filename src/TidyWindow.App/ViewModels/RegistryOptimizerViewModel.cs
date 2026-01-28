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
using Microsoft.VisualBasic.Devices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.Services;
using TidyWindow.App.Resources.Strings;
using TidyWindow.App.Views;
using TidyWindow.Core.Maintenance;

namespace TidyWindow.App.ViewModels;

public sealed partial class RegistryOptimizerViewModel : ViewModelBase
{
    private readonly ActivityLogService _activityLog;
    private readonly MainViewModel _mainViewModel;
    private readonly IRegistryOptimizerService _registryService;
    private readonly RegistryPreferenceService _registryPreferenceService;
    private readonly ISystemRestoreGuardService _restoreGuardService;
    private readonly Func<double>? _getTotalRamGb;
    private bool _isInitialized;
    private bool _isRestoringState;

    private static readonly TimeSpan SystemRestoreFreshnessThreshold = TimeSpan.FromHours(24);

    public event EventHandler<RegistryRestorePointCreatedEventArgs>? RestorePointCreated;

    public RegistryOptimizerViewModel(
        ActivityLogService activityLogService,
        MainViewModel mainViewModel,
        IRegistryOptimizerService registryService,
        RegistryPreferenceService registryPreferenceService,
        ISystemRestoreGuardService restoreGuardService,
        Func<double>? getTotalRamGb = null)
    {
        _activityLog = activityLogService ?? throw new ArgumentNullException(nameof(activityLogService));
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        _registryService = registryService ?? throw new ArgumentNullException(nameof(registryService));
        _registryPreferenceService = registryPreferenceService ?? throw new ArgumentNullException(nameof(registryPreferenceService));
        _restoreGuardService = restoreGuardService ?? throw new ArgumentNullException(nameof(restoreGuardService));
        _getTotalRamGb = getTotalRamGb;

        Tweaks = new ObservableCollection<RegistryTweakCardViewModel>();
        Presets = new ObservableCollection<RegistryPresetViewModel>();

        PopulateFromConfiguration();

        var restoredUserState = RestoreUserSelectionsFromPreferences();
        var initialPreset = DetermineInitialPreset();

        if (initialPreset is not null)
        {
            _isRestoringState = restoredUserState;
            SelectedPreset = initialPreset;
            _isRestoringState = false;
        }

        ApplyBaselineFromPreferences();

        UpdatePendingChanges();
        UpdateRestorePointState(_registryService.TryGetLatestRestorePoint());
        UpdateValidationState();
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

    [ObservableProperty]
    private DateTimeOffset? _latestRestorePointLocalTime;

    [ObservableProperty]
    private bool _isPresetDialogVisible;

    partial void OnSelectedPresetChanged(RegistryPresetViewModel? oldValue, RegistryPresetViewModel? newValue)
    {
        if (newValue is null)
        {
            _registryPreferenceService.SetSelectedPresetId(null);
            UpdatePresetCustomizationState();
            return;
        }

        if (_isRestoringState)
        {
            UpdatePresetCustomizationState();
            return;
        }

        ApplyPreset(newValue);
        _registryPreferenceService.SetSelectedPresetId(newValue.Id);
        if (_isInitialized)
        {
            _mainViewModel.SetStatusMessage($"Preset '{newValue.Name}' loaded.");
        }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        var selectedPreset = SelectedPreset;

        var hasAppliedSnapshot = _registryPreferenceService.HasAppliedSnapshot();
        var appliedPresetId = _registryPreferenceService.GetAppliedPresetId();

        var presetAlreadyApplied = selectedPreset is not null
            && hasAppliedSnapshot
            && _registryPreferenceService.AppliedStatesMatch(selectedPreset.States)
            && string.Equals(appliedPresetId, selectedPreset.Id, StringComparison.OrdinalIgnoreCase);

        var forcePresetReplay = selectedPreset is not null
            && !presetAlreadyApplied
            && (!hasAppliedSnapshot || string.IsNullOrWhiteSpace(appliedPresetId) || !string.Equals(appliedPresetId, selectedPreset.Id, StringComparison.OrdinalIgnoreCase));

        if (!HasPendingChanges && !forcePresetReplay)
        {
            if (presetAlreadyApplied && selectedPreset is not null)
            {
                var skipMessage = $"Preset '{selectedPreset.Name}' already applied; nothing to change.";
                LastOperationSummary = skipMessage;
                _activityLog.LogInformation("Registry", skipMessage);
                _mainViewModel.SetStatusMessage("Preset already applied.");
            }

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

        if (!forcePresetReplay && selectedPreset is not null && hasAppliedSnapshot)
        {
            forcePresetReplay = string.IsNullOrWhiteSpace(appliedPresetId)
                || !string.Equals(appliedPresetId, selectedPreset.Id, StringComparison.OrdinalIgnoreCase);
        }

        if (forcePresetReplay && selectedPreset is not null)
        {
            var presetTweaks = Tweaks.Where(tweak => selectedPreset.TryGetState(tweak.Id, out _));
            pendingTweaks = pendingTweaks
                .Concat(presetTweaks)
                .DistinctBy(t => t.Id)
                .ToList();
        }

        if (pendingTweaks.Count == 0)
        {
            UpdatePendingChanges();
            UpdateValidationState();

            if (presetAlreadyApplied && selectedPreset is not null)
            {
                var skipMessage = $"Preset '{selectedPreset.Name}' already applied; nothing to change.";
                LastOperationSummary = skipMessage;
                _activityLog.LogInformation("Registry", skipMessage);
                _mainViewModel.SetStatusMessage("Preset already applied.");
                return;
            }

            LastOperationSummary = $"No registry scripts required ({DateTime.Now:t}).";
            _activityLog.LogInformation("Registry", LastOperationSummary);
            _mainViewModel.SetStatusMessage("Registry tweaks already in desired state.");
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

        if (!TryRunPreflight(selections, out var preflightError))
        {
            LastOperationSummary = preflightError;
            _activityLog.LogWarning("Registry", preflightError);
            _mainViewModel.SetStatusMessage(preflightError);
            return;
        }

        // Build plans off the UI thread to keep the page responsive when many tweaks are selected.
        var plan = await Task.Run(() => _registryService.BuildPlan(selections)).ConfigureAwait(true);
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

        var guardResult = await _restoreGuardService.CheckAsync(SystemRestoreFreshnessThreshold);
        if (!guardResult.IsSatisfied)
        {
            HandleMissingSystemRestorePoint(guardResult);
            return;
        }

        IsBusy = true;
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

            var appliedStates = new List<RegistryAppliedState>(Tweaks.Count);

            foreach (var tweak in pendingTweaks)
            {
                tweak.CommitSelection();
            }

            foreach (var tweak in Tweaks)
            {
                appliedStates.Add(new RegistryAppliedState(
                    tweak.Id,
                    tweak.IsSelected,
                    tweak.SupportsCustomValue && tweak.CustomValueIsValid ? tweak.CustomValue?.Trim() : null));
            }

            _registryPreferenceService.SetAppliedStates(appliedStates, DateTimeOffset.UtcNow, selectedPreset?.Id);

            UpdatePendingChanges();
            UpdateValidationState();
            var appliedCount = pendingTweaks.Count;
            var summary = $"Applied {appliedCount} registry tweak(s) at {DateTime.Now:t}.";
            LastOperationSummary = summary;
            _activityLog.LogSuccess("Registry", summary, result.Executions.SelectMany(exec => exec.Output));
            _mainViewModel.SetStatusMessage("Registry tweaks applied.");

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
        }
    }

    [RelayCommand]
    private void ShowPresetsDialog()
    {
        IsPresetDialogVisible = true;
    }

    [RelayCommand]
    private void ClosePresetsDialog()
    {
        IsPresetDialogVisible = false;
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

    private bool CanApply() => HasPendingChanges && !IsBusy && !HasValidationErrors;

    private bool CanRevertChanges() => HasPendingChanges && !IsBusy;

    partial void OnIsBusyChanged(bool oldValue, bool newValue)
    {
        ApplyCommand.NotifyCanExecuteChanged();
        RevertChangesCommand.NotifyCanExecuteChanged();
        RestoreLastSnapshotCommand.NotifyCanExecuteChanged();
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

    private bool RestoreUserSelectionsFromPreferences()
    {
        var restored = false;

        foreach (var tweak in Tweaks)
        {
            if (_registryPreferenceService.TryGetTweakState(tweak.Id, out var state))
            {
                tweak.SetSelection(state);
                restored = true;
            }
        }

        return restored;
    }

    private RegistryPresetViewModel? DetermineInitialPreset()
    {
        var storedPresetId = _registryPreferenceService.GetSelectedPresetId();
        if (!string.IsNullOrWhiteSpace(storedPresetId))
        {
            var storedPreset = Presets.FirstOrDefault(preset => string.Equals(preset.Id, storedPresetId, StringComparison.OrdinalIgnoreCase));
            if (storedPreset is not null)
            {
                return storedPreset;
            }
        }

        return Presets.FirstOrDefault(preset => preset.IsDefault) ?? Presets.FirstOrDefault();
    }

    private void ApplyBaselineFromPreferences()
    {
        foreach (var tweak in Tweaks)
        {
            var baselineState = _registryPreferenceService.TryGetAppliedTweakState(tweak.Id, out var appliedState)
                ? appliedState
                : tweak.DefaultState;

            var appliedCustom = _registryPreferenceService.GetAppliedCustomValue(tweak.Id);
            tweak.SetBaseline(baselineState, appliedCustom);
        }
    }

    private void UpdatePendingChanges()
    {
        var pending = Tweaks.Any(tweak => tweak.HasPendingChanges);

        if (!pending && SelectedPreset is not null)
        {
            var hasAppliedSnapshot = _registryPreferenceService.HasAppliedSnapshot();
            var appliedPresetId = _registryPreferenceService.GetAppliedPresetId();
            var presetMatch = hasAppliedSnapshot && _registryPreferenceService.AppliedStatesMatch(SelectedPreset.States);

            if (!presetMatch || string.IsNullOrWhiteSpace(appliedPresetId) || !string.Equals(appliedPresetId, SelectedPreset.Id, StringComparison.OrdinalIgnoreCase))
            {
                pending = true;
            }
        }

        if (HasPendingChanges != pending)
        {
            HasPendingChanges = pending;
        }

        UpdatePresetCustomizationState();
    }

    private bool TryRunPreflight(IReadOnlyList<RegistrySelection> selections, out string error)
    {
        const double minimumRamGb = 16d;

        var targetsDisablePagingExecutive = selections.Any(selection =>
            string.Equals(selection.TweakId, "disable-paging-executive", StringComparison.OrdinalIgnoreCase)
            && selection.TargetState);

        if (targetsDisablePagingExecutive)
        {
            try
            {
                var totalRamGb = _getTotalRamGb?.Invoke();
                if (totalRamGb is null)
                {
                    var info = new ComputerInfo();
                    totalRamGb = info.TotalPhysicalMemory / 1024d / 1024d / 1024d;
                }
                if (totalRamGb < minimumRamGb)
                {
                    error = $"Skip 'Keep kernel in RAM' on systems with under {minimumRamGb:0} GB (detected {totalRamGb:0.#} GB).";
                    return false;
                }
            }
            catch
            {
                // If we cannot read memory, fall through and allow apply.
            }
        }

        error = string.Empty;
        return true;
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
        LatestRestorePointLocalTime = restorePoint?.CreatedUtc.ToLocalTime();
    }

    private void OnRestorePointCreated(RegistryRestorePoint restorePoint)
    {
        RestorePointCreated?.Invoke(this, new RegistryRestorePointCreatedEventArgs(restorePoint));
    }

    private void HandleMissingSystemRestorePoint(SystemRestoreGuardCheckResult result)
    {
        var latestDescription = result.LatestRestorePointUtc is null
            ? "No System Restore checkpoint detected."
            : $"Latest checkpoint: {result.LatestRestorePointUtc.Value.ToLocalTime():f}.";

        var summary = "Blocked: create a System Restore checkpoint within the last 24 hours before applying registry tweaks.";
        LastOperationSummary = summary;
        _activityLog.LogWarning("Registry", $"{summary} {latestDescription}");
        _mainViewModel.SetStatusMessage("Create a System Restore checkpoint before applying registry tweaks.");

        var prompt = new SystemRestoreGuardPrompt(
            source: "Registry optimizer",
            headline: "Create a restore point first",
            body: "Open Essentials ▸ System Restore manager to capture a checkpoint automatically, then return to Registry optimizer.");

        _restoreGuardService.RequestPrompt(prompt);
        _mainViewModel.NavigateTo(typeof(EssentialsPage));
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

        _snapshotEntries = new ObservableCollection<RegistrySnapshotDisplay>();
        _snapshotEntriesView = new ReadOnlyObservableCollection<RegistrySnapshotDisplay>(_snapshotEntries);
        _isStatePending = true;
        RecommendedValue = RegistryOptimizerStrings.ValueRecommendationUnavailable;

        var definitionRecommendation = ResolveDefinitionRecommendation();
        if (!string.IsNullOrWhiteSpace(definitionRecommendation))
        {
            RecommendedValue = definitionRecommendation;
        }

        var initialCustomValue = ResolveInitialCustomValue(definitionRecommendation);
        if (!string.IsNullOrWhiteSpace(initialCustomValue))
        {
            CustomValue = initialCustomValue;
            _pendingPersistedCustomValue = null;
        }

        var supportsCustomByDefinition = DetermineSupportsCustomValueFromDefinition();
        SupportsCustomValue = supportsCustomByDefinition && ResolveCustomParameterName() is not null;

        if (SupportsCustomValue)
        {
            ValidateCustomValue();
            SetBaselineToCurrentCustomValue();
        }
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

    public bool DefaultState => _definition.DefaultState;

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

    partial void OnIsSelectedChanged(bool oldValue, bool newValue)
    {
        _preferences.SetTweakState(Id, newValue);
    }

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

    [ObservableProperty]
    private bool _isDetailsVisible;

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

    public void SetBaseline(bool value, string? customValue)
    {
        _baselineState = value;

        if (!SupportsCustomValue)
        {
            _baselineCustomValueRaw = null;
            _baselineCustomValuePayload = null;
        }
        else if (!string.IsNullOrWhiteSpace(customValue) && TryParseCustomValue(customValue, out var payload, out _))
        {
            _baselineCustomValueRaw = customValue.Trim();
            _baselineCustomValuePayload = payload;
        }

        OnPropertyChanged(nameof(IsBaselineEnabled));
        OnPropertyChanged(nameof(DefaultStateLabel));
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(HasCustomValueChanges));
        OnPropertyChanged(nameof(IsCustomValueBaseline));
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

    [RelayCommand]
    private void ToggleDetails()
    {
        IsDetailsVisible = !IsDetailsVisible;
    }

    private string ResolveRecommendedValueText(RegistryValueState? primaryValue)
    {
        if (primaryValue is not null)
        {
            var recommended = FormatRecommended(primaryValue);
            if (!string.IsNullOrWhiteSpace(recommended))
            {
                return recommended!;
            }
        }

        return ResolveDefinitionRecommendation() ?? RegistryOptimizerStrings.ValueRecommendationUnavailable;
    }

    private string? ResolveDefinitionRecommendation()
    {
        var detectionValues = _definition.Detection?.Values ?? ImmutableArray<RegistryValueDetection>.Empty;
        if (detectionValues.IsDefaultOrEmpty)
        {
            return null;
        }

        var fallbackDefinition = detectionValues.FirstOrDefault(v => v.SupportsCustomValue)
            ?? detectionValues.FirstOrDefault();

        if (fallbackDefinition is null)
        {
            return null;
        }

        var detectionRecommended = FormatValue(fallbackDefinition.RecommendedValue);
        return string.IsNullOrWhiteSpace(detectionRecommended) ? null : detectionRecommended;
    }

    private string? ResolveInitialCustomValue(string? definitionRecommendation)
    {
        if (!string.IsNullOrWhiteSpace(_pendingPersistedCustomValue))
        {
            return _pendingPersistedCustomValue;
        }

        if (!string.IsNullOrWhiteSpace(definitionRecommendation))
        {
            return definitionRecommendation;
        }

        var constraintsDefault = _definition.Constraints?.Default;
        if (constraintsDefault.HasValue)
        {
            return FormatNumeric(constraintsDefault.Value);
        }

        return null;
    }

    private bool DetermineSupportsCustomValueFromDefinition()
    {
        if (_definition.Constraints is not null)
        {
            return true;
        }

        var detectionValues = _definition.Detection?.Values ?? ImmutableArray<RegistryValueDetection>.Empty;
        if (!detectionValues.IsDefaultOrEmpty && detectionValues.Any(v => v.SupportsCustomValue))
        {
            return true;
        }

        return false;
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
            if (!string.IsNullOrWhiteSpace(CustomValue))
            {
                ValidateCustomValue();
            }
            else if (!string.IsNullOrWhiteSpace(_pendingPersistedCustomValue))
            {
                CustomValue = _pendingPersistedCustomValue;
                _pendingPersistedCustomValue = null;
            }
            else
            {
                CustomValueIsValid = true;
                CustomValueError = null;
            }
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
            if (!string.IsNullOrWhiteSpace(state.RecommendedDisplay))
            {
                return state.RecommendedDisplay;
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

        var fallback = FormatValue(value.RecommendedValue);
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

    public IReadOnlyDictionary<string, bool> States => _definition.States;

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
