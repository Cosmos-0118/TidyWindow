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

public sealed partial class ProcessPreferencesViewModel : ViewModelBase, IDisposable
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
    private readonly ProcessAutoStopEnforcer _autoStopEnforcer;
    private readonly ProcessControlService _processControlService;
    private readonly ServiceResolver _serviceResolver;
    private readonly IRelativeTimeTicker _relativeTimeTicker;
    private readonly IUserConfirmationService _confirmationService;
    private readonly ObservableCollection<ProcessPreferenceRowViewModel> _processEntries = new();
    private readonly ObservableCollection<ProcessPreferenceSegmentViewModel> _segments = new();
    private bool _hasPromptedFirstRunQuestionnaire;
    private bool _suspendAutomationStateUpdates;
    private bool _disposed;

    public ProcessPreferencesViewModel(
        MainViewModel mainViewModel,
        ProcessCatalogParser catalogParser,
        ProcessStateStore processStateStore,
        ProcessQuestionnaireEngine questionnaireEngine,
        ProcessAutoStopEnforcer autoStopEnforcer,
        ProcessControlService processControlService,
        ServiceResolver serviceResolver,
        IUserConfirmationService confirmationService,
        IRelativeTimeTicker relativeTimeTicker)
    {
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        _catalogParser = catalogParser ?? throw new ArgumentNullException(nameof(catalogParser));
        _processStateStore = processStateStore ?? throw new ArgumentNullException(nameof(processStateStore));
        _questionnaireEngine = questionnaireEngine ?? throw new ArgumentNullException(nameof(questionnaireEngine));
        _autoStopEnforcer = autoStopEnforcer ?? throw new ArgumentNullException(nameof(autoStopEnforcer));
        _processControlService = processControlService ?? throw new ArgumentNullException(nameof(processControlService));
        _serviceResolver = serviceResolver ?? throw new ArgumentNullException(nameof(serviceResolver));
        _confirmationService = confirmationService ?? throw new ArgumentNullException(nameof(confirmationService));
        _relativeTimeTicker = relativeTimeTicker ?? throw new ArgumentNullException(nameof(relativeTimeTicker));

        _autoStopEnforcer.SettingsChanged += OnAutoStopSettingsChanged;
        _autoStopEnforcer.GuardStatusUpdated += OnGuardStatusUpdated;
        _relativeTimeTicker.Tick += OnRelativeTimeTick;

        ProcessEntriesView = CollectionViewSource.GetDefaultView(_processEntries);
        ProcessEntriesView.Filter = FilterProcessEntry;

        AutoStopEntriesView = new ListCollectionView(_processEntries);
        AutoStopEntriesView.Filter = static item => item is ProcessPreferenceRowViewModel row && row.IsAutoStop;

        Segments = new ReadOnlyObservableCollection<ProcessPreferenceSegmentViewModel>(_segments);

        var existingSnapshot = _processStateStore.GetQuestionnaireSnapshot();
        _hasPromptedFirstRunQuestionnaire = existingSnapshot.CompletedAtUtc is not null;

        LoadAutomationSettings(_autoStopEnforcer.CurrentSettings);

        _ = RefreshProcessPreferencesAsync();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _autoStopEnforcer.SettingsChanged -= OnAutoStopSettingsChanged;
        _autoStopEnforcer.GuardStatusUpdated -= OnGuardStatusUpdated;
        _relativeTimeTicker.Tick -= OnRelativeTimeTick;
    }

    public ICollectionView ProcessEntriesView { get; }

    public ICollectionView AutoStopEntriesView { get; }

    public ReadOnlyObservableCollection<ProcessPreferenceSegmentViewModel> Segments { get; }

    // Smart Guard no longer exposes a user-configurable interval.

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

    [ObservableProperty]
    private bool _hasSegments;

    [ObservableProperty]
    private string _segmentSummary = "Loading catalog segments...";

    [ObservableProperty]
    private bool _isAutomationBusy;

    [ObservableProperty]
    private bool _isAutoStopAutomationEnabled;

    [ObservableProperty]
    private DateTimeOffset? _autoStopLastRunUtc;

    [ObservableProperty]
    private string _autoStopStatusMessage = "Smart Guard is disabled.";

    [ObservableProperty]
    private string _smartGuardDetail = string.Empty;

    [ObservableProperty]
    private int _smartGuardWatchedCount;

    [ObservableProperty]
    private int _smartGuardRunningCount;

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

    partial void OnIsAutoStopAutomationEnabledChanged(bool value)
    {
        if (_suspendAutomationStateUpdates)
        {
            return;
        }

        UpdateAutomationStatus();
    }

    partial void OnAutoStopLastRunUtcChanged(DateTimeOffset? value)
    {
        UpdateAutomationStatus();
    }

    [RelayCommand]
    public async Task RefreshProcessPreferencesAsync()
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
            RebuildSegments(snapshot, rows);
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

    [RelayCommand]
    private async Task ApplyAutoStopAutomationAsync()
    {
        if (IsAutomationBusy)
        {
            return;
        }

        var snapshot = BuildAutomationSettingsSnapshot();

        try
        {
            IsAutomationBusy = true;
            _mainViewModel.SetStatusMessage("Applying Smart Guard...");

            var result = await _autoStopEnforcer.ApplySettingsAsync(snapshot, enforceImmediately: true);

            if (result is ProcessAutoStopResult runResult && !runResult.WasSkipped)
            {
                AutoStopLastRunUtc = runResult.ExecutedAtUtc;
                _mainViewModel.LogActivityInformation("Smart Guard", $"Enforced for {runResult.TargetCount} service(s).");
            }
            else
            {
                var status = snapshot.AutoStopEnabled
                    ? "Smart Guard enabled — watching for target services."
                    : "Smart Guard disabled.";
                _mainViewModel.LogActivityInformation("Smart Guard", status);
            }
        }
        catch (Exception ex)
        {
            _mainViewModel.LogActivity(ActivityLogLevel.Error, "Smart Guard", "Failed to apply Smart Guard settings.", new[] { ex.Message });
        }
        finally
        {
            IsAutomationBusy = false;
            _mainViewModel.SetStatusMessage("Ready");
        }
    }

    [RelayCommand]
    private async Task RunAutoStopNowAsync()
    {
        if (IsAutomationBusy)
        {
            return;
        }

        try
        {
            IsAutomationBusy = true;
            var wasDisabled = !IsAutoStopAutomationEnabled;
            _mainViewModel.SetStatusMessage("Enforcing auto-stop preferences...");

            var result = await _autoStopEnforcer.RunOnceAsync(allowWhenDisabled: true);
            if (!result.WasSkipped)
            {
                AutoStopLastRunUtc = result.ExecutedAtUtc;
            }

            var message = result.WasSkipped
                ? "Smart Guard one-shot was skipped."
                : (result.TargetCount == 0
                    ? "No auto-stop targets required enforcement."
                    : $"Enforced for {result.TargetCount} service(s).");

            if (wasDisabled && !result.WasSkipped)
            {
                message += " Smart Guard remains disabled; this was a one-time run.";
            }

            _mainViewModel.LogActivityInformation("Smart Guard", message);
        }
        catch (Exception ex)
        {
            _mainViewModel.LogActivity(ActivityLogLevel.Error, "Smart Guard", "Failed to enforce auto-stop preferences.", new[] { ex.Message });
        }
        finally
        {
            IsAutomationBusy = false;
            _mainViewModel.SetStatusMessage("Ready");
        }
    }

    [RelayCommand]
    private void ShowThreatWatchHoldings()
    {
        var whitelist = _processStateStore.GetWhitelistEntries()
            .OrderByDescending(static entry => entry.AddedAtUtc)
            .Select(static entry => new ThreatWatchWhitelistEntryViewModel(
                entry.Id,
                entry.Kind,
                entry.Value,
                entry.Notes,
                entry.AddedBy,
                entry.AddedAtUtc))
            .ToList();

        var quarantine = _processStateStore.GetQuarantineEntries()
            .Select(static entry => new ThreatWatchQuarantineEntryViewModel(
                entry.Id,
                entry.ProcessName,
                entry.FilePath,
                entry.Notes,
                entry.AddedBy,
                entry.QuarantinedAtUtc,
                entry.Verdict,
                entry.VerdictSource,
                entry.VerdictDetails,
                entry.Sha256))
            .ToList();

        var dialogViewModel = new ThreatWatchHoldingsDialogViewModel(
            _processStateStore,
            _mainViewModel,
            _confirmationService,
            whitelist,
            quarantine);
        var window = new ThreatWatchHoldingsWindow(dialogViewModel)
        {
            Owner = WpfApplication.Current?.MainWindow,
            WindowStartupLocation = WpfApplication.Current?.MainWindow is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner
        };

        window.ShowDialog();
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
        await RunQuestionnaireFlowAsync(isAutoTrigger: false);
    }

    public async Task TriggerQuestionnaireIfFirstRunAsync()
    {
        if (_hasPromptedFirstRunQuestionnaire)
        {
            return;
        }

        var snapshot = _processStateStore.GetQuestionnaireSnapshot();
        if (snapshot.CompletedAtUtc is not null)
        {
            _hasPromptedFirstRunQuestionnaire = true;
            return;
        }

        _hasPromptedFirstRunQuestionnaire = true;
        await RunQuestionnaireFlowAsync(isAutoTrigger: true, snapshot);
    }

    private async Task RunQuestionnaireFlowAsync(bool isAutoTrigger, ProcessQuestionnaireSnapshot? snapshotOverride = null)
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

        var snapshot = snapshotOverride ?? _processStateStore.GetQuestionnaireSnapshot();
        var dialogViewModel = new ProcessQuestionnaireDialogViewModel(definition, snapshot);
        var window = new ProcessQuestionnaireWindow(dialogViewModel)
        {
            Owner = WpfApplication.Current?.MainWindow,
            WindowStartupLocation = WpfApplication.Current?.MainWindow is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner
        };

        var result = window.ShowDialog();
        if (result != true || dialogViewModel.Answers is null)
        {
            return;
        }

        try
        {
            IsProcessSettingsBusy = true;
            _mainViewModel.SetStatusMessage(isAutoTrigger ? "Applying questionnaire guidance..." : "Evaluating questionnaire...");

            await Task.Run(() => _questionnaireEngine.EvaluateAndApply(dialogViewModel.Answers));
            await RefreshProcessPreferencesAsync();
            _mainViewModel.LogActivityInformation(
                "Process settings",
                isAutoTrigger ? "First-run questionnaire answers applied." : "Questionnaire answers applied.");
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

    public void RefreshAutomationSettingsState()
    {
        LoadAutomationSettings(_autoStopEnforcer.CurrentSettings);
    }

    private ProcessAutomationSettings BuildAutomationSettingsSnapshot()
    {
        return new ProcessAutomationSettings(IsAutoStopAutomationEnabled, ProcessAutomationSettings.MinimumIntervalMinutes, AutoStopLastRunUtc);
    }

    private void OnAutoStopSettingsChanged(object? sender, ProcessAutomationSettings settings)
    {
        if (WpfApplication.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(new Action(() => ApplyAutomationSettingsUpdate(settings)));
            return;
        }

        ApplyAutomationSettingsUpdate(settings);
    }

    private void ApplyAutomationSettingsUpdate(ProcessAutomationSettings settings)
    {
        AutoStopLastRunUtc = settings.LastRunUtc;

        _suspendAutomationStateUpdates = true;
        IsAutoStopAutomationEnabled = settings.AutoStopEnabled;
        _suspendAutomationStateUpdates = false;

        UpdateAutomationStatus();
    }

    private void LoadAutomationSettings(ProcessAutomationSettings settings)
    {
        _suspendAutomationStateUpdates = true;
        IsAutoStopAutomationEnabled = settings.AutoStopEnabled;
        AutoStopLastRunUtc = settings.LastRunUtc;
        _suspendAutomationStateUpdates = false;
        UpdateAutomationStatus();
    }

    private void UpdateAutomationStatus()
    {
        if (!IsAutoStopAutomationEnabled)
        {
            AutoStopStatusMessage = "Smart Guard is disabled.";
            SmartGuardDetail = string.Empty;
            return;
        }

        var lastRunLabel = AutoStopLastRunUtc is null
            ? "No actions taken yet."
            : $"Last action {FormatRelative(AutoStopLastRunUtc.Value)}.";

        var watchLabel = SmartGuardWatchedCount switch
        {
            0 => "No services configured.",
            1 => "Watching 1 service.",
            _ => $"Watching {SmartGuardWatchedCount} services."
        };

        AutoStopStatusMessage = $"Smart Guard active \u2014 {watchLabel}";
        SmartGuardDetail = SmartGuardRunningCount > 0
            ? $"{SmartGuardRunningCount} running (will be stopped). {lastRunLabel}"
            : $"All quiet \u2014 no targets running. {lastRunLabel}";
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

        return timestamp.ToLocalTime().ToString("g");
    }

    private void OnRelativeTimeTick(object? sender, EventArgs e)
    {
        UpdateAutomationStatus();
    }

    private void OnGuardStatusUpdated(object? sender, SmartGuardStatus status)
    {
        if (WpfApplication.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(new Action(() => ApplyGuardStatus(status)));
            return;
        }

        ApplyGuardStatus(status);
    }

    private void ApplyGuardStatus(SmartGuardStatus status)
    {
        SmartGuardWatchedCount = status.WatchedServices;
        SmartGuardRunningCount = status.RunningServices;
        if (status.LastActionUtc is not null)
        {
            AutoStopLastRunUtc = status.LastActionUtc;
        }

        UpdateAutomationStatus();
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

    private void RebuildSegments(ProcessCatalogSnapshot snapshot, IReadOnlyList<ProcessPreferenceRowViewModel> rows)
    {
        _segments.Clear();

        var groupedRows = rows
            .GroupBy(row => row.CategoryKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var category in snapshot.Categories.OrderBy(static cat => cat.Order))
        {
            if (!groupedRows.TryGetValue(category.Key, out var categoryRows) || categoryRows.Count == 0)
            {
                continue;
            }

            var segment = new ProcessPreferenceSegmentViewModel(this, category, categoryRows);
            segment.RefreshCounts();
            _segments.Add(segment);
            groupedRows.Remove(category.Key);
        }

        foreach (var leftover in groupedRows.Values)
        {
            if (leftover.Count == 0)
            {
                continue;
            }

            var fallbackMetadata = new ProcessCatalogCategory(
                leftover[0].CategoryKey,
                leftover[0].CategoryName,
                leftover[0].CategoryDescription,
                leftover[0].IsCaution,
                int.MaxValue);

            var segment = new ProcessPreferenceSegmentViewModel(this, fallbackMetadata, leftover);
            segment.RefreshCounts();
            _segments.Add(segment);
        }

        UpdateSegmentSummaries();
    }

    private void UpdateSegmentSummaries()
    {
        foreach (var segment in _segments)
        {
            segment.RefreshCounts();
        }

        HasSegments = _segments.Count > 0;
        SegmentSummary = HasSegments
            ? $"Quick toggles ready for {_segments.Count} segments."
            : "No catalog segments available.";
    }

    internal void ApplySegmentPreference(ProcessPreferenceSegmentViewModel segment, ProcessActionPreference action)
    {
        if (segment is null)
        {
            return;
        }

        var targets = segment.Rows
            .Where(row => row.EffectiveAction != action)
            .ToList();

        if (targets.Count == 0)
        {
            segment.RefreshCounts();
            return;
        }

        try
        {
            foreach (var row in targets)
            {
                var preference = new ProcessPreference(
                    row.Identifier,
                    action,
                    ProcessPreferenceSource.UserOverride,
                    DateTimeOffset.UtcNow,
                    $"Segment '{segment.Title}' quick toggle",
                    row.ServiceIdentifier);
                _processStateStore.UpsertPreference(preference);
                row.ApplyPreference(preference.Action, preference.Source, preference.UpdatedAtUtc, preference.Notes);
            }

            ProcessEntriesView.Refresh();
            AutoStopEntriesView.Refresh();
            UpdateProcessSummaries();
            UpdateSegmentSummaries();
            _mainViewModel.LogActivityInformation("Process settings", $"Segment '{segment.Title}' set to {(action == ProcessActionPreference.AutoStop ? "auto-stop" : "keep")} ({targets.Count} processes).");
        }
        catch (Exception ex)
        {
            _mainViewModel.LogActivity(ActivityLogLevel.Error, "Process settings", $"Unable to update segment '{segment.Title}'.", new[] { ex.Message });
        }
    }

    private void ApplyProcessPreference(ProcessPreferenceRowViewModel row, ProcessActionPreference action)
    {
        try
        {
            var preference = new ProcessPreference(
                row.Identifier,
                action,
                ProcessPreferenceSource.UserOverride,
                DateTimeOffset.UtcNow,
                "Updated via Processes settings",
                row.ServiceIdentifier);
            _processStateStore.UpsertPreference(preference);
            row.ApplyPreference(preference.Action, preference.Source, preference.UpdatedAtUtc, preference.Notes);
            ProcessEntriesView.Refresh();
            AutoStopEntriesView.Refresh();
            UpdateProcessSummaries();
            UpdateSegmentSummaries();
            _mainViewModel.LogActivityInformation("Process settings", $"{row.DisplayName} set to {row.StatusLabel}.");

            // When switching away from AutoStop, re-enable the service so it can run normally
            // and clear the guard's stop-history (so it gets a fresh log entry if re-added later).
            if (action != ProcessActionPreference.AutoStop)
            {
                _ = TryReEnableServiceAsync(row.Identifier, row.ServiceIdentifier, row.DisplayName);
            }
        }
        catch (Exception ex)
        {
            _mainViewModel.LogActivity(ActivityLogLevel.Error, "Process settings", $"Failed to update {row.DisplayName}.", new[] { ex.Message });
        }
    }

    /// <summary>
    /// Re-enables a service that was previously disabled by Smart Guard.
    /// </summary>
    private async Task TryReEnableServiceAsync(string processIdentifier, string? serviceIdentifier, string displayName)
    {
        try
        {
            var rawId = serviceIdentifier ?? processIdentifier;
            if (string.IsNullOrWhiteSpace(rawId) || rawId.Contains('\\') || rawId.Contains('/'))
            {
                return; // Task entries; not a service.
            }

            var resolution = _serviceResolver.ResolveMany(rawId, displayName);
            if (resolution.Status != ServiceResolutionStatus.Available || resolution.Candidates.Count == 0)
            {
                return;
            }

            foreach (var candidate in resolution.Candidates)
            {
                var result = await _processControlService.EnableAsync(candidate.ServiceName).ConfigureAwait(false);
                if (result.Success)
                {
                    _autoStopEnforcer.ClearStopHistory(candidate.ServiceName);
                    _mainViewModel.LogActivityInformation("Smart Guard", $"Re-enabled {displayName} ({candidate.ServiceName}).");
                }
            }
        }
        catch (Exception ex)
        {
            _mainViewModel.LogActivity(ActivityLogLevel.Warning, "Smart Guard", $"Could not re-enable {displayName}.", new[] { ex.Message });
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

public sealed partial class ProcessPreferenceSegmentViewModel : ObservableObject
{
    private readonly ProcessPreferencesViewModel _owner;
    private readonly IReadOnlyList<ProcessPreferenceRowViewModel> _rows;

    internal ProcessPreferenceSegmentViewModel(ProcessPreferencesViewModel owner, ProcessCatalogCategory metadata, IReadOnlyList<ProcessPreferenceRowViewModel> rows)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _rows = rows ?? throw new ArgumentNullException(nameof(rows));
        Key = metadata?.Key ?? throw new ArgumentNullException(nameof(metadata));
        Title = string.IsNullOrWhiteSpace(metadata.Name) ? Key : metadata.Name;
        Description = metadata.Description;
        IsCaution = metadata.IsCaution;
        DisplayOrder = metadata.Order;
        _autoStopCount = rows.Count(static row => row.IsAutoStop);
    }

    public string Key { get; }

    public string Title { get; }

    public string? Description { get; }

    public bool IsCaution { get; }

    public int DisplayOrder { get; }

    public int TotalCount => _rows.Count;

    public bool HasProcesses => TotalCount > 0;

    [ObservableProperty]
    private int _autoStopCount;

    public string Summary => !HasProcesses
        ? "No catalog entries"
        : $"{AutoStopCount} of {TotalCount} auto-stop";

    public bool IsMixed => HasProcesses && AutoStopCount > 0 && AutoStopCount < TotalCount;

    public string StateLabel => !HasProcesses
        ? "No catalog entries"
        : IsMixed
            ? "Mixed preferences"
            : AutoStopCount == TotalCount
                ? "Auto-stopping all"
                : "Keeping all";

    public bool SegmentToggleValue
    {
        get => HasProcesses && AutoStopCount == TotalCount;
        set
        {
            if (!HasProcesses)
            {
                return;
            }

            var targetAction = value ? ProcessActionPreference.AutoStop : ProcessActionPreference.Keep;
            _owner.ApplySegmentPreference(this, targetAction);
        }
    }

    internal IReadOnlyList<ProcessPreferenceRowViewModel> Rows => _rows;

    internal void RefreshCounts()
    {
        AutoStopCount = _rows.Count(static row => row.IsAutoStop);
    }

    partial void OnAutoStopCountChanged(int value)
    {
        OnPropertyChanged(nameof(SegmentToggleValue));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(IsMixed));
    }
}
