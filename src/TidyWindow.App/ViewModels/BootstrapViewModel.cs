using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.Services;
using TidyWindow.Core.Automation;
using TidyWindow.Core.PackageManagers;

namespace TidyWindow.App.ViewModels;

public sealed partial class BootstrapViewModel : ViewModelBase
{
    private readonly PackageManagerDetector _detector;
    private readonly PackageManagerInstaller _installer;
    private readonly MainViewModel _mainViewModel;
    private readonly ActivityLogService _activityLog;
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

    public BootstrapViewModel(PackageManagerDetector detector, PackageManagerInstaller installer, MainViewModel mainViewModel, ActivityLogService activityLogService)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        _activityLog = activityLogService ?? throw new ArgumentNullException(nameof(activityLogService));
    }

    [RelayCommand]
    private async Task DetectAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var includeScoop = IncludeScoop;
        var includeChocolatey = IncludeChocolatey;

        try
        {
            IsBusy = true;
            _activityLog.LogInformation("Bootstrap", $"Detecting package managers (include Scoop: {includeScoop}; include Chocolatey: {includeChocolatey}).");

            var results = await _detector.DetectAsync(includeScoop, includeChocolatey);

            UpdateManagers(results);
            var completionMessage = $"Detection completed at {DateTime.Now:t}.";
            _mainViewModel.SetStatusMessage(completionMessage);
            _activityLog.LogSuccess("Bootstrap", $"Detection completed • {results.Count} manager(s) evaluated.", BuildDetectionDetails(results, includeScoop, includeChocolatey));
        }
        catch (Exception ex)
        {
            var failureMessage = $"Detection failed: {ex.Message}";
            _mainViewModel.SetStatusMessage(failureMessage);
            _activityLog.LogError("Bootstrap", failureMessage, BuildDetectionFailureDetails(includeScoop, includeChocolatey, ex));
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

        var managerName = manager.Name;
        var refreshAfterInstall = false;

        try
        {
            IsBusy = true;
            manager.IsBusy = true;
            manager.LastOperationMessage = "Preparing install...";
            manager.LastOperationSucceeded = null;

            var statusMessage = $"Running install or repair for {managerName}...";
            _mainViewModel.SetStatusMessage(statusMessage);
            _activityLog.LogInformation("Bootstrap", statusMessage, BuildManagerContextDetails(manager));

            var result = await _installer.InstallOrRepairAsync(managerName);
            var invocationDetails = BuildInvocationDetails(manager, result);
            if (result.IsSuccess)
            {
                var summary = GetOperationSummary(result.Output)
                               ?? $"Install or repair completed for {managerName}.";
                manager.LastOperationMessage = summary;
                manager.LastOperationSucceeded = true;
                _mainViewModel.SetStatusMessage(summary);
                _activityLog.LogSuccess("Bootstrap", summary, invocationDetails);
                refreshAfterInstall = true;
            }
            else
            {
                var adminMessage = TryGetAdminMessage(result.Errors);
                var error = adminMessage
                            ?? GetOperationSummary(result.Errors)
                            ?? $"Install or repair failed for {managerName}.";
                manager.LastOperationMessage = error;
                manager.LastOperationSucceeded = false;
                _mainViewModel.SetStatusMessage(error);

                if (!string.IsNullOrWhiteSpace(adminMessage))
                {
                    _activityLog.LogWarning("Bootstrap", error, invocationDetails);
                }
                else
                {
                    _activityLog.LogError("Bootstrap", error, invocationDetails);
                }
            }
        }
        catch (Exception ex)
        {
            manager.LastOperationMessage = ex.Message;
            manager.LastOperationSucceeded = false;
            var failureMessage = $"Install failed for {managerName}: {ex.Message}";
            _mainViewModel.SetStatusMessage(failureMessage);
            _activityLog.LogError("Bootstrap", failureMessage, BuildExceptionDetails(manager, ex));
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

    private static IEnumerable<string> BuildDetectionDetails(IReadOnlyList<PackageManagerInfo> managers, bool includeScoop, bool includeChocolatey)
    {
        var lines = new List<string>
        {
            $"Include Scoop: {includeScoop}",
            $"Include Chocolatey: {includeChocolatey}"
        };

        if (managers.Count == 0)
        {
            lines.Add("No package managers detected.");
            return lines;
        }

        lines.Add("--- Managers ---");

        foreach (var manager in managers.OrderBy(static manager => manager.Name, StringComparer.OrdinalIgnoreCase))
        {
            var status = manager.IsInstalled ? "Installed" : "Missing";
            if (string.IsNullOrWhiteSpace(manager.Notes))
            {
                lines.Add($"{manager.Name}: {status}");
            }
            else
            {
                lines.Add($"{manager.Name}: {status} — {manager.Notes.Trim()}");
            }
        }

        return lines;
    }

    private static IEnumerable<string> BuildDetectionFailureDetails(bool includeScoop, bool includeChocolatey, Exception exception)
    {
        return new[]
        {
            $"Include Scoop: {includeScoop}",
            $"Include Chocolatey: {includeChocolatey}",
            "--- Exception ---",
            exception.ToString()
        };
    }

    private static List<string> BuildManagerContextDetails(PackageManagerEntryViewModel manager)
    {
        var lines = new List<string>
        {
            $"Identifier: {manager.Identifier}",
            $"Display name: {manager.Name}",
            $"Installed: {manager.IsInstalled}"
        };

        if (!string.IsNullOrWhiteSpace(manager.Notes))
        {
            lines.Add($"Notes: {manager.Notes.Trim()}");
        }

        return lines;
    }

    private static IEnumerable<string> BuildInvocationDetails(PackageManagerEntryViewModel manager, PowerShellInvocationResult result)
    {
        var lines = BuildManagerContextDetails(manager);
        lines.Add($"Exit code: {result.ExitCode}");

        if (result.Output is { Count: > 0 })
        {
            lines.Add("--- Output ---");
            foreach (var line in result.Output)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    lines.Add(line);
                }
            }
        }

        if (result.Errors is { Count: > 0 })
        {
            lines.Add("--- Errors ---");
            foreach (var line in result.Errors)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    lines.Add(line);
                }
            }
        }

        return lines;
    }

    private static IEnumerable<string> BuildExceptionDetails(PackageManagerEntryViewModel manager, Exception exception)
    {
        var lines = BuildManagerContextDetails(manager);
        lines.Add("--- Exception ---");
        lines.Add(exception.ToString());
        return lines;
    }
}
