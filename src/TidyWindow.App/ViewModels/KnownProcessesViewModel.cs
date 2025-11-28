using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.Services;
using TidyWindow.Core.Processes;

namespace TidyWindow.App.ViewModels;

public sealed partial class KnownProcessesViewModel : ViewModelBase
{
    private readonly ProcessCatalogParser _catalogParser;
    private readonly ProcessStateStore _stateStore;
    private readonly ProcessControlService _controlService;
    private readonly IUserConfirmationService _confirmationService;
    private readonly MainViewModel _mainViewModel;
    private bool _isInitialized;

    public KnownProcessesViewModel(
        ProcessCatalogParser catalogParser,
        ProcessStateStore stateStore,
        ProcessControlService controlService,
        IUserConfirmationService confirmationService,
        MainViewModel mainViewModel,
        ProcessPreferencesViewModel processPreferencesViewModel,
        AntiSystemViewModel antiSystemViewModel)
    {
        _catalogParser = catalogParser ?? throw new ArgumentNullException(nameof(catalogParser));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _controlService = controlService ?? throw new ArgumentNullException(nameof(controlService));
        _confirmationService = confirmationService ?? throw new ArgumentNullException(nameof(confirmationService));
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        Preferences = processPreferencesViewModel ?? throw new ArgumentNullException(nameof(processPreferencesViewModel));
        AntiSystem = antiSystemViewModel ?? throw new ArgumentNullException(nameof(antiSystemViewModel));
        Categories = new ObservableCollection<KnownProcessCategoryViewModel>();
    }

    public ObservableCollection<KnownProcessCategoryViewModel> Categories { get; }

    public ProcessPreferencesViewModel Preferences { get; }

    public AntiSystemViewModel AntiSystem { get; }

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _summary = "Loading known processes...";

    [ObservableProperty]
    private bool _hasProcesses;

    [ObservableProperty]
    private KnownProcessViewSection _activeSection = KnownProcessViewSection.Catalog;

    public void EnsureInitialized()
    {
        if (_isInitialized)
        {
            return;
        }

        Refresh();
        AntiSystem.EnsureInitialized();
        _isInitialized = true;
    }

    [RelayCommand]
    private void Refresh()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            _mainViewModel.SetStatusMessage("Loading known processes...");
            var snapshot = _catalogParser.LoadSnapshot();
            var preferenceLookup = BuildPreferenceLookup(_stateStore.GetPreferences());

            Categories.Clear();
            HasProcesses = false;
            foreach (var category in snapshot.Categories)
            {
                var categoryVm = new KnownProcessCategoryViewModel(category.Key, category.Name, category.Description, category.IsCaution);

                foreach (var entry in snapshot.Entries.Where(entry => string.Equals(entry.CategoryKey, category.Key, StringComparison.OrdinalIgnoreCase)))
                {
                    preferenceLookup.TryGetValue(entry.Identifier, out var preference);
                    var effectiveAction = preference?.Action ?? entry.RecommendedAction;
                    var effectiveSource = preference?.Source ?? ProcessPreferenceSource.SystemDefault;
                    var card = new KnownProcessCardViewModel(this, entry, effectiveAction, effectiveSource, preference?.Notes);
                    categoryVm.Processes.Add(card);
                }

                if (categoryVm.Processes.Count > 0)
                {
                    Categories.Add(categoryVm);
                }
            }

