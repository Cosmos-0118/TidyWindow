using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.Services;
using TidyWindow.App.Services.Cleanup;
using TidyWindow.App.ViewModels.Cleanup;
using TidyWindow.Core.Uninstall;
using System.Windows;
using System.Windows.Data;
using WpfApplication = System.Windows.Application;

namespace TidyWindow.App.ViewModels;

public sealed partial class SimpleUninstallerViewModel : ViewModelBase, IDisposable
{
    private readonly IAppInventoryService _inventoryService;
    private readonly IAppUninstallService _uninstallService;
    private readonly MainViewModel _mainViewModel;
    private readonly ActivityLogService _activityLog;
    private readonly AppCleanupPlanner _cleanupPlanner;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly Dictionary<string, CleanupFlowStepViewModel> _cleanupStepLookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CleanupSuggestionViewModel> _cleanupSuggestionPool = new();
    private CancellationTokenSource? _operationCts;

    public SimpleUninstallerViewModel(
        IAppInventoryService inventoryService,
        IAppUninstallService uninstallService,
        MainViewModel mainViewModel,
        ActivityLogService activityLog,
        AppCleanupPlanner cleanupPlanner)
    {
        _inventoryService = inventoryService ?? throw new ArgumentNullException(nameof(inventoryService));
        _uninstallService = uninstallService ?? throw new ArgumentNullException(nameof(uninstallService));
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        _activityLog = activityLog ?? throw new ArgumentNullException(nameof(activityLog));
        _cleanupPlanner = cleanupPlanner ?? throw new ArgumentNullException(nameof(cleanupPlanner));

        HeroTitle = "Simple uninstaller";
        HeroSubtitle = "Enumerating installed applications...";

        FilteredApps = CollectionViewSource.GetDefaultView(Apps);
        FilteredApps.Filter = FilterApp;

        CleanupSuggestions.CollectionChanged += OnCleanupSuggestionsChanged;
        CleanupRegistryEntries.CollectionChanged += OnCleanupRegistryChanged;
    }

    public ObservableCollection<AppRemovalItemViewModel> Apps { get; } = new();

    public ICollectionView FilteredApps { get; }

    [ObservableProperty]
    private AppRemovalItemViewModel? _selectedApp;

    [ObservableProperty]
    private bool _isDetailsPopupOpen;

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

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private SimpleUninstallerPivot _currentPivot = SimpleUninstallerPivot.Inventory;

    public ObservableCollection<CleanupFlowStepViewModel> CleanupSteps { get; } = new();

    public ObservableCollection<CleanupSuggestionViewModel> CleanupSuggestions { get; } = new();

    public ObservableCollection<string> CleanupRegistryEntries { get; } = new();

    [ObservableProperty]
    private string _cleanupSummary = "Cleanup suggestions will appear after an uninstall.";

    [ObservableProperty]
    private bool _cleanupApplyInProgress;

    [ObservableProperty]
    private AppRemovalItemViewModel? _cleanupTarget;

    public bool HasApps => Apps.Count > 0;

    public bool HasCleanupSuggestions => CleanupSuggestions.Count > 0;

    public bool HasCleanupRegistryEntries => CleanupRegistryEntries.Count > 0;

    public bool HasCleanupContext => CleanupTarget is not null;

    public string CleanupTargetTitle => CleanupTarget?.Name ?? "No uninstall selected";

