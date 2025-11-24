using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    private CancellationTokenSource? _sizeCalculationCancellation;
    private static readonly Dictionary<string, int> SourcePriority = new(StringComparer.OrdinalIgnoreCase)
    {
        ["registry"] = 0,
        ["appx"] = 1,
        ["winget"] = 2,
        ["store"] = 3,
        ["steam"] = 4,
        ["epic"] = 5,
        ["portable"] = 6,
        ["shortcut"] = 7
    };
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
    private string _activeFilter = string.Empty;

    [ObservableProperty]
    private ProjectOblivionAppListItemViewModel? _selectedApp;

    [ObservableProperty]
    private DateTimeOffset? _lastRefreshedAt;

    [ObservableProperty]
    private string _headline = "Focused deep clean";

    partial void OnActiveFilterChanged(string value) => ApplyFilter();

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
        ActiveFilter = string.Empty;
    }

    [RelayCommand]
    private void ApplySearch()
    {
        ActiveFilter = SearchText?.Trim() ?? string.Empty;
    }

    public void ResumeActiveFlowIfNeeded()
    {
        if (!Popup.HasActiveRun || Popup.TargetApp is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_inventoryCachePath) || !File.Exists(_inventoryCachePath))
        {
            return;
        }

        var existing = _allApps.FirstOrDefault(app => string.Equals(app.AppId, Popup.TargetApp.AppId, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new ProjectOblivionAppListItemViewModel(Popup.TargetApp);
            _allApps.Insert(0, existing);
            ApplyFilter();
        }

        Popup.Prepare(Popup.TargetApp, _inventoryCachePath, preserveExistingState: true);
        SelectedApp = existing;
        _navigationService.Navigate(typeof(ProjectOblivionFlowPage));
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

        var preserveState = Popup.CanResume(target.Model);
        Popup.Prepare(target.Model, _inventoryCachePath, preserveState);
        SelectedApp = target;
        _navigationService.Navigate(typeof(ProjectOblivionFlowPage));
    }

    private void ApplySnapshot(ProjectOblivionInventorySnapshot snapshot)
    {
        _sizeCalculationCancellation?.Cancel();
        _allApps.Clear();
        var deduplicated = DeduplicateApps(snapshot.Apps);
        foreach (var app in deduplicated.OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase))
        {
            _allApps.Add(new ProjectOblivionAppListItemViewModel(app));
        }

        ApplyFilter();
        StartSizeEstimation();

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
        var filter = ActiveFilter?.Trim();
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

    private static IEnumerable<ProjectOblivionApp> DeduplicateApps(IEnumerable<ProjectOblivionApp> apps)
    {
        if (apps is null)
        {
            return Enumerable.Empty<ProjectOblivionApp>();
        }

        return apps
            .GroupBy(BuildAppKey)
            .Select(group => group
                .OrderBy(GetSourcePriority)
                .ThenByDescending(app => app.EstimatedSizeBytes ?? 0)
                .ThenBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
                .First());
    }

    private static string BuildAppKey(ProjectOblivionApp app)
    {
        var name = string.IsNullOrWhiteSpace(app.Name) ? app.AppId : app.Name.Trim();
        var publisher = string.IsNullOrWhiteSpace(app.Publisher) ? string.Empty : app.Publisher.Trim();
        return $"{name.ToLowerInvariant()}|{publisher.ToLowerInvariant()}";
    }

    private static int GetSourcePriority(ProjectOblivionApp app)
    {
        if (!string.IsNullOrWhiteSpace(app.Source) && SourcePriority.TryGetValue(app.Source, out var priority))
        {
            return priority;
        }

        return SourcePriority.Count + 1;
    }

    private void StartSizeEstimation()
    {
        if (_allApps.Count == 0)
        {
            return;
        }

        var pending = _allApps.Where(app => app.NeedsSizeComputation).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        _sizeCalculationCancellation?.Cancel();
        var cts = new CancellationTokenSource();
        _sizeCalculationCancellation = cts;
        _ = WarmSizesAsync(pending, cts.Token);
    }

    private async Task WarmSizesAsync(IReadOnlyList<ProjectOblivionAppListItemViewModel> items, CancellationToken token)
    {
        var cache = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (token.IsCancellationRequested)
            {
                break;
            }

            var root = item.PrimaryInstallRoot;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            if (cache.TryGetValue(root, out var cached))
            {
                await RunOnUiThreadAsync(() => item.CompleteSizeCalculation(cached)).ConfigureAwait(false);
                continue;
            }

            await RunOnUiThreadAsync(item.BeginSizeComputation).ConfigureAwait(false);
            long? size;
            try
            {
                size = await Task.Run(() => MeasureDirectorySizeSafe(root, token), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            cache[root] = size;
            await RunOnUiThreadAsync(() => item.CompleteSizeCalculation(size)).ConfigureAwait(false);
        }
    }

    private static long? MeasureDirectorySizeSafe(string root, CancellationToken token)
    {
        try
        {
            return MeasureDirectorySize(root, token);
        }
        catch
        {
            return null;
        }
    }

    private static long MeasureDirectorySize(string root, CancellationToken token)
    {
        long total = 0;
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var current = stack.Pop();

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var info = new FileInfo(file);
                    total += info.Length;
                }
                catch
                {
                    // Ignore files we cannot inspect.
                }
            }

            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(current);
            }
            catch
            {
                continue;
            }

            foreach (var directory in directories)
            {
                stack.Push(directory);
            }
        }

        return total;
    }

    private string BuildSummary()
    {
        if (Apps.Count == 0)
        {
            return "Refresh inventory to begin.";
        }

        var totalSize = _allApps.Sum(app => app.EffectiveSizeBytes ?? 0L);
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

public enum ProjectOblivionAppSizeState
{
    Unknown,
    Calculating,
    Calculated
}

public sealed class ProjectOblivionAppListItemViewModel : ObservableObject
{
    private static readonly string[] EmptyTags = Array.Empty<string>();
    private readonly string? _primaryInstallRoot;
    private long? _computedSizeBytes;
    private ProjectOblivionAppSizeState _sizeState;

    public ProjectOblivionAppListItemViewModel(ProjectOblivionApp model)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        Tags = Model.Tags.IsDefaultOrEmpty ? EmptyTags : Model.Tags;
        SourceDisplay = BuildSourceDisplay(Model.Source);
        SourceBadge = SourceDisplay.ToUpperInvariant();
        Monogram = BuildMonogram(Model.Name);
        IconImage = LoadIconImage();
        _primaryInstallRoot = ResolvePrimaryInstallRoot();
        _sizeState = Model.EstimatedSizeBytes is null or <= 0
            ? ProjectOblivionAppSizeState.Unknown
            : ProjectOblivionAppSizeState.Calculated;
    }

    public ProjectOblivionApp Model { get; }

    public string AppId => Model.AppId;

    public string Name => Model.Name;

    public string Monogram { get; }

    public ImageSource? IconImage { get; }

    public bool HasIcon => IconImage is not null;

    public string SourceDisplay { get; }

    public string SourceBadge { get; }

    public IReadOnlyList<string> Tags { get; }

    public bool HasTags => Tags.Count > 0;

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

    public ProjectOblivionAppSizeState SizeState
    {
        get => _sizeState;
        private set => SetProperty(ref _sizeState, value);
    }

    public string SizeDisplay
    {
        get
        {
            if (SizeState == ProjectOblivionAppSizeState.Calculating)
            {
                return "Calculating…";
            }

            var bytes = _computedSizeBytes ?? Model.EstimatedSizeBytes;
            if (bytes is null or <= 0)
            {
                return "Size unknown";
            }

            return ProjectOblivionPopupViewModel.FormatSize(bytes.Value);
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

    public string? ConfidenceDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Model.Confidence))
            {
                return null;
            }

            var trimmed = Model.Confidence!.Trim();
            return string.Equals(trimmed, SourceDisplay, StringComparison.OrdinalIgnoreCase) ? null : trimmed;
        }
    }

    public bool HasConfidence => !string.IsNullOrWhiteSpace(ConfidenceDisplay);

    public long? EstimatedSizeBytes => Model.EstimatedSizeBytes;

    public long? EffectiveSizeBytes => _computedSizeBytes ?? Model.EstimatedSizeBytes;

    public string? PrimaryInstallRoot => _primaryInstallRoot;

    public bool NeedsSizeComputation => SizeState != ProjectOblivionAppSizeState.Calculated && !string.IsNullOrWhiteSpace(PrimaryInstallRoot);

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
            || Tags.Any(tag => tag.Contains(filter, comparison));
    }

    internal void BeginSizeComputation()
    {
        if (SizeState == ProjectOblivionAppSizeState.Calculating)
        {
            return;
        }

        SizeState = ProjectOblivionAppSizeState.Calculating;
        OnPropertyChanged(nameof(SizeDisplay));
    }

    internal void CompleteSizeCalculation(long? bytes)
    {
        _computedSizeBytes = bytes;
        SizeState = ProjectOblivionAppSizeState.Calculated;
        OnPropertyChanged(nameof(SizeDisplay));
    }

    private static string BuildMonogram(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "?";
        }

        foreach (var ch in value.Trim())
        {
            if (char.IsLetterOrDigit(ch))
            {
                return char.ToUpperInvariant(ch).ToString();
            }
        }

        return value.Trim()[0].ToString().ToUpperInvariant();
    }

    private static string BuildSourceDisplay(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return "Manual entry";
        }

        return source!;
    }

    private ImageSource? LoadIconImage()
    {
        foreach (var candidate in EnumerateIconCandidates())
        {
            var normalized = NormalizeIconCandidate(candidate);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            var image = TryCreateImageSource(normalized);
            if (image is not null)
            {
                return image;
            }
        }

        return null;
    }

    private IEnumerable<string?> EnumerateIconCandidates()
    {
        if (!string.IsNullOrWhiteSpace(Model.Registry?.DisplayIcon))
        {
            yield return Model.Registry!.DisplayIcon!;
        }

        yield return ExtractExecutablePath(Model.UninstallCommand);
        yield return ExtractExecutablePath(Model.QuietUninstallCommand);

        if (!string.IsNullOrWhiteSpace(Model.InstallRoot))
        {
            var root = Model.InstallRoot!;
            yield return Path.Combine(root, $"{Model.Name}.exe");
            foreach (var exe in EnumerateExecutables(root))
            {
                yield return exe;
            }
        }

        if (!Model.InstallRoots.IsDefaultOrEmpty)
        {
            foreach (var root in Model.InstallRoots)
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                yield return Path.Combine(root, $"{Model.Name}.exe");
                foreach (var exe in EnumerateExecutables(root))
                {
                    yield return exe;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateExecutables(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            yield break;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.exe", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            yield break;
        }

        var count = 0;
        foreach (var file in files)
        {
            yield return file;
            count++;
            if (count >= 3)
            {
                yield break;
            }
        }
    }

    private static string? NormalizeIconCandidate(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        var trimmed = candidate.Trim().Trim('"');
        var commaIndex = trimmed.IndexOf(',');
        if (commaIndex > 0)
        {
            trimmed = trimmed[..commaIndex];
        }

        trimmed = Environment.ExpandEnvironmentVariables(trimmed);
        return trimmed;
    }

    private static string? ExtractExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var trimmed = command.Trim();
        if (trimmed.StartsWith('"'))
        {
            var closing = trimmed.IndexOf('"', 1);
            if (closing > 1)
            {
                return trimmed[1..closing];
            }
        }

        var spaceIndex = trimmed.IndexOf(' ');
        return spaceIndex > 0 ? trimmed[..spaceIndex] : trimmed;
    }

    private static ImageSource? TryCreateImageSource(string candidatePath)
    {
        if (!File.Exists(candidatePath))
        {
            return null;
        }

        try
        {
            var extension = Path.GetExtension(candidatePath);
            using Icon? icon = extension.Equals(".ico", StringComparison.OrdinalIgnoreCase)
                ? new Icon(candidatePath)
                : Icon.ExtractAssociatedIcon(candidatePath);

            if (icon is null)
            {
                return null;
            }

            var bitmap = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(64, 64));
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private string? ResolvePrimaryInstallRoot()
    {
        if (!string.IsNullOrWhiteSpace(Model.InstallRoot))
        {
            return Model.InstallRoot;
        }

        if (!Model.InstallRoots.IsDefaultOrEmpty)
        {
            foreach (var candidate in Model.InstallRoots)
            {
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    return candidate;
                }
            }
        }

        if (!Model.ArtifactHints.IsDefaultOrEmpty)
        {
            foreach (var hint in Model.ArtifactHints)
            {
                if (!string.IsNullOrWhiteSpace(hint))
                {
                    return hint;
                }
            }
        }

        return null;
    }
}
