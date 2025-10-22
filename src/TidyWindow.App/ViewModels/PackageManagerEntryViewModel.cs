using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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

    public string StatusMessage
    {
        get
        {
            var noteLines = SplitLines(Notes).ToArray();
            if (noteLines.Length > 0)
            {
                return string.Join(Environment.NewLine, noteLines);
            }

            return "No results yet. Run detection to gather status.";
        }
    }

    public string NotesDisplay => string.Join(Environment.NewLine, BuildNoteLines().Select(line => line.Text));

    public IReadOnlyList<PackageManagerNoteLine> NoteLines => BuildNoteLines();

    public string ActionLabel => IsBusy ? "Working..." : (IsInstalled ? "Repair" : "Install");

    public string UninstallLabel => IsBusy ? "Working..." : "Uninstall";

    public bool ShowUninstall => IsInstalled;

    public bool CanUninstall => IsInstalled && !IsBusy;

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
        OnPropertyChanged(nameof(StatusMessage));
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

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(UninstallLabel));
        OnPropertyChanged(nameof(CanUninstall));
    }

    partial void OnIsInstalledChanged(bool value)
    {
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(UninstallLabel));
        OnPropertyChanged(nameof(ShowUninstall));
        OnPropertyChanged(nameof(CanUninstall));
    }

    private IReadOnlyList<PackageManagerNoteLine> BuildNoteLines()
    {
        var lines = new List<PackageManagerNoteLine>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddUniqueLine(string text, PackageManagerNoteSeverity severity)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var normalized = text.Trim();
            if (normalized.Length == 0)
            {
                return;
            }

            if (seen.Add(normalized))
            {
                lines.Add(new PackageManagerNoteLine(normalized, severity));
            }
        }

        if (!string.IsNullOrWhiteSpace(Notes))
        {
            foreach (var note in SplitLines(Notes))
            {
                AddUniqueLine(note, PackageManagerNoteSeverity.Info);
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

            foreach (var note in SplitLines(LastOperationMessage))
            {
                AddUniqueLine(note, severity);
            }
        }

        if (lines.Count == 0)
        {
            lines.Add(new PackageManagerNoteLine("No results yet. Run detection to gather status.", PackageManagerNoteSeverity.Muted));
        }

        return lines;
    }

    private static IEnumerable<string> SplitLines(string value)
    {
        foreach (var segment in value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var piece in segment.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                if (TryNormalizeFragment(piece, out var normalized))
                {
                    yield return normalized;
                }
            }
        }
    }

    private static bool TryNormalizeFragment(string fragment, out string normalized)
    {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(fragment))
        {
            return false;
        }

        var trimmed = fragment.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        trimmed = trimmed.TrimStart('•', '*', '-', '–', '—', '·');
        trimmed = trimmed.Trim();

        trimmed = Regex.Replace(trimmed, @"^\d+(\.\d+)?\s+", string.Empty, RegexOptions.CultureInvariant);
        trimmed = Regex.Replace(trimmed, @"^\[[^\]]+\]\s*", string.Empty, RegexOptions.CultureInvariant);
        trimmed = Regex.Replace(trimmed, @"^(?<token>[A-Fa-f0-9]{6,})\s+", string.Empty, RegexOptions.CultureInvariant);

        trimmed = trimmed.Trim();

        if (trimmed.Length == 0)
        {
            return false;
        }

        if (trimmed.Length <= 3 && trimmed.All(static ch => !char.IsLetter(ch)))
        {
            return false;
        }

        var hasLetter = trimmed.Any(static ch => char.IsLetter(ch));
        var hasDigit = trimmed.Any(static ch => char.IsDigit(ch));

        if (!hasLetter && hasDigit && trimmed.All(static ch => char.IsDigit(ch) || ch == '.' || ch == ','))
        {
            return false;
        }

        normalized = trimmed;
        return true;
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
