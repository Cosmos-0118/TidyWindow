using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.Services;

namespace TidyWindow.App.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly PrivilegeOptions _privilegeOptions;
    private readonly MainViewModel _mainViewModel;
    private readonly IPrivilegeService _privilegeService;

    private readonly AsyncRelayCommand _restartAsAdministratorCommand;
    private readonly AsyncRelayCommand _restartAsStandardCommand;

    private bool _telemetryEnabled;
    private bool _adminPrivilegesEnabled;
    private PrivilegeMode _currentPrivilegeMode;

    public SettingsViewModel(PrivilegeOptions privilegeOptions, MainViewModel mainViewModel, IPrivilegeService privilegeService)
    {
        _privilegeOptions = privilegeOptions ?? throw new ArgumentNullException(nameof(privilegeOptions));
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        _privilegeService = privilegeService ?? throw new ArgumentNullException(nameof(privilegeService));

        _adminPrivilegesEnabled = _privilegeOptions.AdminPrivilegesEnabled;
        _currentPrivilegeMode = _privilegeService.CurrentMode;

        _restartAsAdministratorCommand = new AsyncRelayCommand(
            () => RestartAsync(PrivilegeMode.Administrator),
            () => CanRestartAsAdministrator);

        _restartAsStandardCommand = new AsyncRelayCommand(
            () => RestartAsync(PrivilegeMode.Standard),
            () => CanRestartAsStandard);
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

    public IAsyncRelayCommand RestartAsAdministratorCommand => _restartAsAdministratorCommand;

    public IAsyncRelayCommand RestartAsStandardCommand => _restartAsStandardCommand;

    public bool CanRestartAsAdministrator => CurrentPrivilegeMode != PrivilegeMode.Administrator;

    public bool CanRestartAsStandard => CurrentPrivilegeMode != PrivilegeMode.Standard;

    public bool IsRunningElevated => CurrentPrivilegeMode == PrivilegeMode.Administrator;

    public string CurrentPrivilegeDisplay => IsRunningElevated
        ? "Current session: Administrator mode"
        : "Current session: User mode";

    public string CurrentPrivilegeAdvice => IsRunningElevated
        ? "Per-user installers like Scoop often refuse to run while elevated. Relaunch TidyWindow in user mode before installing them."
        : "Per-machine installers may still prompt for elevation. Enable admin privileges if a task needs them.";

    public string PrivilegeSwitchGuidance => "Switching modes restarts TidyWindow so cached results and queues reset before continuing.";

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
                OnPropertyChanged(nameof(CanRestartAsAdministrator));
                OnPropertyChanged(nameof(CanRestartAsStandard));
                _restartAsAdministratorCommand.NotifyCanExecuteChanged();
                _restartAsStandardCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private async Task RestartAsync(PrivilegeMode targetMode)
    {
        try
        {
            var result = _privilegeService.Restart(targetMode);
            if (result.Success)
            {
                _mainViewModel.SetStatusMessage(targetMode == PrivilegeMode.Administrator
                    ? "Relaunching with administrative privileges to clear cached state..."
                    : "Relaunching in user mode so per-user installs succeed.");

                var app = System.Windows.Application.Current;
                if (app is not null)
                {
                    await app.Dispatcher.InvokeAsync(app.Shutdown);
                }
                return;
            }

            if (result.AlreadyInTargetMode)
            {
                _mainViewModel.SetStatusMessage("TidyWindow is already running in the requested mode.");
                CurrentPrivilegeMode = _privilegeService.CurrentMode;
                return;
            }

            var error = string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? "Unable to restart in the requested mode."
                : result.ErrorMessage;
            _mainViewModel.SetStatusMessage(error);
            CurrentPrivilegeMode = _privilegeService.CurrentMode;
        }
        catch (Exception ex)
        {
            _mainViewModel.SetStatusMessage($"Restart failed: {ex.Message}");
            CurrentPrivilegeMode = _privilegeService.CurrentMode;
        }
    }
}
