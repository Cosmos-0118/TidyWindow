using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using TidyWindow.App.Services;
using TidyWindow.App.Views;

namespace TidyWindow.App.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private readonly NavigationService _navigationService;
    private readonly ActivityLogService _activityLogService;
    private NavigationItemViewModel? _selectedItem;
    private string _statusMessage = "Ready";

    public MainViewModel(NavigationService navigationService, ActivityLogService activityLogService)
    {
        _navigationService = navigationService;
        _activityLogService = activityLogService ?? throw new ArgumentNullException(nameof(activityLogService));

        NavigationItems = new ObservableCollection<NavigationItemViewModel>
        {
            new("Bootstrap", "Verify package managers and get ready", typeof(BootstrapPage)),
            new("Install hub", "Curated package bundles and install queue", typeof(InstallHubPage)),
            new("Essentials", "Run repair automation quickly", typeof(EssentialsPage)),
            new("Registry optimizer", "Stage registry defaults safely", typeof(RegistryOptimizerPage)),
            new("Driver updates", "Detect pending Windows driver versions", typeof(DriverUpdatesPage)),
            new("Maintenance", "Review installed packages, updates, and removals", typeof(PackageMaintenancePage)),
            new("Deep scan", "Find the heaviest files using automation", typeof(DeepScanPage)),
            new("Cleanup", "Preview clutter before removing files", typeof(CleanupPage)),
            new("Logs", "Inspect activity across automation features", typeof(LogsPage)),
            new("Settings", "Configure preferences and integrations", typeof(SettingsPage))
        };
    }

    public ObservableCollection<NavigationItemViewModel> NavigationItems { get; }

    public NavigationItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value) && value is not null)
            {
                NavigateTo(value);
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public void SetStatusMessage(string message)
    {
        var resolved = string.IsNullOrWhiteSpace(message) ? "Ready" : message.Trim();
        StatusMessage = resolved;

        if (!string.Equals(resolved, "Ready", StringComparison.OrdinalIgnoreCase))
        {
            _activityLogService.LogInformation("Status", resolved);
        }
    }

    public void NavigateTo(Type pageType)
    {
        if (pageType is null)
        {
            throw new ArgumentNullException(nameof(pageType));
        }

        var target = NavigationItems.FirstOrDefault(item => item.PageType == pageType);
        if (target is not null)
        {
            SelectedItem = target;
        }
    }

    public void Activate()
    {
        if (NavigationItems.Count == 0)
        {
            StatusMessage = "No modules available.";
            return;
        }

        if (SelectedItem is null)
        {
            SelectedItem = NavigationItems.First();
        }
        else
        {
            NavigateTo(SelectedItem);
        }
    }

    private void NavigateTo(NavigationItemViewModel item)
    {
        if (!_navigationService.IsInitialized)
        {
            return;
        }

        _navigationService.Navigate(item.PageType);
        SetStatusMessage(item.Description);
    }
}
