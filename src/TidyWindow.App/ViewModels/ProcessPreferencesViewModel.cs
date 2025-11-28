using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TidyWindow.App.Models;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels.Dialogs;
using TidyWindow.App.Views.Dialogs;
using TidyWindow.Core.Processes;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using WpfApplication = System.Windows.Application;

namespace TidyWindow.App.ViewModels;

public sealed partial class ProcessPreferencesViewModel : ViewModelBase
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly MainViewModel _mainViewModel;
    private readonly ProcessCatalogParser _catalogParser;
    private readonly ProcessStateStore _processStateStore;
    private readonly ProcessQuestionnaireEngine _questionnaireEngine;
    private readonly IUserConfirmationService _confirmationService;
    private readonly ObservableCollection<ProcessPreferenceRowViewModel> _processEntries = new();

    public ProcessPreferencesViewModel(
        MainViewModel mainViewModel,
        ProcessCatalogParser catalogParser,
        ProcessStateStore processStateStore,
        ProcessQuestionnaireEngine questionnaireEngine,
        IUserConfirmationService confirmationService)
    {
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        _catalogParser = catalogParser ?? throw new ArgumentNullException(nameof(catalogParser));
        _processStateStore = processStateStore ?? throw new ArgumentNullException(nameof(processStateStore));
        _questionnaireEngine = questionnaireEngine ?? throw new ArgumentNullException(nameof(questionnaireEngine));
        _confirmationService = confirmationService ?? throw new ArgumentNullException(nameof(confirmationService));

        ProcessEntriesView = CollectionViewSource.GetDefaultView(_processEntries);
        ProcessEntriesView.Filter = FilterProcessEntry;

        AutoStopEntriesView = new ListCollectionView(_processEntries);
        AutoStopEntriesView.Filter = static item => item is ProcessPreferenceRowViewModel row && row.IsAutoStop;

        _ = RefreshProcessPreferencesAsync();
    }

    public ICollectionView ProcessEntriesView { get; }

    public ICollectionView AutoStopEntriesView { get; }

    [ObservableProperty]
    private bool _isProcessSettingsBusy;

    [ObservableProperty]
    private string _processSummary = "Loading process catalog...";

    [ObservableProperty]
    private string _questionnaireSummary = "Questionnaire has not been completed yet.";

    [ObservableProperty]
    private string _autoStopSummary = "No processes configured to auto-stop.";

    [ObservableProperty]
    private bool _hasAutoStopEntries;

    [ObservableProperty]
    private bool _isAutoStopPanelVisible;

    [ObservableProperty]
    private string _processFilterText = string.Empty;

    [ObservableProperty]
    private bool _showAutoStopOnly;

    partial void OnProcessFilterTextChanged(string value)
    {
        ProcessEntriesView.Refresh();
    }

    partial void OnShowAutoStopOnlyChanged(bool value)
    {
        ProcessEntriesView.Refresh();
    }

    partial void OnHasAutoStopEntriesChanged(bool value)
    {
        if (!value)
        {
            IsAutoStopPanelVisible = false;
        }
    }

    [RelayCommand]
    private async Task RefreshProcessPreferencesAsync()
    {
        if (IsProcessSettingsBusy)
        {
            return;
        }

        try
        {
            IsProcessSettingsBusy = true;
            _mainViewModel.SetStatusMessage("Loading process catalog...");

            var snapshot = await Task.Run(() => _catalogParser.LoadSnapshot());
            var preferences = _processStateStore.GetPreferences();
            var rows = BuildProcessRows(snapshot, preferences);

            _processEntries.Clear();
            foreach (var row in rows)
            {
                _processEntries.Add(row);
            }

            ProcessEntriesView.Refresh();
            AutoStopEntriesView.Refresh();
            UpdateProcessSummaries();
        }
        catch (Exception ex)
        {
            _mainViewModel.LogActivity(ActivityLogLevel.Error, "Process settings", "Failed to load process catalog.", new[] { ex.Message });
            ProcessSummary = "Unable to load process catalog.";
        }
        finally
        {
            IsProcessSettingsBusy = false;
            _mainViewModel.SetStatusMessage("Ready");
        }
    }

    [RelayCommand]
    private void ToggleAutoStopPanel()
    {
        IsAutoStopPanelVisible = !IsAutoStopPanelVisible;
    }

    private void ToggleProcessPreference(ProcessPreferenceRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var nextAction = row.IsAutoStop ? ProcessActionPreference.Keep : ProcessActionPreference.AutoStop;
        ApplyProcessPreference(row, nextAction);
    }

    [RelayCommand]
    private async Task ResetProcessPreferencesAsync()
    {
        if (!_confirmationService.Confirm("Reset process settings", "This clears questionnaire answers and removes all overrides. Do you want to continue?"))
        {
            return;
        }

        try
        {
            IsProcessSettingsBusy = true;
            _mainViewModel.SetStatusMessage("Resetting process settings...");

            foreach (var preference in _processStateStore.GetPreferences().ToArray())
            {
                _processStateStore.RemovePreference(preference.ProcessIdentifier);
            }

            _processStateStore.SaveQuestionnaireSnapshot(ProcessQuestionnaireSnapshot.Empty);
            await RefreshProcessPreferencesAsync();
            _mainViewModel.LogActivityInformation("Process settings", "Process preferences reset to defaults.");
        }
        catch (Exception ex)
        {
            _mainViewModel.LogActivity(ActivityLogLevel.Error, "Process settings", "Unable to reset preferences.", new[] { ex.Message });
        }
        finally
        {
            IsProcessSettingsBusy = false;
            _mainViewModel.SetStatusMessage("Ready");
        }
    }

    [RelayCommand]
    private async Task ExportProcessSettingsAsync()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            FileName = "tidywindow-process-settings.json"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            IsProcessSettingsBusy = true;
            _mainViewModel.SetStatusMessage("Exporting process settings...");

            var snapshot = _processStateStore.GetSnapshot();
            var model = ProcessSettingsPortableModel.FromSnapshot(snapshot);
            var json = JsonSerializer.Serialize(model, SerializerOptions);
            await File.WriteAllTextAsync(dialog.FileName, json);
            _mainViewModel.LogActivityInformation("Process settings", $"Exported {model.Preferences.Count} preferences to '{dialog.FileName}'.");
        }
        catch (Exception ex)
        {
            _mainViewModel.LogActivity(ActivityLogLevel.Error, "Process settings", "Export failed.", new[] { ex.Message });
        }
        finally
        {
            IsProcessSettingsBusy = false;
            _mainViewModel.SetStatusMessage("Ready");
        }
    }

    [RelayCommand]
    private async Task ImportProcessSettingsAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            IsProcessSettingsBusy = true;
            _mainViewModel.SetStatusMessage("Importing process settings...");

            var json = await File.ReadAllTextAsync(dialog.FileName);
            var model = JsonSerializer.Deserialize<ProcessSettingsPortableModel>(json, SerializerOptions);
            if (model is null)
            {
                throw new InvalidOperationException("Invalid settings file.");
            }

            foreach (var preference in _processStateStore.GetPreferences().ToArray())
            {
                _processStateStore.RemovePreference(preference.ProcessIdentifier);
            }

            foreach (var preference in model.ToPreferences())
            {
                _processStateStore.UpsertPreference(preference);
            }

            var questionnaire = model.ToQuestionnaireSnapshot();
            _processStateStore.SaveQuestionnaireSnapshot(questionnaire);

            await RefreshProcessPreferencesAsync();
            _mainViewModel.LogActivityInformation("Process settings", $"Imported {model.Preferences.Count} preferences from '{dialog.FileName}'.");
        }
        catch (Exception ex)
        {
            _mainViewModel.LogActivity(ActivityLogLevel.Error, "Process settings", "Import failed.", new[] { ex.Message });
        }
        finally
        {
            IsProcessSettingsBusy = false;
            _mainViewModel.SetStatusMessage("Ready");
        }
    }

    [RelayCommand]
    private async Task RerunQuestionnaireAsync()
    {
        if (IsProcessSettingsBusy)
        {
            return;
        }

        var definition = _questionnaireEngine.GetDefinition();
        if (definition.Questions.Count == 0)
        {
            _mainViewModel.LogActivityInformation("Process settings", "Questionnaire definition is empty.");
            return;
        }

        var snapshot = _processStateStore.GetQuestionnaireSnapshot();
        var dialogViewModel = new ProcessQuestionnaireDialogViewModel(definition, snapshot);
        var window = new ProcessQuestionnaireWindow(dialogViewModel)
        {
            Owner = WpfApplication.Current?.MainWindow,
            WindowStartupLocation = WpfApplication.Current?.MainWindow is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner
        };

        var result = window.ShowDialog();
        if (result != true || dialogViewModel.Answers is null)
        {
            return;
        }

        try
        {
            IsProcessSettingsBusy = true;
            _mainViewModel.SetStatusMessage("Evaluating questionnaire...");

            await Task.Run(() => _questionnaireEngine.EvaluateAndApply(dialogViewModel.Answers));
            await RefreshProcessPreferencesAsync();
            _mainViewModel.LogActivityInformation("Process settings", "Questionnaire answers applied.");
        }
        catch (Exception ex)
        {
            _mainViewModel.LogActivity(ActivityLogLevel.Error, "Process settings", "Questionnaire evaluation failed.", new[] { ex.Message });
        }
        finally
        {
            IsProcessSettingsBusy = false;
            _mainViewModel.SetStatusMessage("Ready");
        }
    }

    private bool FilterProcessEntry(object item)
    {
        if (item is not ProcessPreferenceRowViewModel row)
        {
            return false;
        }

        if (ShowAutoStopOnly && !row.IsAutoStop)
        {
            return false;
        }

        return row.MatchesFilter(ProcessFilterText);
    }

    private List<ProcessPreferenceRowViewModel> BuildProcessRows(ProcessCatalogSnapshot snapshot, IReadOnlyCollection<ProcessPreference> preferences)
    {
        var lookup = preferences
            .GroupBy(pref => pref.ProcessIdentifier)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

        var rows = new List<ProcessPreferenceRowViewModel>(snapshot.Entries.Count + lookup.Count);
        var toggleAction = new Action<ProcessPreferenceRowViewModel>(ToggleProcessPreference);
        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in snapshot.Entries)
        {
            lookup.TryGetValue(entry.Identifier, out var preference);
            var row = new ProcessPreferenceRowViewModel(toggleAction, entry, preference);
            rows.Add(row);
            included.Add(entry.Identifier);
        }

        foreach (var preference in lookup.Values)
        {
            if (included.Contains(preference.ProcessIdentifier))
            {
                continue;
            }

            rows.Add(ProcessPreferenceRowViewModel.CreateOrphan(toggleAction, preference));
        }

        return rows
            .OrderBy(row => row.CategoryName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void ApplyProcessPreference(ProcessPreferenceRowViewModel row, ProcessActionPreference action)
    {
        try
        {
            var preference = new ProcessPreference(row.Identifier, action, ProcessPreferenceSource.UserOverride, DateTimeOffset.UtcNow, "Updated via Processes settings");
            _processStateStore.UpsertPreference(preference);
            row.ApplyPreference(preference.Action, preference.Source, preference.UpdatedAtUtc, preference.Notes);
            ProcessEntriesView.Refresh();
            AutoStopEntriesView.Refresh();
            UpdateProcessSummaries();
            _mainViewModel.LogActivityInformation("Process settings", $"{row.DisplayName} set to {row.StatusLabel}.");
        }
        catch (Exception ex)
        {
            _mainViewModel.LogActivity(ActivityLogLevel.Error, "Process settings", $"Failed to update {row.DisplayName}.", new[] { ex.Message });
        }
    }

    private void UpdateProcessSummaries()
    {
        if (_processEntries.Count == 0)
        {
            ProcessSummary = "Process catalog not loaded yet.";
            AutoStopSummary = "No processes configured to auto-stop.";
            QuestionnaireSummary = FormatQuestionnaireSummary(_processStateStore.GetQuestionnaireSnapshot());
            HasAutoStopEntries = false;
            return;
        }

        var autoStopCount = _processEntries.Count(row => row.IsAutoStop);
        ProcessSummary = autoStopCount switch
        {
            0 => $"{_processEntries.Count} processes loaded. None set to auto-stop.",
            1 => $"1 of {_processEntries.Count} processes set to auto-stop.",
            _ => $"{autoStopCount} of {_processEntries.Count} processes set to auto-stop."
        };

        AutoStopSummary = autoStopCount switch
        {
            0 => "No processes configured to auto-stop.",
            1 => "1 process will auto-stop when automation runs.",
            _ => $"{autoStopCount} processes will auto-stop when automation runs."
        };

        HasAutoStopEntries = autoStopCount > 0;
        QuestionnaireSummary = FormatQuestionnaireSummary(_processStateStore.GetQuestionnaireSnapshot());
    }

    private static string FormatQuestionnaireSummary(ProcessQuestionnaireSnapshot snapshot)
    {
        if (snapshot.CompletedAtUtc is null)
        {
            return "Questionnaire has not been completed yet.";
        }

        var completed = snapshot.CompletedAtUtc.Value.ToLocalTime();
        return $"Questionnaire last completed on {completed:G}.";
    }
}
