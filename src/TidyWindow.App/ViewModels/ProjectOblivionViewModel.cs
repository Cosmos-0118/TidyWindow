using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.Services;
using TidyWindow.Core.ProjectOblivion;
using TidyWindow.App.Views;

namespace TidyWindow.App.ViewModels;

public sealed partial class ProjectOblivionViewModel : ViewModelBase
{
    private readonly ProjectOblivionInventoryService _inventoryService;
    private readonly ActivityLogService _activityLog;
    private readonly MainViewModel _mainViewModel;
    private readonly NavigationService _navigationService;
    private readonly List<ProjectOblivionAppListItemViewModel> _allApps = new();
    private string? _inventoryCachePath;

    public ProjectOblivionViewModel(
        ProjectOblivionInventoryService inventoryService,
        ActivityLogService activityLogService,
        MainViewModel mainViewModel,
        ProjectOblivionPopupViewModel popupViewModel,
        NavigationService navigationService)
    {
        _inventoryService = inventoryService ?? throw new ArgumentNullException(nameof(inventoryService));
        _activityLog = activityLogService ?? throw new ArgumentNullException(nameof(activityLogService));
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        Popup = popupViewModel ?? throw new ArgumentNullException(nameof(popupViewModel));
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));

        Apps = new ObservableCollection<ProjectOblivionAppListItemViewModel>();
        Warnings = new ObservableCollection<string>();
    }

    public ObservableCollection<ProjectOblivionAppListItemViewModel> Apps { get; }

    public ObservableCollection<string> Warnings { get; }

    public ProjectOblivionPopupViewModel Popup { get; }

    public string Summary => BuildSummary();

    public string LastRefreshedDisplay => LastRefreshedAt is null
        ? "Inventory has not been collected yet."
        : $"Inventory updated {LastRefreshedAt.Value.ToLocalTime():g}";

    public bool HasApps => Apps.Count > 0;

    public bool HasWarnings => Warnings.Count > 0;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private ProjectOblivionAppListItemViewModel? _selectedApp;

    [ObservableProperty]
    private DateTimeOffset? _lastRefreshedAt;

    [ObservableProperty]
    private string _headline = "Focused deep clean";

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
    }

    partial void OnSelectedAppChanged(ProjectOblivionAppListItemViewModel? value)
    {
        if (value is null)
        {
            return;
        }

        _mainViewModel.SetStatusMessage($"Selected {value.Name}.");
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        _mainViewModel.SetStatusMessage("Collecting installed applications...");
        var cachePath = EnsureInventoryCachePath();

        try
        {
            var snapshot = await _inventoryService.GetInventoryAsync(cachePath).ConfigureAwait(false);
            await RunOnUiThreadAsync(() =>
            {
                _inventoryCachePath = cachePath;
                ApplySnapshot(snapshot);
                _mainViewModel.SetStatusMessage($"Inventory ready • {snapshot.Apps.Length:N0} app(s).");
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var message = string.IsNullOrWhiteSpace(ex.Message) ? "Inventory failed." : ex.Message.Trim();
            await RunOnUiThreadAsync(() => _mainViewModel.SetStatusMessage(message)).ConfigureAwait(false);
            _activityLog.LogError("Project Oblivion", message, new[] { ex.ToString() });
        }
        finally
        {
            await RunOnUiThreadAsync(() => IsBusy = false).ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = string.Empty;
    }

    [RelayCommand]
    private void LaunchFlow(ProjectOblivionAppListItemViewModel? app)
    {
        var target = app ?? SelectedApp;
        if (target is null)
        {
            _mainViewModel.SetStatusMessage("Select an application to continue.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_inventoryCachePath) || !File.Exists(_inventoryCachePath))
        {
            _mainViewModel.SetStatusMessage("Refresh inventory before running Project Oblivion.");
            return;
        }

        Popup.Prepare(target.Model, _inventoryCachePath);
        SelectedApp = target;
        _navigationService.Navigate(typeof(ProjectOblivionFlowPage));
    }

    private void ApplySnapshot(ProjectOblivionInventorySnapshot snapshot)
    {
        _allApps.Clear();
        foreach (var app in snapshot.Apps.OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase))
        {
            _allApps.Add(new ProjectOblivionAppListItemViewModel(app));
        }

        ApplyFilter();

        Warnings.Clear();
        foreach (var warning in snapshot.Warnings)
        {
            if (string.IsNullOrWhiteSpace(warning))
            {
                continue;
            }

            Warnings.Add(warning);
            _activityLog.LogWarning("Project Oblivion", warning);
        }

        LastRefreshedAt = snapshot.GeneratedAt.ToLocalTime();
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasApps));
        OnPropertyChanged(nameof(HasWarnings));
        OnPropertyChanged(nameof(LastRefreshedDisplay));

        if (Apps.Count > 0)
        {
            SelectedApp = Apps[0];
        }
    }

    private void ApplyFilter()
    {
        var filter = SearchText?.Trim();
        IEnumerable<ProjectOblivionAppListItemViewModel> query = _allApps;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            query = query.Where(app => app.Matches(filter));
        }

        var filtered = query.ToList();
        Apps.Clear();
        foreach (var app in filtered)
        {
            Apps.Add(app);
        }

        if (SelectedApp is null || !Apps.Contains(SelectedApp))
        {
            SelectedApp = Apps.FirstOrDefault();
        }

        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasApps));
    }

    private string BuildSummary()
    {
        if (Apps.Count == 0)
        {
            return "Refresh inventory to begin.";
        }

        var totalSize = _allApps.Sum(app => app.EstimatedSizeBytes ?? 0L);
        return totalSize <= 0
            ? $"{Apps.Count:N0} app(s) ready for removal."
            : $"{Apps.Count:N0} app(s) • {ProjectOblivionPopupViewModel.FormatSize(totalSize)} detected.";
    }

    private static string EnsureInventoryCachePath()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWindow", "ProjectOblivion");
        Directory.CreateDirectory(root);
        return Path.Combine(root, "oblivion-inventory.json");
    }

    private static Task RunOnUiThreadAsync(Action action)
    {
        if (action is null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action, DispatcherPriority.Normal).Task;
    }
}

