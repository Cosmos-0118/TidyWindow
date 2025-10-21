using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using TidyWindow.Core.PackageManagers;

namespace TidyWindow.App.ViewModels;

public sealed partial class PackageManagerEntryViewModel : ObservableObject
{
    public PackageManagerEntryViewModel(PackageManagerInfo info)
    {
        Identifier = info.Name;
        UpdateFromInfo(info);
    }

    public string Identifier { get; }

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private bool _isInstalled;

    [ObservableProperty]
    private string _notes = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _lastOperationMessage;

    [ObservableProperty]
    private bool? _lastOperationSucceeded;

    public string NotesDisplay => string.Join(Environment.NewLine, BuildNoteLines().Select(line => line.Text));

    public IReadOnlyList<PackageManagerNoteLine> NoteLines => BuildNoteLines();

    public string ActionLabel => IsBusy ? "Working..." : "Install or repair";

    public void UpdateFromInfo(PackageManagerInfo info)
    {
        Name = info.Name;
        IsInstalled = info.IsInstalled;
        Notes = info.Notes;
    }

    public void ResetOperationStatus()
    {
        LastOperationMessage = null;
        LastOperationSucceeded = null;
    }

    partial void OnNotesChanged(string value)
    {
        OnPropertyChanged(nameof(NotesDisplay));
        OnPropertyChanged(nameof(NoteLines));
    }

    partial void OnLastOperationMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(NotesDisplay));
        OnPropertyChanged(nameof(NoteLines));
    }

    partial void OnLastOperationSucceededChanged(bool? value)
    {
        OnPropertyChanged(nameof(NotesDisplay));
        OnPropertyChanged(nameof(NoteLines));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(ActionLabel));

    private IReadOnlyList<PackageManagerNoteLine> BuildNoteLines()
    {
        var lines = new List<PackageManagerNoteLine>();

        if (!string.IsNullOrWhiteSpace(Notes))
        {
            foreach (var note in SplitLines(Notes))
            {
                if (note.Length == 0)
                {
                    continue;
                }

                lines.Add(new PackageManagerNoteLine(note, PackageManagerNoteSeverity.Info));
            }
        }

        if (!string.IsNullOrWhiteSpace(LastOperationMessage))
        {
            var severity = LastOperationSucceeded switch
            {
                true => PackageManagerNoteSeverity.Success,
                false => PackageManagerNoteSeverity.Error,
                _ => PackageManagerNoteSeverity.Info
            };

            lines.Add(new PackageManagerNoteLine(LastOperationMessage.Trim(), severity));
        }

        if (lines.Count == 0)
        {
            lines.Add(new PackageManagerNoteLine("No results yet. Run detection to gather status.", PackageManagerNoteSeverity.Muted));
        }

        return lines;
    }

    private static IEnumerable<string> SplitLines(string value)
    {
        return value
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0);
    }
}

public sealed record PackageManagerNoteLine(string Text, PackageManagerNoteSeverity Severity);

public enum PackageManagerNoteSeverity
{
    Muted,
    Info,
    Success,
    Error
}
