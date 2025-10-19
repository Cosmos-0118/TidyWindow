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
            new("Dashboard", "Overview of health and quick actions", typeof(DashboardPage)),
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
