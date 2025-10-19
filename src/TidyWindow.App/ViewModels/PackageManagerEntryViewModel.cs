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

    public string NotesDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(LastOperationMessage))
            {
                return Notes;
            }

            var prefix = LastOperationSucceeded switch
            {
                true => "[Success] ",
                false => "[Action needed] ",
                _ => string.Empty
            };

            var statusLine = prefix + LastOperationMessage.Trim();

            if (string.IsNullOrWhiteSpace(Notes))
            {
                return statusLine;
            }

            return Notes + "\n" + statusLine;
        }
    }

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

    partial void OnNotesChanged(string value) => OnPropertyChanged(nameof(NotesDisplay));

    partial void OnLastOperationMessageChanged(string? value) => OnPropertyChanged(nameof(NotesDisplay));

    partial void OnLastOperationSucceededChanged(bool? value) => OnPropertyChanged(nameof(NotesDisplay));

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(ActionLabel));
}
