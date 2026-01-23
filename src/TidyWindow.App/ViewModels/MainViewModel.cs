using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using TidyWindow.App.Services;
using TidyWindow.App.Views;

namespace TidyWindow.App.ViewModels;

public sealed partial class MainViewModel : ViewModelBase
{
    private readonly NavigationService _navigationService;
    private readonly ActivityLogService _activityLogService;
    private NavigationItemViewModel? _selectedItem;
    private string _statusMessage = "Ready";
    private int _loadingOperations;
    private object? _titleBarContent;

    public MainViewModel(NavigationService navigationService, ActivityLogService activityLogService)
    {
        _navigationService = navigationService;
        _activityLogService = activityLogService ?? throw new ArgumentNullException(nameof(activityLogService));

        NavigationItems = new ObservableCollection<NavigationItemViewModel>
        {
            new("Bootstrap", "Verify package managers and get ready", "\uE9D2", typeof(BootstrapPage)),
            new("Install hub", "Curated package bundles and install queue", "\uE896", typeof(InstallHubPage)),
            new("Essentials", "Run repair automation quickly", "\uED5A", typeof(EssentialsPage)),
            new("Processes", "Review safe auto-stop recommendations", "\uEDA2", typeof(KnownProcessesPage)),
            new("Startup", "Control Run keys, startup folders, tasks, and services", "\uE7E7", typeof(StartupControllerPage)),
            new("Performance lab", "Lean 8-step stack: power, services, hardware, kernel, security, tracing, scheduler, auto-tune", "\uE945", typeof(PerformanceLabPage)),
            new("PathPilot", "Control runtime precedence and PATH backups", "\uE71B", typeof(PathPilotPage)),
            new("Registry optimizer", "Stage registry defaults safely", "\uE9F5", typeof(RegistryOptimizerPage)),
            new("Maintenance", "Review installed packages, updates, and removals", "\uE90A", typeof(PackageMaintenancePage)),
            new("Deep scan", "Scan to surface files fast", "\uE721", typeof(DeepScanPage)),
            new("Cleanup", "Preview clutter before removing files", "\uE74D", typeof(CleanupPage)),
            new("Logs", "Inspect activity across automation features", "\uE90E", typeof(LogsPage)),
            new("Settings", "Configure preferences and integrations", "\uE713", typeof(SettingsPage))
        };
    }

    [ObservableProperty]
    private bool _isShellLoading;

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

    public object? TitleBarContent
    {
        get => _titleBarContent;
        set => SetProperty(ref _titleBarContent, value);
    }

    public void SetTitleBarContent(object? content)
    {
        TitleBarContent = content;
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

    public void LogActivityInformation(string source, string message, IEnumerable<string>? details = null)
    {
        _activityLogService.LogInformation(source, message, details);
    }

    public void LogActivity(ActivityLogLevel level, string source, string message, IEnumerable<string>? details = null)
    {
        switch (level)
        {
            case ActivityLogLevel.Success:
                _activityLogService.LogSuccess(source, message, details);
                break;
            case ActivityLogLevel.Warning:
                _activityLogService.LogWarning(source, message, details);
                break;
            case ActivityLogLevel.Error:
                _activityLogService.LogError(source, message, details);
                break;
            default:
                _activityLogService.LogInformation(source, message, details);
                break;
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

    public void BeginShellLoad()
    {
        var operations = Interlocked.Increment(ref _loadingOperations);
        if (operations == 1)
        {
            IsShellLoading = true;
        }
    }

    public void CompleteShellLoad()
    {
        var operations = Interlocked.Decrement(ref _loadingOperations);
        if (operations <= 0)
        {
            Interlocked.Exchange(ref _loadingOperations, 0);
            IsShellLoading = false;
        }
    }
}