public sealed class ProjectOblivionAppListItemViewModel : ObservableObject
{
    private static readonly string[] EmptyTags = Array.Empty<string>();

    public ProjectOblivionAppListItemViewModel(ProjectOblivionApp model)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
    }

    public ProjectOblivionApp Model { get; }

    public string AppId => Model.AppId;

    public string Name => Model.Name;

    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Model.Version))
            {
                parts.Add(Model.Version!);
            }

            if (!string.IsNullOrWhiteSpace(Model.Publisher))
            {
                parts.Add(Model.Publisher!);
            }

            if (!string.IsNullOrWhiteSpace(Model.Source))
            {
                parts.Add(Model.Source!);
            }

            return parts.Count == 0 ? "Unknown publisher" : string.Join(" • ", parts);
        }
    }

    public string SizeDisplay
    {
        get
        {
            if (Model.EstimatedSizeBytes is null or <= 0)
            {
                return "Size unknown";
            }

            return ProjectOblivionPopupViewModel.FormatSize(Model.EstimatedSizeBytes.Value);
        }
    }

    public string ScopeDisplay
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Model.Scope))
            {
                return Model.Scope!;
            }

            return Model.Source?.Equals("appx", StringComparison.OrdinalIgnoreCase) == true ? "AppX" : "Win32";
        }
    }

    public long? EstimatedSizeBytes => Model.EstimatedSizeBytes;

    public bool Matches(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        var comparison = StringComparison.OrdinalIgnoreCase;
        return Name.Contains(filter, comparison)
            || (Model.Publisher?.Contains(filter, comparison) ?? false)
            || (Model.Source?.Contains(filter, comparison) ?? false)
            || (Model.Scope?.Contains(filter, comparison) ?? false)
            || GetTags().Any(tag => tag.Contains(filter, comparison));

        IEnumerable<string> GetTags()
        {
            if (Model.Tags.IsDefaultOrEmpty)
            {
                return EmptyTags;
            }

            return Model.Tags;
        }
    }
}
