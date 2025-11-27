using CommunityToolkit.Mvvm.ComponentModel;

namespace TidyWindow.App.ViewModels.Cleanup;

public sealed partial class CleanupFlowStepViewModel : ObservableObject
{
    public CleanupFlowStepViewModel(string id, string title)
    {
        Id = id;
        Title = title;
    }

    public string Id { get; }

    public string Title { get; }

    [ObservableProperty]
    private CleanupFlowStepState _state = CleanupFlowStepState.Pending;

    [ObservableProperty]
    private string _detail = string.Empty;
}

public enum CleanupFlowStepState
{
    Pending,
    Running,
    Completed,
    Failed
}
