using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
    private readonly string _inventorySnapshotRoot;
    private string? _inventorySnapshotPath;
    private bool _refreshRequestedAfterBusy;
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
    private static readonly AppIdentityComparer IdentityComparer = new();
    private static readonly Regex VersionSuffixPattern = new("\\s+(?:v)?\\d+(?:[\\._-]\\d+)*(?:\\s*(?:x64|x86))?\\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NonAlphaNumericPattern = new("[^a-z0-9]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
        _inventorySnapshotRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWindow", "ProjectOblivion", "inventory");
        Popup.CleanupCompleted += OnPopupRunCompleted;
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
            _refreshRequestedAfterBusy = true;
            return;
        }

        do
        {
            _refreshRequestedAfterBusy = false;
            await RefreshCoreAsync().ConfigureAwait(false);
        }
        while (_refreshRequestedAfterBusy);
    }

    private async Task RefreshCoreAsync()
    {
        IsBusy = true;
        _mainViewModel.SetStatusMessage("Collecting installed applications...");
        var snapshotPath = CreateInventorySnapshotPath();

        try
        {
            var snapshot = await _inventoryService.GetInventoryAsync(snapshotPath).ConfigureAwait(false);
            await RunOnUiThreadAsync(() =>
            {
                ReplaceInventorySnapshot(snapshotPath);
                ApplySnapshot(snapshot);
                _mainViewModel.SetStatusMessage($"Inventory ready • {snapshot.Apps.Length:N0} app(s).");
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            TryDeleteFile(snapshotPath);
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

        if (!HasInventorySnapshot())
        {
            _inventorySnapshotPath = null;
            return;
        }

        var existing = _allApps.FirstOrDefault(app => string.Equals(app.AppId, Popup.TargetApp.AppId, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new ProjectOblivionAppListItemViewModel(Popup.TargetApp);
            _allApps.Insert(0, existing);
            ApplyFilter();
        }

        Popup.Prepare(Popup.TargetApp, _inventorySnapshotPath!, preserveExistingState: true);
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

        if (!HasInventorySnapshot())
        {
            _mainViewModel.SetStatusMessage("Refresh inventory before running Project Oblivion.");
            return;
        }

        var preserveState = Popup.CanResume(target.Model);
        Popup.Prepare(target.Model, _inventorySnapshotPath!, preserveState);
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
            .GroupBy(BuildAppIdentity, IdentityComparer)
            .Select(MergeAppGroup);
    }

    private static ProjectOblivionApp MergeAppGroup(IGrouping<AppIdentity, ProjectOblivionApp> group)
    {
        var ordered = group
            .OrderBy(GetSourcePriority)
            .ThenByDescending(app => app.EstimatedSizeBytes ?? 0)
            .ThenBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var primary = ordered[0];

        string? installRoot = SelectFirstNonEmpty(ordered.Select(a => ProjectOblivionPathHelper.NormalizeDirectoryCandidate(a.InstallRoot)));
        if (string.IsNullOrWhiteSpace(installRoot))
        {
            installRoot = SelectFirstNonEmpty(ordered
                .SelectMany(EnumerateAllInstallRoots)
                .Select(ProjectOblivionPathHelper.NormalizeDirectoryCandidate));
        }

        var installRoots = ordered
            .SelectMany(a => EnumerateStrings(a.InstallRoots))
            .Select(ProjectOblivionPathHelper.NormalizeDirectoryCandidate)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        if (string.IsNullOrWhiteSpace(installRoot) && installRoots.Length > 0)
        {
            installRoot = installRoots[0];
        }

        var tags = BuildDistinctStrings(ordered.SelectMany(a => EnumerateStrings(a.Tags)));
        var artifactHints = BuildDistinctStrings(ordered.SelectMany(a => EnumerateStrings(a.ArtifactHints)));
        var processHints = BuildDistinctStrings(ordered.SelectMany(a => EnumerateStrings(a.ProcessHints)));
        var serviceHints = BuildDistinctStrings(ordered.SelectMany(a => EnumerateStrings(a.ServiceHints)));
        var managerHints = ordered
            .SelectMany(a => EnumerateManagerHints(a.ManagerHints))
            .Distinct()
            .ToImmutableArray();

        var estimatedSize = primary.EstimatedSizeBytes;
        if (estimatedSize is null or <= 0)
        {
            estimatedSize = ordered
                .Select(a => a.EstimatedSizeBytes)
                .FirstOrDefault(value => value.HasValue && value.Value > 0);
        }

        var scope = string.IsNullOrWhiteSpace(primary.Scope)
            ? SelectFirstNonEmpty(ordered.Select(a => a.Scope))
            : primary.Scope;
        var publisher = string.IsNullOrWhiteSpace(primary.Publisher)
            ? SelectFirstNonEmpty(ordered.Select(a => a.Publisher))
            : primary.Publisher;
        var version = string.IsNullOrWhiteSpace(primary.Version)
            ? SelectFirstNonEmpty(ordered.Select(a => a.Version))
            : primary.Version;
        var source = string.IsNullOrWhiteSpace(primary.Source)
            ? SelectFirstNonEmpty(ordered.Select(a => a.Source))
            : primary.Source;
        var confidence = string.IsNullOrWhiteSpace(primary.Confidence)
            ? SelectFirstNonEmpty(ordered.Select(a => a.Confidence))
            : primary.Confidence;
        var registry = primary.Registry ?? ordered.Select(a => a.Registry).FirstOrDefault(r => r is not null);

        var normalizedInstallRoots = installRoots.Length > 0
            ? installRoots
            : (primary.InstallRoots.IsDefault ? ImmutableArray<string>.Empty : primary.InstallRoots);

        var normalizedTags = tags.Length > 0
            ? tags
            : (primary.Tags.IsDefault ? ImmutableArray<string>.Empty : primary.Tags);

        var normalizedArtifacts = artifactHints.Length > 0
            ? artifactHints
            : (primary.ArtifactHints.IsDefault ? ImmutableArray<string>.Empty : primary.ArtifactHints);

        var normalizedProcessHints = processHints.Length > 0
            ? processHints
            : (primary.ProcessHints.IsDefault ? ImmutableArray<string>.Empty : primary.ProcessHints);

        var normalizedServiceHints = serviceHints.Length > 0
            ? serviceHints
            : (primary.ServiceHints.IsDefault ? ImmutableArray<string>.Empty : primary.ServiceHints);

        var normalizedManagerHints = managerHints.Length > 0
            ? managerHints
            : (primary.ManagerHints.IsDefault ? ImmutableArray<ProjectOblivionManagerHint>.Empty : primary.ManagerHints);

        return primary with
        {
            InstallRoot = installRoot ?? primary.InstallRoot,
            InstallRoots = normalizedInstallRoots,
            Tags = normalizedTags,
            ArtifactHints = normalizedArtifacts,
            ProcessHints = normalizedProcessHints,
            ServiceHints = normalizedServiceHints,
            ManagerHints = normalizedManagerHints,
            EstimatedSizeBytes = estimatedSize ?? primary.EstimatedSizeBytes,
            Scope = scope ?? primary.Scope,
            Publisher = publisher ?? primary.Publisher,
            Version = version ?? primary.Version,
            Source = source ?? primary.Source,
            Confidence = confidence ?? primary.Confidence,
            Registry = registry ?? primary.Registry
        };
    }

    private static AppIdentity BuildAppIdentity(ProjectOblivionApp app)
    {
        if (!string.IsNullOrWhiteSpace(app.PackageFamilyName))
        {
            return AppIdentity.PackageFamily(app.PackageFamilyName.Trim().ToLowerInvariant());
        }

        var managerKey = BuildManagerKey(app);
        if (!string.IsNullOrWhiteSpace(managerKey))
        {
            return AppIdentity.Manager(managerKey);
        }

        var installKey = BuildInstallRootKey(app);
        if (!string.IsNullOrWhiteSpace(installKey))
        {
            return AppIdentity.InstallRoot(installKey);
        }

        var normalizedName = NormalizeNameForKey(app.Name, app.AppId);
        var normalizedPublisher = NormalizeNameForKey(app.Publisher, string.Empty);
        return AppIdentity.Name(normalizedName, normalizedPublisher);
    }

    private static string? BuildManagerKey(ProjectOblivionApp app)
    {
        if (app.ManagerHints.IsDefaultOrEmpty)
        {
            return null;
        }

        foreach (var hint in app.ManagerHints)
        {
            if (string.IsNullOrWhiteSpace(hint.Manager) || string.IsNullOrWhiteSpace(hint.PackageId))
            {
                continue;
            }

            return $"manager:{hint.Manager.Trim().ToLowerInvariant()}|pkg:{hint.PackageId.Trim().ToLowerInvariant()}";
        }

        return null;
    }

    private static string? BuildInstallRootKey(ProjectOblivionApp app)
    {
        foreach (var raw in EnumerateAllInstallRoots(app))
        {
            var normalized = ProjectOblivionPathHelper.NormalizeDirectoryCandidate(raw);
            if (!string.IsNullOrWhiteSpace(normalized) && ProjectOblivionPathHelper.IsHighConfidenceInstallPath(normalized))
            {
                return $"install:{normalized.ToLowerInvariant()}";
            }
        }

        return null;
    }

    private static IEnumerable<string?> EnumerateAllInstallRoots(ProjectOblivionApp app)
    {
        if (!string.IsNullOrWhiteSpace(app.InstallRoot))
        {
            yield return app.InstallRoot;
        }

        if (!app.InstallRoots.IsDefaultOrEmpty)
        {
            foreach (var root in app.InstallRoots)
            {
                yield return root;
            }
        }

        if (!app.ArtifactHints.IsDefaultOrEmpty)
        {
            foreach (var hint in app.ArtifactHints)
            {
                yield return hint;
            }
        }

        if (!string.IsNullOrWhiteSpace(app.Registry?.InstallLocation))
        {
            yield return app.Registry!.InstallLocation;
        }
    }

    private static ImmutableArray<string> BuildDistinctStrings(IEnumerable<string> values)
    {
        return values
            .Select(value => value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    private static IEnumerable<string> EnumerateStrings(ImmutableArray<string> values)
    {
        return values.IsDefaultOrEmpty ? Array.Empty<string>() : values;
    }

    private static IEnumerable<ProjectOblivionManagerHint> EnumerateManagerHints(ImmutableArray<ProjectOblivionManagerHint> values)
    {
        return values.IsDefaultOrEmpty ? Array.Empty<ProjectOblivionManagerHint>() : values;
    }

    private static string? SelectFirstNonEmpty(IEnumerable<string?> values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string NormalizeNameForKey(string? value, string fallback)
    {
        var input = string.IsNullOrWhiteSpace(value) ? fallback : value;
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var trimmed = VersionSuffixPattern.Replace(input.Trim(), string.Empty);
        trimmed = trimmed.Replace("®", string.Empty, StringComparison.Ordinal)
            .Replace("™", string.Empty, StringComparison.Ordinal);
        var normalized = NonAlphaNumericPattern.Replace(trimmed.ToLowerInvariant(), " ").Trim();
        return normalized.Replace(" ", string.Empty, StringComparison.Ordinal);
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

    private bool HasInventorySnapshot()
    {
        return !string.IsNullOrWhiteSpace(_inventorySnapshotPath) && File.Exists(_inventorySnapshotPath);
    }

    private string CreateInventorySnapshotPath()
    {
        Directory.CreateDirectory(_inventorySnapshotRoot);
        var fileName = $"oblivion-inventory-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json";
        return Path.Combine(_inventorySnapshotRoot, fileName);
    }

    private void ReplaceInventorySnapshot(string newSnapshotPath)
    {
        if (string.IsNullOrWhiteSpace(newSnapshotPath))
        {
            return;
        }

        var previous = _inventorySnapshotPath;
        _inventorySnapshotPath = newSnapshotPath;
        if (!string.IsNullOrWhiteSpace(previous) && !string.Equals(previous, newSnapshotPath, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteFile(previous);
        }

        CleanupInventorySnapshots();
    }

    private void CleanupInventorySnapshots()
    {
        try
        {
            if (!Directory.Exists(_inventorySnapshotRoot))
            {
                return;
            }

            var files = Directory.GetFiles(_inventorySnapshotRoot, "oblivion-inventory-*.json");
            if (files.Length == 0)
            {
                return;
            }

            var keepCount = 3;
            var ordered = files
                .OrderByDescending(file =>
                {
                    try
                    {
                        return File.GetCreationTimeUtc(file);
                    }
                    catch
                    {
                        return DateTime.MinValue;
                    }
                })
                .ToList();

            foreach (var file in ordered.Skip(keepCount))
            {
                if (!string.IsNullOrWhiteSpace(_inventorySnapshotPath)
                    && string.Equals(Path.GetFullPath(file), Path.GetFullPath(_inventorySnapshotPath), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                TryDeleteFile(file);
            }
        }
        catch
        {
            // Snapshot cleanup is best-effort.
        }
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore IO cleanup failures.
        }
    }

    private void MarkInventorySnapshotStale()
    {
        if (string.IsNullOrWhiteSpace(_inventorySnapshotPath))
        {
            return;
        }

        TryDeleteFile(_inventorySnapshotPath);
        _inventorySnapshotPath = null;
    }

    private async void OnPopupRunCompleted(object? sender, ProjectOblivionRunCompletedEventArgs e)
    {
        MarkInventorySnapshotStale();

        if (IsBusy)
        {
            _refreshRequestedAfterBusy = true;
            return;
        }

        try
        {
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _activityLog.LogWarning("Project Oblivion", "Automatic inventory refresh failed after cleanup.", new[] { ex.ToString() });
        }
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
    private static readonly HashSet<string> GenericSystemExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "msiexec.exe",
        "setup.exe",
        "install.exe",
        "uninstall.exe",
        "powershell.exe",
        "pwsh.exe",
        "cmd.exe",
        "conhost.exe",
        "rundll32.exe",
        "dism.exe",
        "appinstaller.exe",
        "wuauclt.exe"
    };
    private static readonly HashSet<string> ImageFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".bmp"
    };
    private static readonly string WindowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string SystemDirectory = Environment.SystemDirectory;
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();
    private readonly string? _primaryInstallRoot;
    private readonly string[] _nameTokens;
    private long? _computedSizeBytes;
    private ProjectOblivionAppSizeState _sizeState;

    public ProjectOblivionAppListItemViewModel(ProjectOblivionApp model)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        Tags = Model.Tags.IsDefaultOrEmpty ? EmptyTags : Model.Tags;
        SourceDisplay = BuildSourceDisplay(Model.Source);
        SourceBadge = SourceDisplay.ToUpperInvariant();
        Monogram = BuildMonogram(Model.Name);
        _nameTokens = BuildNameTokens(Model.Name);
        _primaryInstallRoot = ResolvePrimaryInstallRoot();
        IconImage = LoadIconImage();
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
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prioritized = new List<(string Path, int Score)>();

        foreach (var candidate in EnumerateIconCandidates())
        {
            var normalized = NormalizeIconCandidate(candidate);
            if (string.IsNullOrWhiteSpace(normalized) || !File.Exists(normalized))
            {
                continue;
            }

            if (!seen.Add(normalized))
            {
                continue;
            }

            prioritized.Add((normalized, ScoreIconCandidate(normalized)));
        }

        foreach (var entry in prioritized
                     .OrderByDescending(item => item.Score)
                     .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
        {
            var image = TryCreateImageSource(entry.Path);
            if (image is not null)
            {
                return image;
            }
        }

        if (string.Equals(Model.Source, "appx", StringComparison.OrdinalIgnoreCase))
        {
            var storeProxyIcon = TryLoadStoreProxyIcon();
            if (storeProxyIcon is not null)
            {
                return storeProxyIcon;
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

        foreach (var root in EnumerateKnownRoots())
        {
            var nameCandidate = BuildNameBasedExecutableCandidate(root);
            if (!string.IsNullOrWhiteSpace(nameCandidate))
            {
                yield return nameCandidate;
            }

            foreach (var exe in EnumerateExecutables(root))
            {
                yield return exe;
            }

            foreach (var asset in EnumerateImageAssets(root))
            {
                yield return asset;
            }
        }

        if (string.Equals(Model.Source, "appx", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var proxy in EnumerateStoreProxyExecutables())
            {
                yield return proxy;
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

    private static IEnumerable<string> EnumerateImageAssets(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            yield break;
        }

        var patterns = new[] { "*.ico", "*icon*.png", "*logo*.png" };
        foreach (var pattern in patterns)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            var count = 0;
            foreach (var file in files)
            {
                yield return file;
                count++;
                if (count >= 3)
                {
                    break;
                }
            }
        }
    }

    private IEnumerable<string> EnumerateKnownRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in EnumerateRawRoots())
        {
            var normalized = ProjectOblivionPathHelper.NormalizeDirectoryCandidate(raw);
            if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
            {
                yield return normalized;
            }
        }
    }

    private IEnumerable<string?> EnumerateRawRoots()
    {
        if (!string.IsNullOrWhiteSpace(_primaryInstallRoot))
        {
            yield return _primaryInstallRoot;
        }

        yield return Model.InstallRoot;

        if (!Model.InstallRoots.IsDefaultOrEmpty)
        {
            foreach (var root in Model.InstallRoots)
            {
                yield return root;
            }
        }

        if (!Model.ArtifactHints.IsDefaultOrEmpty)
        {
            foreach (var hint in Model.ArtifactHints)
            {
                yield return hint;
            }
        }

        if (!string.IsNullOrWhiteSpace(Model.Registry?.InstallLocation))
        {
            yield return Model.Registry!.InstallLocation;
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
            if (extension.Equals(".ico", StringComparison.OrdinalIgnoreCase))
            {
                using var icon = new Icon(candidatePath);
                return CreateBitmapFromIcon(icon);
            }

            if (ImageFileExtensions.Contains(extension))
            {
                return CreateBitmapFromImage(candidatePath);
            }

            using var associatedIcon = Icon.ExtractAssociatedIcon(candidatePath);
            return associatedIcon is null ? null : CreateBitmapFromIcon(associatedIcon);
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? CreateBitmapFromIcon(Icon icon)
    {
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

    private static ImageSource CreateBitmapFromImage(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 64;
        bitmap.DecodePixelHeight = 64;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private ImageSource? TryLoadStoreProxyIcon()
    {
        foreach (var path in EnumerateStoreProxyExecutables().Take(5))
        {
            var image = TryCreateImageSource(path);
            if (image is not null)
            {
                return image;
            }
        }

        return null;
    }

    private string? BuildNameBasedExecutableCandidate(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(Model.Name))
        {
            return null;
        }

        var sanitized = new string(Model.Name
            .Where(ch => Array.IndexOf(InvalidFileNameChars, ch) < 0)
            .ToArray()).Trim();

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return null;
        }

        try
        {
            return Path.Combine(root, $"{sanitized}.exe");
        }
        catch
        {
            return null;
        }
    }

    private IEnumerable<string> EnumerateStoreProxyExecutables()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(Model.Name))
        {
            var compact = new string(Model.Name.Where(char.IsLetterOrDigit).ToArray());
            if (!string.IsNullOrWhiteSpace(compact))
            {
                candidates.Add($"{compact}*.exe");
            }

            if (_nameTokens.Length > 0)
            {
                var token = new string(_nameTokens[0].Where(char.IsLetterOrDigit).ToArray());
                if (!string.IsNullOrWhiteSpace(token))
                {
                    candidates.Add($"{token}*.exe");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(Model.PackageFamilyName))
        {
            var family = Model.PackageFamilyName!;
            var prefix = family.Split('_')[0];
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                candidates.Add($"{prefix}*.exe");
            }
        }

        if (candidates.Count == 0)
        {
            yield break;
        }

        var windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");
        if (!Directory.Exists(windowsApps))
        {
            yield break;
        }

        foreach (var pattern in candidates)
        {
            IEnumerable<string> matches;
            try
            {
                matches = Directory.EnumerateFiles(windowsApps, pattern, SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            var count = 0;
            foreach (var match in matches)
            {
                yield return match;
                count++;
                if (count >= 3)
                {
                    break;
                }
            }
        }
    }

    private string? ResolvePrimaryInstallRoot()
    {
        var normalized = ProjectOblivionPathHelper.NormalizeDirectoryCandidate(Model.InstallRoot);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            return normalized;
        }

        if (!Model.InstallRoots.IsDefaultOrEmpty)
        {
            foreach (var candidate in Model.InstallRoots)
            {
                normalized = ProjectOblivionPathHelper.NormalizeDirectoryCandidate(candidate);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    return normalized;
                }
            }
        }

        if (!Model.ArtifactHints.IsDefaultOrEmpty)
        {
            foreach (var hint in Model.ArtifactHints)
            {
                normalized = ProjectOblivionPathHelper.NormalizeDirectoryCandidate(hint);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    return normalized;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(Model.Registry?.InstallLocation))
        {
            normalized = ProjectOblivionPathHelper.NormalizeDirectoryCandidate(Model.Registry!.InstallLocation);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        return null;
    }

    private int ScoreIconCandidate(string candidatePath)
    {
        var score = 0;
        var extension = Path.GetExtension(candidatePath);

        if (ImageFileExtensions.Contains(extension))
        {
            score += 6;
        }
        else if (extension.Equals(".ico", StringComparison.OrdinalIgnoreCase))
        {
            score += 5;
        }
        else if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) || extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            score += 3;
        }

        if (PathMatchesInstallRoot(candidatePath))
        {
            score += 5;
        }

        var fileName = Path.GetFileNameWithoutExtension(candidatePath);
        if (NameTokensMatch(fileName))
        {
            score += 4;
        }

        if (IsGenericSystemExecutable(Path.GetFileName(candidatePath)))
        {
            score -= 6;
        }

        if (IsSystemPath(candidatePath))
        {
            score -= 3;
        }

        return score;
    }

    private bool PathMatchesInstallRoot(string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        var comparison = StringComparison.OrdinalIgnoreCase;

        if (!string.IsNullOrWhiteSpace(_primaryInstallRoot) && candidatePath.StartsWith(_primaryInstallRoot, comparison))
        {
            return true;
        }

        foreach (var root in EnumerateKnownRoots())
        {
            if (!string.IsNullOrWhiteSpace(root) && candidatePath.StartsWith(root, comparison))
            {
                return true;
            }
        }

        return false;
    }

    private bool NameTokensMatch(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || _nameTokens.Length == 0)
        {
            return false;
        }

        var normalized = candidate.ToLowerInvariant();
        foreach (var token in _nameTokens)
        {
            if (normalized.Contains(token, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsGenericSystemExecutable(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        return GenericSystemExecutables.Contains(fileName);
    }

    private static bool IsSystemPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var comparison = StringComparison.OrdinalIgnoreCase;

        if (!string.IsNullOrWhiteSpace(SystemDirectory) && path.StartsWith(SystemDirectory, comparison))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(WindowsDirectory) && path.StartsWith(WindowsDirectory, comparison))
        {
            return true;
        }

        return false;
    }

    private static string[] BuildNameTokens(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return value
            .Split(new[] { ' ', '.', '-', '_', '(', ')', '[', ']', ':' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim().ToLowerInvariant())
            .Where(token => token.Length > 2)
            .Distinct()
            .ToArray();
    }

}

internal readonly record struct AppIdentity(string Kind, string Primary, string? Secondary)
{
    public static AppIdentity PackageFamily(string value) => new("family", value ?? string.Empty, string.Empty);
    public static AppIdentity Manager(string value) => new("manager", value ?? string.Empty, string.Empty);
    public static AppIdentity InstallRoot(string value) => new("install", value ?? string.Empty, string.Empty);
    public static AppIdentity Name(string value, string? publisher) => new("name", value ?? string.Empty, publisher ?? string.Empty);
}

// Treats publisher-less identities as wildcards so weaker records (like shortcuts) collapse into richer entries sharing the same name.
internal sealed class AppIdentityComparer : IEqualityComparer<AppIdentity>
{
    public bool Equals(AppIdentity x, AppIdentity y)
    {
        if (!string.Equals(x.Kind, y.Kind, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(x.Primary, y.Primary, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(x.Kind, "name", StringComparison.Ordinal))
        {
            return string.Equals(x.Secondary, y.Secondary, StringComparison.Ordinal);
        }

        if (string.IsNullOrEmpty(x.Secondary) || string.IsNullOrEmpty(y.Secondary))
        {
            return true;
        }

        return string.Equals(x.Secondary, y.Secondary, StringComparison.Ordinal);
    }

    public int GetHashCode(AppIdentity obj)
    {
        var hash = HashCode.Combine(obj.Kind, obj.Primary);
        if (!string.Equals(obj.Kind, "name", StringComparison.Ordinal) && !string.IsNullOrEmpty(obj.Secondary))
        {
            hash = HashCode.Combine(hash, obj.Secondary);
        }

        return hash;
    }
}

internal static class ProjectOblivionPathHelper
{
    public static string? NormalizeDirectoryCandidate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(trimmed);

        try
        {
            if (Directory.Exists(expanded))
            {
                return Path.GetFullPath(expanded);
            }

            if (File.Exists(expanded))
            {
                var directory = Path.GetDirectoryName(expanded);
                return string.IsNullOrWhiteSpace(directory) ? null : Path.GetFullPath(directory);
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    public static bool IsHighConfidenceInstallPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = path.Replace('/', '\\').ToLowerInvariant();
        if (normalized.Contains("start menu"))
        {
            return false;
        }

        if (normalized.Contains("\\shortcuts\\"))
        {
            return false;
        }

        return true;
    }
}
