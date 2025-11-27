using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.Services;
using TidyWindow.Core.Uninstall;
using System.Windows;
using WpfApplication = System.Windows.Application;

namespace TidyWindow.App.ViewModels;

public sealed partial class SimpleUninstallerViewModel : ViewModelBase, IDisposable
{
    private readonly IAppInventoryService _inventoryService;
    private readonly IAppUninstallService _uninstallService;
    private readonly MainViewModel _mainViewModel;
    private readonly ActivityLogService _activityLog;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private CancellationTokenSource? _operationCts;

    public SimpleUninstallerViewModel(
        IAppInventoryService inventoryService,
        IAppUninstallService uninstallService,
        MainViewModel mainViewModel,
        ActivityLogService activityLog)
    {
        _inventoryService = inventoryService ?? throw new ArgumentNullException(nameof(inventoryService));
        _uninstallService = uninstallService ?? throw new ArgumentNullException(nameof(uninstallService));
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        _activityLog = activityLog ?? throw new ArgumentNullException(nameof(activityLog));

        HeroTitle = "Simple uninstaller";
        HeroSubtitle = "Enumerating installed applications...";
    }

    public ObservableCollection<AppRemovalItemViewModel> Apps { get; } = new();

    [ObservableProperty]
    private AppRemovalItemViewModel? _selectedApp;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isDryRun = true;

    [ObservableProperty]
    private string _heroTitle = string.Empty;

    [ObservableProperty]
    private string _heroSubtitle = string.Empty;

    [ObservableProperty]
    private string _lastUpdatedText = "Inventory pending";

    public bool HasApps => Apps.Count > 0;

    public void Dispose()
    {
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _semaphore.Dispose();
    }

    public async Task InitializeAsync()
    {
        if (Apps.Count > 0)
        {
            return;
        }

        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        await _semaphore.WaitAsync();
        var options = new AppInventoryOptions
        {
            IncludeSystemComponents = false,
            IncludeUpdates = false,
            IncludeWinget = true,
            IncludeUserEntries = true,
            ForceRefresh = true
        };

        try
        {
            IsBusy = true;
            _mainViewModel.SetStatusMessage("Refreshing installed apps...");
            _activityLog.LogInformation(
                "Uninstaller",
                "Refreshing installed app inventory...",
                DescribeInventoryOptions(options));

            var snapshot = await _inventoryService.GetInventoryAsync(options);
            var ordered = snapshot.Apps
                .OrderBy(static app => app.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            await RunOnUiThreadAsync(() =>
            {
                Apps.Clear();
                foreach (var app in ordered)
                {
                    Apps.Add(new AppRemovalItemViewModel(app));
                }

                OnPropertyChanged(nameof(HasApps));
                LastUpdatedText = snapshot.GeneratedAt == DateTimeOffset.MinValue
                    ? "Inventory updated"
                    : $"Inventory updated {FormatRelative(snapshot.GeneratedAt)}";
                HeroSubtitle = ordered.Count == 0
                    ? "No user-installed applications detected."
                    : ordered.Count == 1
                        ? "1 application ready for orchestration"
                        : $"{ordered.Count} applications ready for orchestration";
                _mainViewModel.SetStatusMessage($"Inventory ready • {ordered.Count} app(s)");
            });

            _activityLog.LogSuccess(
                "Uninstaller",
                "Inventory refresh complete.",
                BuildInventoryDiagnostics(snapshot));

            foreach (var warning in snapshot.Warnings)
            {
                if (!string.IsNullOrWhiteSpace(warning))
                {
                    _activityLog.LogWarning("Uninstaller", warning.Trim());
                }
            }
        }
        catch (Exception ex)
        {
            await RunOnUiThreadAsync(() =>
            {
                _mainViewModel.SetStatusMessage($"Inventory refresh failed: {ex.Message}");
            });

            _activityLog.LogError(
                "Uninstaller",
                "Inventory refresh failed",
                DescribeInventoryOptions(options).Concat(new[] { ex.ToString() }));
        }
        finally
        {
            IsBusy = false;
            _semaphore.Release();
            UpdateHeroSubtitle();
        }
    }

    [RelayCommand]
    private async Task UninstallSelectedAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var targets = Apps.Where(static item => item.IsSelected).ToList();
        if (targets.Count == 0)
        {
            _mainViewModel.SetStatusMessage("Select at least one app to uninstall.");
            _activityLog.LogWarning("Uninstaller", "Uninstall requested but no apps were selected.");
            return;
        }

        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        var token = _operationCts.Token;

        await _semaphore.WaitAsync(token);
        try
        {
            IsBusy = true;
            var modeLabel = IsDryRun ? "dry run" : "uninstall";
            _mainViewModel.SetStatusMessage(IsDryRun
                ? $"Simulating uninstall for {targets.Count} app(s)..."
                : $"Uninstalling {targets.Count} app(s)...");

            _activityLog.LogInformation(
                "Uninstaller",
                $"Starting {modeLabel} batch for {targets.Count} app(s).",
                targets.Select(static t => t.Name));

            foreach (var item in targets)
            {
                token.ThrowIfCancellationRequested();
                await ProcessItemAsync(item, token);
            }

            _mainViewModel.SetStatusMessage(IsDryRun
                ? "Dry run complete."
                : "Uninstall batch complete.");

            _activityLog.LogSuccess(
                "Uninstaller",
                $"{modeLabel.ToUpperInvariant()} batch complete",
                new[] { $"Processed: {targets.Count}", $"Dry run: {IsDryRun}" });
        }
        catch (OperationCanceledException)
        {
            _mainViewModel.SetStatusMessage("Uninstall batch cancelled.");
            _activityLog.LogWarning("Uninstaller", "Uninstall batch cancelled by user.");
        }
        finally
        {
            IsBusy = false;
            _semaphore.Release();
            UpdateHeroSubtitle();
        }
    }