            HasProcesses = Categories.Count > 0;
            UpdateSummary();
        }
        catch (Exception ex)
        {
            _mainViewModel.LogActivity(ActivityLogLevel.Error, "Known Processes", "Failed to load catalog.", new[] { ex.Message });
            Summary = "Unable to load known processes.";
        }
        finally
        {
            IsBusy = false;
            _mainViewModel.SetStatusMessage("Ready");
        }
    }

    [RelayCommand]
    private void SwitchSection(KnownProcessViewSection section)
    {
        ActiveSection = section;
        if (section == KnownProcessViewSection.AntiSystem)
        {
            AntiSystem.EnsureInitialized();
        }
    }

    internal void ToggleAction(KnownProcessCardViewModel card)
    {
        if (card is null)
        {
            return;
        }

        var newAction = card.EffectiveAction == ProcessActionPreference.AutoStop
            ? ProcessActionPreference.Keep
            : ProcessActionPreference.AutoStop;

        ApplyUserPreference(card, newAction);
    }

    internal async Task StopServiceAsync(KnownProcessCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        if (!card.SupportsServiceControl)
        {
            card.LastActionMessage = "This catalog entry cannot be controlled automatically.";
            return;
        }

        if (!ConfirmServiceAction("Stop", "Stopping", card))
        {
            card.LastActionMessage = "Stop cancelled.";
            return;
        }

        card.IsActionInProgress = true;
        try
        {
            _mainViewModel.SetStatusMessage($"Stopping {card.DisplayName}...");
            var result = await _controlService.StopAsync(card.DisplayName);
            var message = BuildActionMessage(card, result.Message);
            card.LastActionMessage = message;
            _mainViewModel.LogActivity(result.Success ? ActivityLogLevel.Success : ActivityLogLevel.Warning, "Known Processes", message);
        }
        finally
        {
            card.IsActionInProgress = false;
            _mainViewModel.SetStatusMessage("Ready");
        }
    }

    internal async Task RestartServiceAsync(KnownProcessCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        if (!card.SupportsServiceControl)
        {
            card.LastActionMessage = "This catalog entry cannot be controlled automatically.";
            return;
        }

        if (!ConfirmServiceAction("Restart", "Restarting", card))
        {
            card.LastActionMessage = "Restart cancelled.";
            return;
        }

        card.IsActionInProgress = true;
        try
        {
            _mainViewModel.SetStatusMessage($"Restarting {card.DisplayName}...");
            var result = await _controlService.RestartAsync(card.DisplayName);
            var message = BuildActionMessage(card, result.Message);
            card.LastActionMessage = message;
            _mainViewModel.LogActivity(result.Success ? ActivityLogLevel.Success : ActivityLogLevel.Warning, "Known Processes", message);
        }
        finally
        {
            card.IsActionInProgress = false;
            _mainViewModel.SetStatusMessage("Ready");
        }
    }

    internal void LearnMore(KnownProcessCardViewModel card)
    {
        if (card is null)
        {
            return;
        }

        try
        {
            var query = Uri.EscapeDataString($"{card.DisplayName} Windows service");
            var uri = new Uri($"https://learn.microsoft.com/search/?terms={query}");
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
            _mainViewModel.LogActivityInformation("Known Processes", $"Opened documentation for {card.DisplayName}.");
        }
        catch (Exception ex)
        {
            _mainViewModel.LogActivity(ActivityLogLevel.Warning, "Known Processes", $"Unable to launch documentation for {card.DisplayName}.", new[] { ex.Message });
            card.LastActionMessage = "Couldn't open browser.";
        }
    }

    private void ApplyUserPreference(KnownProcessCardViewModel card, ProcessActionPreference action)
    {
        try
        {
            var preference = new ProcessPreference(card.Identifier, action, ProcessPreferenceSource.UserOverride, DateTimeOffset.UtcNow, "Set via Known Processes tab");
            _stateStore.UpsertPreference(preference);
            card.ApplyPreference(action, ProcessPreferenceSource.UserOverride);
            UpdateSummary();
            _mainViewModel.LogActivityInformation("Known Processes", $"{card.DisplayName} set to {card.ActionLabel}.");
        }
        catch (Exception ex)
        {
            _mainViewModel.LogActivity(ActivityLogLevel.Error, "Known Processes", $"Failed to update preference for {card.DisplayName}.", new[] { ex.Message });
            card.LastActionMessage = ex.Message;
        }
    }

    private void UpdateSummary()
    {
        if (!HasProcesses)
        {
            Summary = "No catalog recommendations are available yet.";
            return;
        }

        var autoStopCount = Categories.SelectMany(category => category.Processes).Count(process => process.IsAutoStop);
        Summary = autoStopCount switch
        {
            0 => "No processes configured to auto-stop yet.",
            1 => "1 process configured to auto-stop.",
            _ => $"{autoStopCount} processes configured to auto-stop."
        };
    }

    private bool ConfirmServiceAction(string action, string gerund, KnownProcessCardViewModel card)
    {
        var title = $"Confirm {action}";
        var message = $"{gerund} '{card.DisplayName}' can temporarily disrupt Windows features that depend on it.\n\nDo you want to continue?";
        return _confirmationService.Confirm(title, message);
    }

    private static string BuildActionMessage(KnownProcessCardViewModel card, string resultMessage)
    {
        var trimmed = string.IsNullOrWhiteSpace(resultMessage) ? "Operation completed." : resultMessage.Trim();
        return $"{card.DisplayName}: {trimmed}";
    }

    private static Dictionary<string, ProcessPreference> BuildPreferenceLookup(IReadOnlyCollection<ProcessPreference> preferences)
    {
        var lookup = new Dictionary<string, ProcessPreference>(StringComparer.OrdinalIgnoreCase);
        foreach (var preference in preferences)
        {
            lookup[preference.ProcessIdentifier] = preference;
        }

        return lookup;
    }
}

