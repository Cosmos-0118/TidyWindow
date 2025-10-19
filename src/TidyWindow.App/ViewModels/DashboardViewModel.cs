namespace TidyWindow.App.ViewModels;

public sealed class DashboardViewModel : ViewModelBase
{
    private string _welcomeMessage = "Welcome to your TidyWindow dashboard.";

    public string WelcomeMessage
    {
        get => _welcomeMessage;
        set => SetProperty(ref _welcomeMessage, value);
    }
}