    public void Dispose()
    {
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _semaphore.Dispose();

        CleanupSuggestions.CollectionChanged -= OnCleanupSuggestionsChanged;
        foreach (var suggestion in _cleanupSuggestionPool)
        {
            suggestion.PropertyChanged -= OnCleanupSuggestionChanged;
        }
        CleanupRegistryEntries.CollectionChanged -= OnCleanupRegistryChanged;
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
                FilteredApps.Refresh();
                _mainViewModel.SetStatusMessage($"Inventory ready • {ordered.Count} app(s)");
                UpdateHeroSubtitle();
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
    private async Task UninstallAppAsync(AppRemovalItemViewModel? item)
    {
        if (IsBusy || item is null)
        {
            return;
        }

        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        var token = _operationCts.Token;
        var closeDetails = ReferenceEquals(SelectedApp, item);

        BeginCleanupFlow(item);
        SetCleanupStepState("start", CleanupFlowStepState.Running, "Initializing uninstall...");

        await _semaphore.WaitAsync(token);
        try
        {
            IsBusy = true;
            var modeLabel = IsDryRun ? "dry run" : "uninstall";
            _mainViewModel.SetStatusMessage(IsDryRun
                ? $"Simulating uninstall for {item.Name}..."
                : $"Uninstalling {item.Name}...");

            _activityLog.LogInformation(
                "Uninstaller",
                $"Starting {modeLabel} for {item.Name}.",
                new[] { item.Name });

            token.ThrowIfCancellationRequested();
            SetCleanupStepState("start", CleanupFlowStepState.Completed, "Queued uninstall flow.");

            SetCleanupStepState("uninstall", CleanupFlowStepState.Running, "Invoking default uninstall...");
            var uninstallSucceeded = await ProcessItemAsync(item, token);
            SetCleanupStepState(
                "uninstall",
                uninstallSucceeded ? CleanupFlowStepState.Completed : CleanupFlowStepState.Failed,
                uninstallSucceeded ? "Official uninstall finished." : "Uninstallers reported a failure.");

            SetCleanupStepState("leftovers", CleanupFlowStepState.Running, "Scanning leftover folders...");
            var plan = await LoadCleanupPlanAsync(item, token);
            if (plan is not null)
            {
                var detail = plan.HasSuggestions
                    ? $"Found {plan.Suggestions.Count} leftover item(s)."
                    : "No leftover folders or shortcuts detected.";
                SetCleanupStepState("leftovers", CleanupFlowStepState.Completed, detail);
            }
            else
            {
                SetCleanupStepState("leftovers", CleanupFlowStepState.Failed, "Unable to inspect leftovers.");
            }

            var summary = BuildCleanupSummary(uninstallSucceeded, plan);
            CleanupSummary = summary;
            SetCleanupStepState("summary", CleanupFlowStepState.Completed, summary);

            _mainViewModel.SetStatusMessage(IsDryRun
                ? "Dry run complete."
                : "Uninstall complete.");

            _activityLog.LogSuccess(
                "Uninstaller",
                $"{modeLabel.ToUpperInvariant()} complete",
                new[] { $"Target: {item.Name}", $"Dry run: {IsDryRun}" });
        }
        catch (OperationCanceledException)
        {
            _mainViewModel.SetStatusMessage("Uninstall cancelled.");
            SetCleanupStepState("uninstall", CleanupFlowStepState.Failed, "Cancelled by user.");
            SetCleanupStepState("leftovers", CleanupFlowStepState.Failed, "Cancelled.");
            SetCleanupStepState("summary", CleanupFlowStepState.Failed, "Cancelled.");
            CleanupSummary = "Uninstall cancelled.";
            _activityLog.LogWarning("Uninstaller", "Uninstall cancelled by user.");
        }
        finally
        {
            IsBusy = false;
            _semaphore.Release();
            UpdateHeroSubtitle();
            if (closeDetails)
            {
                CloseDetails();
            }
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

    [RelayCommand(CanExecute = nameof(CanApplyCleanup))]
    private async Task ApplyCleanupAsync()
    {
        if (CleanupTarget is null)
        {
            return;
        }

        var selections = CleanupSuggestions
            .Where(static suggestion => suggestion.IsSelected)
            .Select(static suggestion => suggestion.Suggestion)
            .ToList();

        if (selections.Count == 0)
        {
            return;
        }

        CleanupApplyInProgress = true;
        try
        {
            var result = await _cleanupPlanner.ApplyAsync(selections, CancellationToken.None);
            foreach (var message in result.Messages)
            {
                _activityLog.LogInformation("Uninstaller", message);
            }

            if (result.Errors.Count > 0)
            {
                foreach (var error in result.Errors)
                {
                    _activityLog.LogWarning("Uninstaller", error);
                }
            }

            CleanupSummary = result.Errors.Count == 0
                ? $"Deleted {result.Succeeded} leftover item(s)."
                : $"Deleted {result.Succeeded}/{result.Processed} item(s). Some entries could not be removed.";

            await LoadCleanupPlanAsync(CleanupTarget, CancellationToken.None);
        }
        catch (Exception ex)
        {
            CleanupSummary = "Cleanup failed. Check the activity log for details.";
            _activityLog.LogError("Uninstaller", "Cleanup execution failed", new[] { ex.ToString() });
        }
        finally
        {
            CleanupApplyInProgress = false;
        }
    }

    private bool CanApplyCleanup()
    {
        if (IsDryRun || CleanupApplyInProgress || CleanupTarget is null)
        {
            return false;
        }

        return CleanupSuggestions.Any(static suggestion => suggestion.IsSelected);
    }

    [RelayCommand]
    private void SwitchPivot(SimpleUninstallerPivot pivot)
    {
        CurrentPivot = pivot;
    }

    [RelayCommand]
    private void OpenDetails(AppRemovalItemViewModel? item)
    {
        SelectedApp = item;
        IsDetailsPopupOpen = item is not null;
    }

    [RelayCommand]
    private void CloseDetails()
    {
        IsDetailsPopupOpen = false;
        SelectedApp = null;
    }

    private void BeginCleanupFlow(AppRemovalItemViewModel target)
    {
        CleanupTarget = target;
        CleanupSummary = "Preparing uninstall...";
        ResetCleanupSuggestions();
        CleanupSteps.Clear();
        _cleanupStepLookup.Clear();
        AddCleanupStep("start", "Starting uninstall");
        AddCleanupStep("uninstall", "Running default uninstall");
        AddCleanupStep("leftovers", "Checking leftovers");
        AddCleanupStep("summary", "Summary");
        CurrentPivot = SimpleUninstallerPivot.Cleanup;
    }

    private void ResetCleanupSuggestions()
    {
        foreach (var suggestion in _cleanupSuggestionPool)
        {
            suggestion.PropertyChanged -= OnCleanupSuggestionChanged;
        }

        _cleanupSuggestionPool.Clear();
        CleanupSuggestions.Clear();
        CleanupRegistryEntries.Clear();
    }

    private void UpdateCleanupSuggestions(AppCleanupPlan plan)
    {
        ResetCleanupSuggestions();
        foreach (var suggestion in plan.Suggestions)
        {
            var vm = new CleanupSuggestionViewModel(suggestion);
            vm.PropertyChanged += OnCleanupSuggestionChanged;
            _cleanupSuggestionPool.Add(vm);
            CleanupSuggestions.Add(vm);
        }

        UpdateRegistryEntries(plan.DeferredItems);
    }

    private void UpdateRegistryEntries(IEnumerable<string> entries)
    {
        CleanupRegistryEntries.Clear();
        if (entries is null)
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (!string.IsNullOrWhiteSpace(entry))
            {
                CleanupRegistryEntries.Add(entry.Trim());
            }
        }
    }

    private async Task<AppCleanupPlan?> LoadCleanupPlanAsync(AppRemovalItemViewModel target, CancellationToken cancellationToken)
    {
        try
        {
            var plan = await _cleanupPlanner.BuildPlanAsync(target.App, cancellationToken);
            UpdateCleanupSuggestions(plan);

            foreach (var deferred in plan.DeferredItems)
            {
                if (!string.IsNullOrWhiteSpace(deferred))
                {
                    _activityLog.LogInformation("Uninstaller", $"Deferred cleanup candidate logged: {deferred}");
                }
            }

            return plan;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ResetCleanupSuggestions();
            CleanupSummary = "Unable to scan leftovers.";
            _activityLog.LogError("Uninstaller", $"{target.App.Name}: cleanup discovery failed", new[] { ex.ToString() });
            return null;
        }
    }

    private static string FormatCleanupSummary(bool uninstallSucceeded, AppCleanupPlan? plan, bool isDryRun = false)
    {
        var hasSuggestions = plan?.HasSuggestions == true;
        if (isDryRun)
        {
            return hasSuggestions
                ? "Dry run recorded. Review leftover folders below."
                : "Dry run recorded. No leftover folders detected.";
        }

        if (uninstallSucceeded)
        {
            return hasSuggestions
                ? "Uninstall finished. Optional leftovers detected below."
                : "Uninstall finished with no leftover folders or shortcuts.";
        }

        return hasSuggestions
            ? "Default uninstall failed. You can still remove leftover folders or shortcuts below."
            : "Default uninstall failed. No additional cleanup suggestions detected.";
    }

    private string BuildCleanupSummary(bool uninstallSucceeded, AppCleanupPlan? plan)
        => FormatCleanupSummary(uninstallSucceeded, plan, IsDryRun);

    private CleanupFlowStepViewModel AddCleanupStep(string id, string title)
    {
        var step = new CleanupFlowStepViewModel(id, title);
        CleanupSteps.Add(step);
        _cleanupStepLookup[id] = step;
        return step;
    }

    private void SetCleanupStepState(string id, CleanupFlowStepState state, string? detail = null)
    {
        if (_cleanupStepLookup.TryGetValue(id, out var step))
        {
            step.State = state;
            if (!string.IsNullOrWhiteSpace(detail))
            {
                step.Detail = detail;
            }
        }
    }

    private async Task<bool> ProcessItemAsync(AppRemovalItemViewModel item, CancellationToken cancellationToken)
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
                    ["UI.Source"] = "SimpleUninstaller"
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
                return true;
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
                return false;
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
            return false;
        }
    }

