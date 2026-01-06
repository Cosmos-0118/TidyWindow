using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels;
using TidyWindow.Core.Processes;
using TidyWindow.Core.Processes.ThreatWatch;

namespace TidyWindow.App.ViewModels.Dialogs;

public sealed partial class ThreatWatchHoldingsDialogViewModel : ObservableObject
{
    private readonly ProcessStateStore _stateStore;
    private readonly MainViewModel _mainViewModel;
    private readonly IUserConfirmationService _confirmationService;

    public ThreatWatchHoldingsDialogViewModel(
        ProcessStateStore stateStore,
        MainViewModel mainViewModel,
        IUserConfirmationService confirmationService,
        IEnumerable<ThreatWatchWhitelistEntryViewModel>? whitelist,
        IEnumerable<ThreatWatchQuarantineEntryViewModel>? quarantine)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        _confirmationService = confirmationService ?? throw new ArgumentNullException(nameof(confirmationService));
        WhitelistEntries = new ObservableCollection<ThreatWatchWhitelistEntryViewModel>(whitelist ?? Enumerable.Empty<ThreatWatchWhitelistEntryViewModel>());
        QuarantineEntries = new ObservableCollection<ThreatWatchQuarantineEntryViewModel>(quarantine ?? Enumerable.Empty<ThreatWatchQuarantineEntryViewModel>());

        WhitelistEntries.CollectionChanged += (_, __) => OnPropertyChanged(nameof(HasWhitelistEntries));
        QuarantineEntries.CollectionChanged += (_, __) => OnPropertyChanged(nameof(HasQuarantineEntries));
    }

    public ObservableCollection<ThreatWatchWhitelistEntryViewModel> WhitelistEntries { get; }

    public ObservableCollection<ThreatWatchQuarantineEntryViewModel> QuarantineEntries { get; }

    public bool HasWhitelistEntries => WhitelistEntries.Count > 0;

    public bool HasQuarantineEntries => QuarantineEntries.Count > 0;

    [RelayCommand]
    private void RemoveWhitelistEntry(ThreatWatchWhitelistEntryViewModel? entry)
    {
        if (entry is null)
        {
            return;
        }

        if (!_confirmationService.Confirm("Remove whitelist entry", $"Stop ignoring '{entry.Value}'?"))
        {
            return;
        }

        try
        {
            if (_stateStore.RemoveWhitelistEntry(entry.Id))
            {
                WhitelistEntries.Remove(entry);
                _mainViewModel.LogActivityInformation("Threat Watch", $"Removed whitelist entry for {entry.Value}.");
            }
            else
            {
                _mainViewModel.LogActivity(ActivityLogLevel.Warning, "Threat Watch", "Whitelist entry could not be removed.", new[] { entry.Value });
            }
        }
        catch (Exception ex)
        {
            _mainViewModel.LogActivity(ActivityLogLevel.Error, "Threat Watch", "Failed to remove whitelist entry.", new[] { ex.Message });
        }
    }

    [RelayCommand]
    private void RemoveQuarantineEntry(ThreatWatchQuarantineEntryViewModel? entry)
    {
        if (entry is null)
        {
            return;
        }

        if (!_confirmationService.Confirm("Remove quarantine record", $"Delete the quarantine log for '{entry.ProcessName}'?"))
        {
            return;
        }

        try
        {
            if (_stateStore.RemoveQuarantineEntry(entry.Id))
            {
                QuarantineEntries.Remove(entry);
                _mainViewModel.LogActivityInformation("Threat Watch", $"Removed quarantine record for {entry.ProcessName}.");
            }
            else
            {
                _mainViewModel.LogActivity(ActivityLogLevel.Warning, "Threat Watch", "Quarantine record could not be removed.", new[] { entry.ProcessName });
            }
        }
        catch (Exception ex)
        {
            _mainViewModel.LogActivity(ActivityLogLevel.Error, "Threat Watch", "Failed to remove quarantine record.", new[] { ex.Message });
        }
    }
}

public sealed class ThreatWatchWhitelistEntryViewModel
{
    public ThreatWatchWhitelistEntryViewModel(
        string id,
        ThreatWatchWhitelistEntryKind kind,
        string value,
        string? notes,
        string? addedBy,
        DateTimeOffset addedAtUtc)
    {
        Id = id;
        Kind = kind;
        Value = value;
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        AddedBy = string.IsNullOrWhiteSpace(addedBy) ? "TidyWindow" : addedBy.Trim();
        AddedAtUtc = addedAtUtc == default ? DateTimeOffset.UtcNow : addedAtUtc;
    }

    public string Id { get; }

    public ThreatWatchWhitelistEntryKind Kind { get; }

    public string Value { get; }

    public string? Notes { get; }

    public string AddedBy { get; }

    public DateTimeOffset AddedAtUtc { get; }

    public string KindLabel => Kind switch
    {
        ThreatWatchWhitelistEntryKind.Directory => "Directory",
        ThreatWatchWhitelistEntryKind.FileHash => "File hash",
        ThreatWatchWhitelistEntryKind.ProcessName => "Process name",
        _ => Kind.ToString()
    };

    public string AddedAtDisplay => AddedAtUtc.ToLocalTime().ToString("g");
}

public sealed class ThreatWatchQuarantineEntryViewModel
{
    public ThreatWatchQuarantineEntryViewModel(
        string id,
        string processName,
        string filePath,
        string? notes,
        string? addedBy,
        DateTimeOffset quarantinedAtUtc,
        ThreatIntelVerdict? verdict,
        string? verdictSource,
        string? verdictDetails,
        string? sha256)
    {
        Id = id;
        ProcessName = string.IsNullOrWhiteSpace(processName) ? "Unknown" : processName.Trim();
        FilePath = filePath;
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        AddedBy = string.IsNullOrWhiteSpace(addedBy) ? "TidyWindow" : addedBy.Trim();
        QuarantinedAtUtc = quarantinedAtUtc == default ? DateTimeOffset.UtcNow : quarantinedAtUtc;
        Verdict = verdict;
        VerdictSource = string.IsNullOrWhiteSpace(verdictSource) ? null : verdictSource.Trim();
        VerdictDetails = string.IsNullOrWhiteSpace(verdictDetails) ? null : verdictDetails.Trim();
        Sha256 = string.IsNullOrWhiteSpace(sha256) ? null : sha256.Trim();
    }

    public string Id { get; }

    public string ProcessName { get; }

    public string FilePath { get; }

    public string? Notes { get; }

    public string AddedBy { get; }

    public DateTimeOffset QuarantinedAtUtc { get; }

    public string FileName => Path.GetFileName(FilePath);

    public string QuarantinedAtDisplay => QuarantinedAtUtc.ToLocalTime().ToString("g");

    public ThreatIntelVerdict? Verdict { get; }

    public string? VerdictSource { get; }

    public string? VerdictDetails { get; }

    public string? Sha256 { get; }

    public bool HasVerdict => Verdict is not null;

    public bool HasVerdictDetails => !string.IsNullOrWhiteSpace(VerdictDetails);

    public bool HasSha256 => !string.IsNullOrWhiteSpace(Sha256);

    public string? VerdictSummary
    {
        get
        {
            if (Verdict is null)
            {
                return null;
            }

            var summary = Verdict switch
            {
                ThreatIntelVerdict.KnownBad => "Defender flagged this file as malicious",
                ThreatIntelVerdict.KnownGood => "Defender marked this file as trusted",
                _ => "Defender had no opinion about this file"
            };

            return VerdictSource is null
                ? summary + "."
                : $"{summary} ({VerdictSource}).";
        }
    }
}
