using System;
using TidyWindow.App.Services;

namespace TidyWindow.App.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly MainViewModel _mainViewModel;

    private bool _telemetryEnabled;
    private PrivilegeMode _currentPrivilegeMode;

    public SettingsViewModel(MainViewModel mainViewModel, IPrivilegeService privilegeService)
    {
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        _currentPrivilegeMode = privilegeService?.CurrentMode ?? PrivilegeMode.Administrator;
    }

    public bool TelemetryEnabled
    {
        get => _telemetryEnabled;
        set
        {
            if (SetProperty(ref _telemetryEnabled, value))
            {
                _mainViewModel.SetStatusMessage(value
                    ? "Telemetry sharing is enabled."
                    : "Telemetry sharing is disabled.");
            }
        }
    }

    public bool IsRunningElevated => CurrentPrivilegeMode == PrivilegeMode.Administrator;

    public string CurrentPrivilegeDisplay => IsRunningElevated
        ? "Current session: Administrator mode"
        : "Current session: User mode";

    public string CurrentPrivilegeAdvice => "TidyWindow always runs with administrative privileges so catalog updates and maintenance tasks succeed.";

    private PrivilegeMode CurrentPrivilegeMode
    {
        get => _currentPrivilegeMode;
        set
        {
            if (SetProperty(ref _currentPrivilegeMode, value))
            {
                OnPropertyChanged(nameof(IsRunningElevated));
                OnPropertyChanged(nameof(CurrentPrivilegeDisplay));
                OnPropertyChanged(nameof(CurrentPrivilegeAdvice));
            }
        }
    }
}
