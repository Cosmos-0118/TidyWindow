using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using TidyWindow.Core.Processes;

namespace TidyWindow.App.ViewModels.Dialogs;

public sealed partial class AntiSystemHoldingsDialogViewModel : ObservableObject
{
    public AntiSystemHoldingsDialogViewModel(
        IEnumerable<AntiSystemWhitelistEntryViewModel>? whitelist,
        IEnumerable<AntiSystemQuarantineEntryViewModel>? quarantine)
    {
        WhitelistEntries = new ObservableCollection<AntiSystemWhitelistEntryViewModel>(whitelist ?? Enumerable.Empty<AntiSystemWhitelistEntryViewModel>());
        QuarantineEntries = new ObservableCollection<AntiSystemQuarantineEntryViewModel>(quarantine ?? Enumerable.Empty<AntiSystemQuarantineEntryViewModel>());

        WhitelistEntries.CollectionChanged += (_, __) => OnPropertyChanged(nameof(HasWhitelistEntries));
        QuarantineEntries.CollectionChanged += (_, __) => OnPropertyChanged(nameof(HasQuarantineEntries));
    }

    public ObservableCollection<AntiSystemWhitelistEntryViewModel> WhitelistEntries { get; }

    public ObservableCollection<AntiSystemQuarantineEntryViewModel> QuarantineEntries { get; }

    public bool HasWhitelistEntries => WhitelistEntries.Count > 0;

    public bool HasQuarantineEntries => QuarantineEntries.Count > 0;
}

public sealed class AntiSystemWhitelistEntryViewModel
{
    public AntiSystemWhitelistEntryViewModel(
        string id,
        AntiSystemWhitelistEntryKind kind,
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

    public AntiSystemWhitelistEntryKind Kind { get; }

    public string Value { get; }

    public string? Notes { get; }

    public string AddedBy { get; }

    public DateTimeOffset AddedAtUtc { get; }

    public string KindLabel => Kind switch
    {
        AntiSystemWhitelistEntryKind.Directory => "Directory",
        AntiSystemWhitelistEntryKind.FileHash => "File hash",
        AntiSystemWhitelistEntryKind.ProcessName => "Process name",
        _ => Kind.ToString()
    };

    public string AddedAtDisplay => AddedAtUtc.ToLocalTime().ToString("g");
}

public sealed class AntiSystemQuarantineEntryViewModel
{
    public AntiSystemQuarantineEntryViewModel(
        string id,
        string processName,
        string filePath,
        string? notes,
        string? addedBy,
        DateTimeOffset quarantinedAtUtc)
    {
        Id = id;
        ProcessName = string.IsNullOrWhiteSpace(processName) ? "Unknown" : processName.Trim();
        FilePath = filePath;
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        AddedBy = string.IsNullOrWhiteSpace(addedBy) ? "TidyWindow" : addedBy.Trim();
        QuarantinedAtUtc = quarantinedAtUtc == default ? DateTimeOffset.UtcNow : quarantinedAtUtc;
    }

    public string Id { get; }

    public string ProcessName { get; }

    public string FilePath { get; }

    public string? Notes { get; }

    public string AddedBy { get; }

    public DateTimeOffset QuarantinedAtUtc { get; }

    public string FileName => Path.GetFileName(FilePath);

    public string QuarantinedAtDisplay => QuarantinedAtUtc.ToLocalTime().ToString("g");
}
