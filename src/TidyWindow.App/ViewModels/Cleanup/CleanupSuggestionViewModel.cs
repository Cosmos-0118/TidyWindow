using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TidyWindow.App.Services.Cleanup;

namespace TidyWindow.App.ViewModels.Cleanup;

public sealed partial class CleanupSuggestionViewModel : ObservableObject
{
    public CleanupSuggestionViewModel(CleanupSuggestion suggestion)
    {
        Suggestion = suggestion;
    }

    public CleanupSuggestion Suggestion { get; }

    public string Title => Suggestion.Title;

    public string Description => Suggestion.Description;

    public string Path => Suggestion.Path;

    public bool IsSafe => Suggestion.IsSafe;

    public CleanupSuggestionKind Kind => Suggestion.Kind;

    [ObservableProperty]
    private bool _isSelected;
}
