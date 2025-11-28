using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.Core.Processes;

namespace TidyWindow.App.ViewModels;

internal sealed partial class ProcessPreferenceRowViewModel : ObservableObject
{
    private readonly Action<ProcessPreferenceRowViewModel> _toggleAction;
    private readonly string _searchContext;

    public ProcessPreferenceRowViewModel(
        Action<ProcessPreferenceRowViewModel> toggleAction,
        ProcessCatalogEntry entry,
        ProcessPreference? preference)
        : this(
            toggleAction,
            entry?.Identifier ?? throw new ArgumentNullException(nameof(entry)),
            entry.DisplayName,
            entry.CategoryName,
            entry.CategoryDescription,
            entry.RiskLevel == ProcessRiskLevel.Caution,
            entry.RecommendedAction,
            entry.Rationale ?? entry.CategoryDescription ?? "No rationale provided.",
            preference?.Action ?? entry.RecommendedAction,
            preference?.Source ?? ProcessPreferenceSource.SystemDefault,
            preference?.UpdatedAtUtc ?? DateTimeOffset.MinValue,
            preference?.Notes,
            isCatalogEntry: true)
    {
    }

    private ProcessPreferenceRowViewModel(
        Action<ProcessPreferenceRowViewModel> toggleAction,
        string identifier,
        string displayName,
        string categoryName,
        string? categoryDescription,
        bool isCaution,
        ProcessActionPreference recommendedAction,
        string rationale,
        ProcessActionPreference effectiveAction,
        ProcessPreferenceSource effectiveSource,
        DateTimeOffset updatedAtUtc,
        string? notes,
        bool isCatalogEntry)
    {
        _toggleAction = toggleAction ?? throw new ArgumentNullException(nameof(toggleAction));
        Identifier = ProcessCatalogEntry.NormalizeIdentifier(identifier);
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? Identifier : displayName;
        CategoryName = string.IsNullOrWhiteSpace(categoryName) ? "General" : categoryName;
        CategoryDescription = categoryDescription;
        IsCaution = isCaution;
        RecommendedAction = recommendedAction;
        Rationale = string.IsNullOrWhiteSpace(rationale) ? "No rationale provided." : rationale.Trim();
        IsCatalogEntry = isCatalogEntry;

        _searchContext = string.Join(' ', Identifier, DisplayName, CategoryName, Rationale).ToLowerInvariant();

        _effectiveAction = effectiveAction;
        _effectiveSource = effectiveSource;
        _updatedAtUtc = updatedAtUtc;
        _notes = notes;
    }

    public string Identifier { get; }

    public string DisplayName { get; }

    public string CategoryName { get; }

    public string? CategoryDescription { get; }

    public bool IsCaution { get; }

    public bool IsCatalogEntry { get; }

    public ProcessActionPreference RecommendedAction { get; }

    public string Rationale { get; }

    public string RecommendationLabel => RecommendedAction == ProcessActionPreference.AutoStop
        ? "Recommended: Auto-stop"
        : "Recommended: Keep";

    public string StatusLabel => EffectiveAction == ProcessActionPreference.AutoStop ? "Auto-stop" : "Keep";

    public string SourceLabel => EffectiveSource switch
    {
        ProcessPreferenceSource.Questionnaire => "Questionnaire",
        ProcessPreferenceSource.UserOverride => "Manual override",
        ProcessPreferenceSource.SystemDefault => "Default",
        _ => EffectiveSource.ToString()
    };

    public string UpdatedLabel => UpdatedAtUtc == DateTimeOffset.MinValue
        ? "Not configured"
        : UpdatedAtUtc.ToLocalTime().ToString("g");

    public bool IsAutoStop => EffectiveAction == ProcessActionPreference.AutoStop;

    public string ToggleActionLabel => IsAutoStop ? "Switch to keep" : "Switch to auto-stop";

    [ObservableProperty]
    private ProcessActionPreference _effectiveAction;

    [ObservableProperty]
    private ProcessPreferenceSource _effectiveSource;

    [ObservableProperty]
    private DateTimeOffset _updatedAtUtc;

    [ObservableProperty]
    private string? _notes;

    public bool MatchesFilter(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        var normalized = filter.Trim().ToLowerInvariant();
        return _searchContext.Contains(normalized, StringComparison.Ordinal)
               || (!string.IsNullOrWhiteSpace(Notes) && Notes!.ToLowerInvariant().Contains(normalized, StringComparison.Ordinal));
    }

    public void ApplyPreference(ProcessActionPreference action, ProcessPreferenceSource source, DateTimeOffset updatedAtUtc, string? notes)
    {
        EffectiveAction = action;
        EffectiveSource = source;
        UpdatedAtUtc = updatedAtUtc;
        Notes = notes;
    }

    public static ProcessPreferenceRowViewModel CreateOrphan(Action<ProcessPreferenceRowViewModel> toggleAction, ProcessPreference preference)
    {
        if (preference is null)
        {
            throw new ArgumentNullException(nameof(preference));
        }

        return new ProcessPreferenceRowViewModel(
            toggleAction,
            preference.ProcessIdentifier,
            preference.ProcessIdentifier,
            "Custom",
            "Imported preference",
            isCaution: false,
            recommendedAction: ProcessActionPreference.Keep,
            "Preference imported for a process that is not currently in the catalog.",
            preference.Action,
            preference.Source,
            preference.UpdatedAtUtc,
            preference.Notes,
            isCatalogEntry: false);
    }

    [RelayCommand]
    private void TogglePreference()
    {
        _toggleAction(this);
    }

    partial void OnEffectiveActionChanged(ProcessActionPreference value)
    {
        OnPropertyChanged(nameof(IsAutoStop));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(ToggleActionLabel));
    }
}