    [RelayCommand]
    private void CancelOperations()
    {
        if (!IsBusy)
        {
            return;
        }

        _operationCts?.Cancel();
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var app in Apps)
        {
            app.IsSelected = true;
        }

        _mainViewModel.SetStatusMessage($"Selected {Apps.Count} app(s).");
    }

    private async Task ProcessItemAsync(AppRemovalItemViewModel item, CancellationToken cancellationToken)
    {
        item.Status = AppRemovalStatus.Running;
        item.StatusMessage = IsDryRun ? "Simulating uninstall plan..." : "Running uninstall plan...";

        try
        {
            var options = new AppUninstallOptions
            {
                DryRun = IsDryRun,
                EnableWingetFallback = item.WingetFallbackEnabled && item.SupportsWinget,
                WingetOnly = item.WingetOnly,
                MetadataOverrides = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>
                {
                    ["UI.Selected"] = item.IsSelected.ToString()
                })
            };

            _activityLog.LogInformation(
                "Uninstaller",
                $"{item.App.Name}: starting {(IsDryRun ? "dry run" : "uninstall")}.",
                DescribeUninstallOptions(options, item));

            var result = await _uninstallService.UninstallAsync(item.App, options, cancellationToken);
            var success = result.IsSuccess;
            item.Status = success ? AppRemovalStatus.Succeeded : AppRemovalStatus.Failed;
            item.StatusMessage = success ? (IsDryRun ? "Dry run recorded" : "Completed") : "Failed";

            if (success)
            {
                _activityLog.LogSuccess(
                    "Uninstaller",
                    $"{item.App.Name}: {(IsDryRun ? "dry run" : "uninstall")} complete.",
                    BuildStepDiagnostics(result));
            }
            else
            {
                var errors = result.Operation.Steps.SelectMany(step => step.Errors).Where(static line => !string.IsNullOrWhiteSpace(line)).ToList();
                if (errors.Count == 0)
                {
                    errors.Add("No diagnostic output captured.");
                }

                _activityLog.LogError(
                    "Uninstaller",
                    $"{item.App.Name}: uninstall failed",
                    BuildStepDiagnostics(result).Concat(errors));
            }
        }
        catch (OperationCanceledException)
        {
            item.Status = AppRemovalStatus.Cancelled;
            item.StatusMessage = "Cancelled";
            throw;
        }
        catch (Exception ex)
        {
            item.Status = AppRemovalStatus.Failed;
            item.StatusMessage = ex.Message;
            _activityLog.LogError("Uninstaller", $"{item.App.Name}: uninstall failed", new[] { ex.ToString() });
        }
        finally
        {
            item.IsSelected = false;
        }
    }

    private void UpdateHeroSubtitle()
    {
        var selected = Apps.Count(static item => item.IsSelected);
        var total = Apps.Count;
        HeroSubtitle = total == 0
            ? "No user-installed applications detected."
            : selected == 0
                ? $"{total} application(s) ready for action"
                : $"{total} application(s) • {selected} selected";
    }

    internal static string FormatRelative(DateTimeOffset timestamp)
    {
        var delta = DateTimeOffset.UtcNow - timestamp;
        if (delta < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (delta < TimeSpan.FromHours(1))
        {
            return $"{Math.Floor(delta.TotalMinutes)} minute(s) ago";
        }

        if (delta < TimeSpan.FromDays(1))
        {
            return $"{Math.Floor(delta.TotalHours)} hour(s) ago";
        }

        return timestamp.ToLocalTime().ToString("g");
    }

    private static Task RunOnUiThreadAsync(Action action)
    {
        if (action is null)
        {
            return Task.CompletedTask;
        }

        var dispatcher = WpfApplication.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    private static IEnumerable<string> DescribeInventoryOptions(AppInventoryOptions options)
    {
        yield return $"IncludeSystemComponents={options.IncludeSystemComponents}";
        yield return $"IncludeUpdates={options.IncludeUpdates}";
        yield return $"IncludeWinget={options.IncludeWinget}";
        yield return $"IncludeUserEntries={options.IncludeUserEntries}";
        yield return $"DryRun={options.DryRun}";
    }

    private static IEnumerable<string> BuildInventoryDiagnostics(AppInventorySnapshot snapshot)
    {
        yield return $"Apps={snapshot.Apps.Length}";
        if (snapshot.Duration > TimeSpan.Zero)
        {
            yield return $"Duration={snapshot.Duration.TotalMilliseconds:F0} ms";
        }

        if (snapshot.Plan.Length > 0)
        {
            yield return "Plan=" + string.Join(" | ", snapshot.Plan.Take(10));
        }
    }

    private static IEnumerable<string> DescribeUninstallOptions(AppUninstallOptions options, AppRemovalItemViewModel item)
    {
        yield return $"DryRun={options.DryRun}";
        yield return $"WingetFallback={options.EnableWingetFallback}";
        yield return $"WingetOnly={options.WingetOnly}";
        yield return $"RequiresElevation={item.RequiresElevation}";
    }

    private static IEnumerable<string> BuildStepDiagnostics(AppUninstallResult result)
    {
        foreach (var snapshot in result.Operation.Steps)
        {
            var exit = snapshot.DryRun
                ? "dry-run"
                : snapshot.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "n/a";

            yield return $"{snapshot.Plan.Description}: exit {exit} • {snapshot.Duration.TotalSeconds:F1}s";

            if (!snapshot.DryRun && snapshot.Errors.Count > 0)
            {
                foreach (var error in snapshot.Errors.Take(3))
                {
                    yield return $"  error: {error}";
                }
            }
        }
    }
}

public enum AppRemovalStatus
{
    Idle,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

public sealed partial class AppRemovalItemViewModel : ObservableObject
{
    public AppRemovalItemViewModel(InstalledApp app)
    {
        App = app ?? throw new ArgumentNullException(nameof(app));
        StatusMessage = "Idle";
    }

    public InstalledApp App { get; }

    public string Name => App.Name;

    public string? Version => App.Version;

    public string Publisher => string.IsNullOrWhiteSpace(App.Publisher) ? "Unknown publisher" : App.Publisher!;

    public string SourceBadge => App.SourceTags.FirstOrDefault() ?? (RequiresElevation ? "Machine" : "User");

    public bool SupportsWinget => App.HasWingetMetadata;

    public bool RequiresElevation => !App.SourceTags.Any(static tag => string.Equals(tag, "User", StringComparison.OrdinalIgnoreCase))
        && (string.IsNullOrWhiteSpace(App.RegistryKey)
            || !App.RegistryKey.Contains("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase));

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private AppRemovalStatus _status = AppRemovalStatus.Idle;

    [ObservableProperty]
    private string _statusMessage;

    [ObservableProperty]
    private bool _wingetFallbackEnabled;

    [ObservableProperty]
    private bool _wingetOnly;
}