public enum KnownProcessViewSection
{
    Catalog,
    Settings,
    AntiSystem
}

public sealed partial class KnownProcessCategoryViewModel : ObservableObject
{
    public KnownProcessCategoryViewModel(string key, string name, string? description, bool isCaution)
    {
        Key = key;
        Name = name;
        Description = description;
        IsCaution = isCaution;
        Processes = new ObservableCollection<KnownProcessCardViewModel>();
    }

    public string Key { get; }

    public string Name { get; }

    public string? Description { get; }

    public bool IsCaution { get; }

    public ObservableCollection<KnownProcessCardViewModel> Processes { get; }

    [ObservableProperty]
    private bool _isExpanded = true;
}

public sealed partial class KnownProcessCardViewModel : ObservableObject
{
    private readonly KnownProcessesViewModel _owner;
    private readonly bool _isCaution;

    public KnownProcessCardViewModel(
        KnownProcessesViewModel owner,
        ProcessCatalogEntry entry,
        ProcessActionPreference action,
        ProcessPreferenceSource source,
        string? notes)
    {
        _owner = owner;
        Identifier = entry.Identifier;
        DisplayName = entry.DisplayName;
        CategoryKey = entry.CategoryKey;
        CategoryName = entry.CategoryName;
        Rationale = string.IsNullOrWhiteSpace(entry.Rationale)
            ? entry.CategoryDescription ?? "No rationale provided."
            : entry.Rationale;
        RecommendedAction = entry.RecommendedAction;
        _isCaution = entry.RiskLevel == ProcessRiskLevel.Caution;
        SupportsServiceControl = !entry.IsPattern;
        Notes = notes;
        ApplyPreference(action, source);
    }

    public string Identifier { get; }

    public string DisplayName { get; }

    public string CategoryKey { get; }

    public string CategoryName { get; }

    public string Rationale { get; }

    public ProcessActionPreference RecommendedAction { get; }

    public string? Notes { get; }

    public bool IsCaution => _isCaution;

    public bool SupportsServiceControl { get; }

    public string RecommendationLabel => RecommendedAction == ProcessActionPreference.AutoStop
        ? "Recommended: Auto-stop"
        : "Recommended: Keep";

    public string ActionLabel => EffectiveAction == ProcessActionPreference.AutoStop ? "Auto-stop" : "Keep";

    public string ToggleActionLabel => EffectiveAction == ProcessActionPreference.AutoStop ? "Switch to keep" : "Switch to auto-stop";

    public string SourceLabel => EffectiveSource switch
    {
        ProcessPreferenceSource.Questionnaire => "Questionnaire",
        ProcessPreferenceSource.UserOverride => "Manual override",
        ProcessPreferenceSource.SystemDefault => "Default",
        _ => "Unknown"
    };

    public bool IsAutoStop => EffectiveAction == ProcessActionPreference.AutoStop;

    public bool CanControl => SupportsServiceControl && !IsActionInProgress;

    [ObservableProperty]
    private ProcessActionPreference _effectiveAction;

    [ObservableProperty]
    private ProcessPreferenceSource _effectiveSource;

    [ObservableProperty]
    private bool _isActionInProgress;

    [ObservableProperty]
    private string? _lastActionMessage;

    internal void ApplyPreference(ProcessActionPreference action, ProcessPreferenceSource source)
    {
        EffectiveAction = action;
        EffectiveSource = source;
    }

    [RelayCommand]
    private void ToggleAction()
    {
        _owner.ToggleAction(this);
    }

    [RelayCommand(CanExecute = nameof(CanControl))]
    private Task StopAsync()
    {
        return _owner.StopServiceAsync(this);
    }

    [RelayCommand(CanExecute = nameof(CanControl))]
    private Task RestartAsync()
    {
        return _owner.RestartServiceAsync(this);
    }

    [RelayCommand]
    private void LearnMore()
    {
        _owner.LearnMore(this);
    }

    partial void OnEffectiveActionChanged(ProcessActionPreference value)
    {
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(ToggleActionLabel));
        OnPropertyChanged(nameof(IsAutoStop));
    }

    partial void OnEffectiveSourceChanged(ProcessPreferenceSource value)
    {
        OnPropertyChanged(nameof(SourceLabel));
    }

    partial void OnIsActionInProgressChanged(bool value)
    {
        OnPropertyChanged(nameof(CanControl));
        StopCommand.NotifyCanExecuteChanged();
        RestartCommand.NotifyCanExecuteChanged();
    }
}
