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
    private NavigationItemViewModel? _selectedItem;
    private string _statusMessage = "Ready";

    public MainViewModel(NavigationService navigationService)
    {
        _navigationService = navigationService;

        NavigationItems = new ObservableCollection<NavigationItemViewModel>
        {
            new("Bootstrap", "Verify package managers and get ready", typeof(BootstrapPage)),
            new("Install hub", "Curated package bundles and install queue", typeof(InstallHubPage)),
            new("Dashboard", "Overview of health and quick actions", typeof(DashboardPage)),
            new("Runtime updates", "Track essential runtimes and apply updates", typeof(RuntimeUpdatesPage)),
            new("Deep scan", "Find the heaviest files using automation", typeof(DeepScanPage)),
            new("Cleanup", "Preview clutter before removing files", typeof(CleanupPage)),
            new("Tasks", "Track queued and completed maintenance jobs", typeof(TasksPage)),
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
        StatusMessage = string.IsNullOrWhiteSpace(message) ? "Ready" : message;
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
        StatusMessage = item.Description;
    }
}
