using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.Core.PackageManagers;

namespace TidyWindow.App.ViewModels;

public sealed partial class BootstrapViewModel : ViewModelBase
{
    private readonly PackageManagerDetector _detector;
    private readonly MainViewModel _mainViewModel;

    [ObservableProperty]
    private bool _includeScoop = true;

    [ObservableProperty]
    private bool _includeChocolatey = true;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _headline = "Check your package manager tools";

    public ObservableCollection<PackageManagerInfo> Managers { get; } = new();

    public BootstrapViewModel(PackageManagerDetector detector, MainViewModel mainViewModel)
    {
        _detector = detector;
        _mainViewModel = mainViewModel;
    }

    [RelayCommand]
    private async Task DetectAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            Managers.Clear();

            var results = await _detector.DetectAsync(IncludeScoop, IncludeChocolatey);
            foreach (var manager in results)
            {
                Managers.Add(manager);
            }

            _mainViewModel.SetStatusMessage($"Detection completed at {DateTime.Now:t}.");
        }
        catch (Exception ex)
        {
            _mainViewModel.SetStatusMessage($"Detection failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task InstallAsync(PackageManagerInfo? manager)
    {
        if (manager is null)
        {
            return;
        }

        _mainViewModel.SetStatusMessage($"Install workflow for {manager.Name} is coming soon.");
        await Task.Delay(300);
    }
}
