namespace TidyWindow.App.ViewModels;

public sealed class TasksViewModel : ViewModelBase
{
    private string _summary = "No active tasks yet. Queue actions from the dashboard.";

    public string Summary
    {
        get => _summary;
        set => SetProperty(ref _summary, value);
    }
}