    private void UpdateHeroSubtitle()
    {
        var total = Apps.Count;
        HeroSubtitle = total == 0
            ? "No user-installed applications detected."
            : total == 1
                ? "1 application ready for action"
                : $"{total} application(s) ready for action";
    }

    partial void OnSearchTextChanged(string value)
    {
        FilteredApps.Refresh();
    }

    partial void OnIsDryRunChanged(bool value)
    {
        ApplyCleanupCommand.NotifyCanExecuteChanged();
    }

    partial void OnCleanupApplyInProgressChanged(bool value)
    {
        ApplyCleanupCommand.NotifyCanExecuteChanged();
    }

    partial void OnCleanupTargetChanged(AppRemovalItemViewModel? value)
    {
        OnPropertyChanged(nameof(CleanupTargetTitle));
        OnPropertyChanged(nameof(HasCleanupContext));
        ApplyCleanupCommand.NotifyCanExecuteChanged();
    }

    private bool FilterApp(object? item)
    {
        if (item is not AppRemovalItemViewModel app)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        var query = SearchText.Trim();
        return app.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(app.Publisher) && app.Publisher.Contains(query, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(app.Version) && app.Version.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private void OnCleanupSuggestionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasCleanupSuggestions));
        ApplyCleanupCommand.NotifyCanExecuteChanged();
    }

    private void OnCleanupSuggestionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(CleanupSuggestionViewModel.IsSelected), StringComparison.Ordinal))
        {
            ApplyCleanupCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnCleanupRegistryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasCleanupRegistryEntries));
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

public enum SimpleUninstallerPivot
{
    Inventory,
    Cleanup
}

public sealed partial class AppRemovalItemViewModel : ObservableObject
{
    public AppRemovalItemViewModel(InstalledApp app)
    {
        App = app ?? throw new ArgumentNullException(nameof(app));
        StatusMessage = string.Empty;
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
    private AppRemovalStatus _status = AppRemovalStatus.Idle;

    [ObservableProperty]
    private string _statusMessage;

    [ObservableProperty]
    private bool _wingetFallbackEnabled;

    [ObservableProperty]
    private bool _wingetOnly;
}

