using System;
using TidyWindow.App.Services;

namespace TidyWindow.App.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly PrivilegeOptions _privilegeOptions;
    private readonly MainViewModel _mainViewModel;

    private bool _telemetryEnabled;
    private bool _adminPrivilegesEnabled;

    public SettingsViewModel(PrivilegeOptions privilegeOptions, MainViewModel mainViewModel)
    {
        _privilegeOptions = privilegeOptions ?? throw new ArgumentNullException(nameof(privilegeOptions));
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));

        _adminPrivilegesEnabled = _privilegeOptions.AdminPrivilegesEnabled;
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

    public bool AdminPrivilegesEnabled
    {
        get => _adminPrivilegesEnabled;
        set
        {
            if (SetProperty(ref _adminPrivilegesEnabled, value))
            {
                _privilegeOptions.AdminPrivilegesEnabled = value;
                OnPropertyChanged(nameof(AdminPrivilegesStatus));
                _mainViewModel.SetStatusMessage(value
                    ? "Administrative privileges will be requested when required."
                    : "Administrative privileges are disabled for maintenance tasks.");
            }
        }
    }

    public string AdminPrivilegesStatus => AdminPrivilegesEnabled
        ? "TidyWindow will request elevation when tasks require administrative access."
        : "TidyWindow stays in user mode; elevated operations may be skipped.";
}
