using CommunityToolkit.Mvvm.ComponentModel;

namespace TidyWindow.App.Services;

/// <summary>
/// Central store for user-selected privilege behavior so automation layers can check whether
/// admin elevation is permitted before attempting privileged actions.
/// </summary>
public sealed class PrivilegeOptions : ObservableObject
{
    private bool _adminPrivilegesEnabled = true;

    public bool AdminPrivilegesEnabled
    {
        get => _adminPrivilegesEnabled;
        set => SetProperty(ref _adminPrivilegesEnabled, value);
    }
}
