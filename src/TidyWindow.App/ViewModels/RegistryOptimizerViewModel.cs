using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
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
    private readonly RegistryOptimizerService _registryService;
    private bool _isInitialized;

    public event EventHandler<RegistryRestorePointCreatedEventArgs>? RestorePointCreated;

    public RegistryOptimizerViewModel(ActivityLogService activityLogService, MainViewModel mainViewModel, RegistryOptimizerService registryService)
    {
        _activityLog = activityLogService ?? throw new ArgumentNullException(nameof(activityLogService));
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        _registryService = registryService ?? throw new ArgumentNullException(nameof(registryService));

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

        var pendingTweaks = Tweaks.Where(t => t.HasPendingChanges).ToList();
        if (pendingTweaks.Count == 0)
        {
            UpdatePendingChanges();
            return;
        }

        var selections = pendingTweaks
            .Select(tweak => new RegistrySelection(tweak.Id, tweak.IsSelected, tweak.IsBaselineEnabled))
            .ToImmutableArray();

        var plan = _registryService.BuildPlan(selections);
        if (!plan.HasWork)
        {
            foreach (var tweak in pendingTweaks)
            {
                tweak.CommitSelection();
            }

            UpdatePendingChanges();
            LastOperationSummary = $"No registry scripts required ({DateTime.Now:t}).";
            _activityLog.LogInformation("Registry", LastOperationSummary);
            _mainViewModel.SetStatusMessage("Registry tweaks already in desired state.");
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

            foreach (var tweak in pendingTweaks)
            {
                tweak.CommitSelection();
            }

            UpdatePendingChanges();
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

    [RelayCommand(CanExecute = nameof(CanRevertChanges))]
    private void RevertChanges()
    {
        foreach (var tweak in Tweaks)
        {
            tweak.RevertToBaseline();
        }

        UpdatePendingChanges();
        LastOperationSummary = $"Selections reverted at {DateTime.Now:t}.";
        _activityLog.LogInformation("Registry", "Selections reverted to last applied values.");
        _mainViewModel.SetStatusMessage("Registry selections reset.");
    }

    private bool CanApply() => HasPendingChanges && !IsBusy;

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
                tweakDefinition.Id,
                localizedName,
                localizedSummary,
                tweakDefinition.RiskLevel,
                localizedRisk,
                tweakDefinition.Icon,
                tweakDefinition.Category,
                tweakDefinition.DefaultState,
                tweakDefinition.DocumentationLink,
                tweakDefinition.Constraints);

            tweak.PropertyChanged += (_, _) => UpdatePendingChanges();
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
    private bool _baselineState;

    public RegistryTweakCardViewModel(string id, string title, string summary, string riskLevel, string riskLabel, string icon, string category, bool baselineState, string? documentationLink, RegistryTweakConstraints? constraints = null)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(id));
        }

        Id = id;
        Title = title ?? throw new ArgumentNullException(nameof(title));
        Summary = summary ?? throw new ArgumentNullException(nameof(summary));
        RiskLevel = riskLevel ?? "Safe";
        RiskLabel = string.IsNullOrWhiteSpace(riskLabel) ? RegistryOptimizerStrings.GetTweakRisk(id, RiskLevel) : riskLabel;
        Icon = string.IsNullOrWhiteSpace(icon) ? "🧰" : icon;
        Category = category ?? "General";
        DocumentationLink = documentationLink;
        Constraints = constraints;
        _baselineState = baselineState;
        _isSelected = baselineState;
    }

    public string Id { get; }

    public string Title { get; }

    public string Summary { get; }

    public string RiskLevel { get; }

    public string RiskLabel { get; }

    public string Icon { get; }

    public string Category { get; }

    public string? DocumentationLink { get; }

    public RegistryTweakConstraints? Constraints { get; }

    public bool HasPendingChanges => IsSelected != _baselineState;

    public bool IsBaselineEnabled => _baselineState;

    public string DefaultStateLabel => _baselineState
        ? RegistryOptimizerStrings.DefaultEnabled
        : RegistryOptimizerStrings.DefaultDisabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingChanges))]
    private bool _isSelected;

    public void SetSelection(bool value)
    {
        IsSelected = value;
    }

    public void CommitSelection()
    {
        _baselineState = IsSelected;
        OnPropertyChanged(nameof(IsBaselineEnabled));
        OnPropertyChanged(nameof(DefaultStateLabel));
    }

    public void RevertToBaseline()
    {
        IsSelected = _baselineState;
    }
}

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
