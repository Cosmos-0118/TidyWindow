namespace TidyWindow.App.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private bool _telemetryEnabled;

    public bool TelemetryEnabled
    {
        get => _telemetryEnabled;
        set => SetProperty(ref _telemetryEnabled, value);
    }
}
