using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.Core.PackageManagers;

namespace TidyWindow.App.ViewModels;

public sealed partial class BootstrapViewModel : ViewModelBase
{
    private readonly PackageManagerDetector _detector;
    private readonly PackageManagerInstaller _installer;
    private readonly MainViewModel _mainViewModel;
    private readonly Dictionary<string, PackageManagerEntryViewModel> _managerLookup = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    private bool _includeScoop = true;

    [ObservableProperty]
    private bool _includeChocolatey = true;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _headline = "Check your package manager tools";

    public ObservableCollection<PackageManagerEntryViewModel> Managers { get; } = new();

    public BootstrapViewModel(PackageManagerDetector detector, PackageManagerInstaller installer, MainViewModel mainViewModel)
    {
        _detector = detector;
        _installer = installer;
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
            var results = await _detector.DetectAsync(IncludeScoop, IncludeChocolatey);

            UpdateManagers(results);

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
    private async Task InstallAsync(PackageManagerEntryViewModel? manager)
    {
        if (manager is null || string.IsNullOrWhiteSpace(manager.Name) || manager.IsBusy)
        {
            return;
        }

        var refreshAfterInstall = false;

        try
        {
            IsBusy = true;
            manager.IsBusy = true;
            manager.LastOperationMessage = "Preparing install...";
            manager.LastOperationSucceeded = null;

            _mainViewModel.SetStatusMessage($"Running install or repair for {manager.Name}...");

            var result = await _installer.InstallOrRepairAsync(manager.Name);
            if (result.IsSuccess)
            {
                var summary = GetOperationSummary(result.Output)
                               ?? $"Install or repair completed for {manager.Name}.";
                manager.LastOperationMessage = summary;
                manager.LastOperationSucceeded = true;
                _mainViewModel.SetStatusMessage(summary);
                refreshAfterInstall = true;
            }
            else
            {
                var error = TryGetAdminMessage(result.Errors)
                            ?? GetOperationSummary(result.Errors)
                            ?? $"Install or repair failed for {manager.Name}.";
                manager.LastOperationMessage = error;
                manager.LastOperationSucceeded = false;
                _mainViewModel.SetStatusMessage(error);
            }
        }
        catch (Exception ex)
        {
            manager.LastOperationMessage = ex.Message;
            manager.LastOperationSucceeded = false;
            _mainViewModel.SetStatusMessage($"Install failed for {manager?.Name}: {ex.Message}");
        }
        finally
        {
            manager.IsBusy = false;
            IsBusy = false;
        }

        if (refreshAfterInstall)
        {
            await DetectAsync();
        }
    }

    private void UpdateManagers(IReadOnlyList<PackageManagerInfo> detected)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var info in detected)
        {
            seen.Add(info.Name);

            if (!_managerLookup.TryGetValue(info.Name, out var entry))
            {
                entry = new PackageManagerEntryViewModel(info);
                _managerLookup[info.Name] = entry;
                Managers.Add(entry);
            }
            else
            {
                entry.UpdateFromInfo(info);
            }
        }

        for (var index = Managers.Count - 1; index >= 0; index--)
        {
            var entry = Managers[index];
            if (!seen.Contains(entry.Identifier))
            {
                Managers.RemoveAt(index);
                _managerLookup.Remove(entry.Identifier);
            }
        }
    }

    private static string? TryGetAdminMessage(IReadOnlyList<string> errors)
    {
        foreach (var line in errors)
        {
            if (line.Contains("Administrator privileges are required", StringComparison.OrdinalIgnoreCase))
            {
                return "Administrator approval is required. When prompted, allow TidyWindow to make changes.";
            }

            if (line.Contains("Administrator approval was denied", StringComparison.OrdinalIgnoreCase))
            {
                return "Administrator approval was denied. Please accept the UAC prompt to continue.";
            }

            if (line.Contains("Administrator approval was", StringComparison.OrdinalIgnoreCase) && line.Contains("operation could start", StringComparison.OrdinalIgnoreCase))
            {
                return "Administrator approval is needed to continue. Retry and confirm the Windows permission prompt.";
            }
        }

        return null;
    }

    private static string? GetOperationSummary(IReadOnlyList<string> lines)
    {
        if (lines is null || lines.Count == 0)
        {
            return null;
        }

        for (var index = lines.Count - 1; index >= 0; index--)
        {
            var line = lines[index];
            if (!string.IsNullOrWhiteSpace(line))
            {
                return line.Trim();
            }
        }

        return null;
    }
}
