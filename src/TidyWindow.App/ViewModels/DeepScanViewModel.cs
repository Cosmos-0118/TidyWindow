using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.Core.Diagnostics;

namespace TidyWindow.App.ViewModels;

public sealed record DeepScanLocationOption(string Label, string Path, string Description);

public sealed partial class DeepScanViewModel : ViewModelBase
{
    private readonly DeepScanService _deepScanService;
    private readonly MainViewModel _mainViewModel;
    private readonly List<DeepScanFinding> _allFindings = new();
    private readonly int _pageSize = 100;

    private bool _isBusy;
    private bool _isDeleting;
    private string _targetPath = string.Empty;
    private int _minimumSizeMb = 0;
    private int _maxItems = 1000;
    private bool _includeHidden;
    private DateTimeOffset? _lastScanned;
    private string _summary = "Run a scan to surface large files and folders.";
    private string _nameFilter = string.Empty;
    private DeepScanNameMatchMode _selectedMatchMode = DeepScanNameMatchMode.Contains;
    private bool _isCaseSensitiveMatch;
    private bool _includeDirectories;
    private DeepScanLocationOption? _selectedPreset;

    private int _currentPage = 1;
    private int _totalFindings;
    private bool _suppressPresetSync;

    public DeepScanViewModel(DeepScanService deepScanService, MainViewModel mainViewModel)
    {
        _deepScanService = deepScanService ?? throw new ArgumentNullException(nameof(deepScanService));
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? string.Empty;
        PresetLocations = BuildPresetLocations(userProfile);

        var defaultScanRoot = Directory.Exists("C:\\") ? "C:\\" : userProfile;
        TargetPath = defaultScanRoot ?? string.Empty;

        VisibleFindings = new ObservableCollection<DeepScanItemViewModel>();
    }

    public ObservableCollection<DeepScanItemViewModel> VisibleFindings { get; }

    public IReadOnlyList<DeepScanNameMatchMode> NameMatchModes { get; } = Enum.GetValues<DeepScanNameMatchMode>();

    public IReadOnlyList<DeepScanLocationOption> PresetLocations { get; }

    public bool HasResults => _totalFindings > 0;

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public string TargetPath
    {
        get => _targetPath;
        set
        {
            if (SetProperty(ref _targetPath, value ?? string.Empty))
            {
                SyncPresetFromPath(value);
            }
        }
    }

    public int MinimumSizeMb
    {
        get => _minimumSizeMb;
        set => SetProperty(ref _minimumSizeMb, value < 0 ? 0 : value);
    }

    public int MaxItems
    {
        get => _maxItems;
        set => SetProperty(ref _maxItems, value < 1 ? 1 : value);
    }

    public bool IncludeHidden
    {
        get => _includeHidden;
        set => SetProperty(ref _includeHidden, value);
    }

    public DateTimeOffset? LastScanned
    {
        get => _lastScanned;
        set
        {
            if (SetProperty(ref _lastScanned, value))
            {
                OnPropertyChanged(nameof(LastScannedDisplay));
            }
        }
    }

    public string Summary
    {
        get => _summary;
        set => SetProperty(ref _summary, value ?? string.Empty);
    }

    public string NameFilter
    {
        get => _nameFilter;
        set => SetProperty(ref _nameFilter, value ?? string.Empty);
    }

    public DeepScanNameMatchMode SelectedMatchMode
    {
        get => _selectedMatchMode;
        set => SetProperty(ref _selectedMatchMode, value);
    }

    public bool IsCaseSensitiveMatch
    {
        get => _isCaseSensitiveMatch;
        set => SetProperty(ref _isCaseSensitiveMatch, value);
    }

    public bool IncludeDirectories
    {
        get => _includeDirectories;
        set => SetProperty(ref _includeDirectories, value);
    }

    public DeepScanLocationOption? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (SetProperty(ref _selectedPreset, value))
            {
                ApplyPreset(value);
            }
        }
    }

    public int PageSize => _pageSize;

    public int CurrentPage
    {
        get => _currentPage;
        private set => SetCurrentPageInternal(value, refreshVisible: true, raisePaginationProperties: true);
    }

    public int TotalFindings => _totalFindings;

    public int TotalPages => _totalFindings == 0 ? 1 : (int)Math.Ceiling(_totalFindings / (double)PageSize);

    public string PageDisplay => HasResults ? $"Page {CurrentPage} of {TotalPages}" : "Page 0 of 0";

    public bool CanGoToPreviousPage => HasResults && CurrentPage > 1;

    public bool CanGoToNextPage => HasResults && CurrentPage < TotalPages;

    public string LastScannedDisplay => LastScanned is DateTimeOffset timestamp
        ? $"Last scanned {timestamp.LocalDateTime:G}"
        : "No scans yet.";

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            _mainViewModel.SetStatusMessage("Scanning for large files...");

            ClearFindings();
            Summary = "Scanning…";

            var filters = string.IsNullOrWhiteSpace(NameFilter)
                ? Array.Empty<string>()
                : NameFilter.Split(new[] { ';', ',', '|' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            var request = new DeepScanRequest(
                TargetPath,
                MaxItems,
                MinimumSizeMb,
                IncludeHidden,
                filters,
                SelectedMatchMode,
                IsCaseSensitiveMatch,
                IncludeDirectories);

            var progress = new Progress<DeepScanProgressUpdate>(update => ApplyProgress(update));

            var result = await _deepScanService.RunScanAsync(request, progress);

            ReplaceFindings(result.Findings);

            LastScanned = result.GeneratedAt;
            Summary = result.TotalCandidates > 0
                ? FormatFinalSummary(result.TotalCandidates, result.TotalSizeDisplay, result.CategoryTotals)
                : "No items above the configured threshold.";

            _mainViewModel.SetStatusMessage(
                result.TotalCandidates > 0
                    ? $"Deep scan complete: {result.TotalCandidates} candidates totaling {result.TotalSizeDisplay}."
                    : "Deep scan completed with no candidates.");
        }
        catch (Exception ex)
        {
            _mainViewModel.SetStatusMessage($"Deep scan failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenContainingFolder(DeepScanItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{item.Path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _mainViewModel.SetStatusMessage($"Unable to open file location: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DeleteFindingAsync(DeepScanItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (IsBusy || _isDeleting)
        {
            return;
        }

        _isDeleting = true;

        try
        {
            var path = item.Path;
            if (string.IsNullOrWhiteSpace(path))
            {
                _mainViewModel.SetStatusMessage("Delete failed: missing file path.");
                return;
            }

            var name = item.Name;
            var existsOnDisk = item.IsDirectory ? Directory.Exists(path) : File.Exists(path);
            if (!existsOnDisk)
            {
                var removedCount = RemoveFinding(item.Finding);
                var suffix = removedCount > 1 ? $" and {removedCount - 1} related item(s)" : string.Empty;
                _mainViewModel.SetStatusMessage($"'{name}' was already missing. Removed from the results{suffix}.");
                return;
            }

            _mainViewModel.SetStatusMessage($"Deleting '{name}'…");
            var (success, error) = await Task.Run(() => TryDeleteItem(item));

            if (success)
            {
                var removedCount = RemoveFinding(item.Finding);
                var suffix = removedCount > 1 ? $" and {removedCount - 1} nested item(s)" : string.Empty;
                _mainViewModel.SetStatusMessage($"Deleted '{name}'{suffix}.");
            }
            else
            {
                var message = string.IsNullOrWhiteSpace(error) ? "Unknown error." : error;
                _mainViewModel.SetStatusMessage($"Delete failed: {message}");
            }
        }
        finally
        {
            _isDeleting = false;
        }
    }

    [RelayCommand]
    private void PreviousPage()
    {
        if (CanGoToPreviousPage)
        {
            CurrentPage--;
        }
    }

    [RelayCommand]
    private void NextPage()
    {
        if (CanGoToNextPage)
        {
            CurrentPage++;
        }
    }

    private void RefreshVisibleFindings()
    {
        if (!HasResults)
        {
            if (VisibleFindings.Count > 0)
            {
                VisibleFindings.Clear();
            }

            return;
        }

        var startIndex = (_currentPage - 1) * PageSize;
        var endExclusive = Math.Min(startIndex + PageSize, _allFindings.Count);
        var targetCount = Math.Max(0, endExclusive - startIndex);

        for (var i = VisibleFindings.Count - 1; i >= targetCount; i--)
        {
            VisibleFindings.RemoveAt(i);
        }

        for (var offset = 0; offset < targetCount; offset++)
        {
            var finding = _allFindings[startIndex + offset];
            if (offset < VisibleFindings.Count)
            {
                var current = VisibleFindings[offset];
                if (!ReferenceEquals(current.Finding, finding))
                {
                    VisibleFindings[offset] = new DeepScanItemViewModel(finding);
                }
            }
            else
            {
                VisibleFindings.Add(new DeepScanItemViewModel(finding));
            }
        }
    }

    private void ClearFindings()
    {
        _allFindings.Clear();
        SetTotalFindings(0, resetPage: true, forceRefresh: true);
    }

    private int RemoveFinding(DeepScanFinding? finding)
    {
        if (finding is null)
        {
            return 0;
        }

        var comparison = StringComparison.OrdinalIgnoreCase;
        var directoryPrefix = finding.IsDirectory ? NormalizeDirectoryPrefix(finding.Path) : null;
        var removed = 0;
        for (var index = _allFindings.Count - 1; index >= 0; index--)
        {
            var current = _allFindings[index];
            if (ReferenceEquals(current, finding)
                || string.Equals(current.Path, finding.Path, comparison)
                || (directoryPrefix is not null && current.Path.StartsWith(directoryPrefix, comparison)))
            {
                _allFindings.RemoveAt(index);
                removed++;
            }
        }

        if (removed == 0)
        {
            return 0;
        }

        SetTotalFindings(_allFindings.Count, resetPage: false, forceRefresh: true);
        UpdateSummaryFromFindings();
        return removed;
    }

    private static string? NormalizeDirectoryPrefix(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        path = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
    }

    private void UpdateSummaryFromFindings()
    {
        if (_allFindings.Count == 0)
        {
            Summary = "No items above the configured threshold.";
            return;
        }

        long totalSize = 0;
        for (var index = 0; index < _allFindings.Count; index++)
        {
            var size = _allFindings[index].SizeBytes;
            if (size > 0)
            {
                totalSize += size;
            }
        }

        Summary = $"{_allFindings.Count} item(s) • {FormatBytes(totalSize)}";
    }

    private void SetTotalFindings(int totalCount, bool resetPage, bool forceRefresh = false)
    {
        if (totalCount < 0)
        {
            totalCount = 0;
        }

        var previousCount = _totalFindings;
        var countChanged = previousCount != totalCount;

        if (countChanged)
        {
            _totalFindings = totalCount;
            OnPropertyChanged(nameof(TotalFindings));
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(TotalPages));
        }

        var targetPage = resetPage
            ? 1
            : (_totalFindings == 0 ? 1 : Math.Min(Math.Max(_currentPage, 1), TotalPages));

        var pageChanged = SetCurrentPageInternal(targetPage, refreshVisible: false, raisePaginationProperties: false);

        OnPropertyChanged(nameof(PageDisplay));
        OnPropertyChanged(nameof(CanGoToPreviousPage));
        OnPropertyChanged(nameof(CanGoToNextPage));

        if (forceRefresh || countChanged || pageChanged)
        {
            RefreshVisibleFindings();
        }
    }

    private bool SetCurrentPageInternal(int desiredPage, bool refreshVisible, bool raisePaginationProperties)
    {
        var clamped = desiredPage < 1 ? 1 : desiredPage > TotalPages ? TotalPages : desiredPage;
        var changed = _currentPage != clamped;
        if (changed)
        {
            _currentPage = clamped;
            OnPropertyChanged(nameof(CurrentPage));
        }

        if (raisePaginationProperties)
        {
            OnPropertyChanged(nameof(PageDisplay));
            OnPropertyChanged(nameof(CanGoToPreviousPage));
            OnPropertyChanged(nameof(CanGoToNextPage));
        }

        if (refreshVisible)
        {
            RefreshVisibleFindings();
        }

        return changed;
    }

    private void ApplyProgress(DeepScanProgressUpdate update)
    {
        ReplaceFindings(update.Findings);
        Summary = BuildStreamingSummary(update);
    }

    private void ReplaceFindings(IReadOnlyList<DeepScanFinding> findings, bool resetPage = true)
    {
        if (!ShouldUpdateFindings(findings))
        {
            return;
        }

        _allFindings.Clear();
        if (_allFindings.Capacity < findings.Count)
        {
            _allFindings.Capacity = findings.Count;
        }

        for (var index = 0; index < findings.Count; index++)
        {
            _allFindings.Add(findings[index]);
        }

        SetTotalFindings(_allFindings.Count, resetPage, forceRefresh: true);
    }

    private bool ShouldUpdateFindings(IReadOnlyList<DeepScanFinding>? findings)
    {
        if (findings is null)
        {
            return _allFindings.Count != 0;
        }

        if (_allFindings.Count != findings.Count)
        {
            return true;
        }

        for (var index = 0; index < findings.Count; index++)
        {
            if (!ReferenceEquals(_allFindings[index], findings[index]))
            {
                return true;
            }
        }

        return false;
    }

    private string BuildStreamingSummary(DeepScanProgressUpdate update)
    {
        var categorySuffix = FormatCategorySummary(update.CategoryTotals);
        return $"Scanning… processed {update.ProcessedEntries:N0} item(s) • {update.ProcessedSizeDisplay}{categorySuffix}";
    }

    private static string FormatFinalSummary(int totalCandidates, string totalSizeDisplay, IReadOnlyDictionary<string, long> categories)
    {
        var categorySuffix = FormatCategorySummary(categories);
        return $"{totalCandidates} item(s) • {totalSizeDisplay}{categorySuffix}";
    }

    private static string FormatCategorySummary(IReadOnlyDictionary<string, long>? categories)
    {
        if (categories is null || categories.Count == 0)
        {
            return string.Empty;
        }

        var top = categories
            .Where(static pair => pair.Value > 0)
            .OrderByDescending(static pair => pair.Value)
            .Take(3)
            .Select(static pair => $"{pair.Key}: {FormatBytes(pair.Value)}")
            .ToList();

        return top.Count == 0 ? string.Empty : " • " + string.Join(", ", top);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        double size = bytes;
        var units = new[] { "B", "KB", "MB", "GB", "TB", "PB" };
        var unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:0.0} {units[unitIndex]}";
    }

    private void ApplyPreset(DeepScanLocationOption? preset)
    {
        if (preset is null || _suppressPresetSync)
        {
            return;
        }

        try
        {
            _suppressPresetSync = true;
            if (!string.IsNullOrWhiteSpace(preset.Path) && !string.Equals(TargetPath, preset.Path, StringComparison.OrdinalIgnoreCase))
            {
                TargetPath = preset.Path;
            }
        }
        finally
        {
            _suppressPresetSync = false;
        }
    }

    private void SyncPresetFromPath(string? path)
    {
        if (_suppressPresetSync)
        {
            return;
        }

        try
        {
            _suppressPresetSync = true;
            var match = PresetLocations.FirstOrDefault(option => string.Equals(option.Path, path, StringComparison.OrdinalIgnoreCase));
            if (!EqualityComparer<DeepScanLocationOption?>.Default.Equals(match, _selectedPreset))
            {
                _selectedPreset = match;
                OnPropertyChanged(nameof(SelectedPreset));
            }
        }
        finally
        {
            _suppressPresetSync = false;
        }
    }

    private static IReadOnlyList<DeepScanLocationOption> BuildPresetLocations(string defaultRoot)
    {
        var items = new List<DeepScanLocationOption>();

        if (!string.IsNullOrWhiteSpace(defaultRoot))
        {
            items.Add(new DeepScanLocationOption("User profile", defaultRoot, "Scan the full user profile."));

            var downloads = SafeCombine(defaultRoot, "Downloads");
            if (!string.IsNullOrWhiteSpace(downloads))
            {
                items.Add(new DeepScanLocationOption("Downloads", downloads, "Focus on downloaded installers and archives."));
            }

            var desktop = SafeCombine(defaultRoot, "Desktop");
            if (!string.IsNullOrWhiteSpace(desktop))
            {
                items.Add(new DeepScanLocationOption("Desktop", desktop, "Review files stacked on the desktop."));
            }
        }

        void AddKnownFolder(Environment.SpecialFolder folder, string label, string description)
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrWhiteSpace(path))
            {
                items.Add(new DeepScanLocationOption(label, path, description));
            }
        }

        AddKnownFolder(Environment.SpecialFolder.MyDocuments, "Documents", "Large documents and archives.");
        AddKnownFolder(Environment.SpecialFolder.MyPictures, "Pictures", "High-resolution photos and media.");
        AddKnownFolder(Environment.SpecialFolder.MyVideos, "Videos", "Video captures and renders.");

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            items.Add(new DeepScanLocationOption("Local AppData", localAppData, "Application caches and logs."));

            var browserCache = SafeCombine(localAppData, "Microsoft", "Edge", "User Data");
            if (!string.IsNullOrWhiteSpace(browserCache))
            {
                items.Add(new DeepScanLocationOption("Edge profiles", browserCache, "Inspect heavy browser profiles."));
            }
        }

        return items
            .Where(option => !string.IsNullOrWhiteSpace(option.Path) && Directory.Exists(option.Path))
            .ToList();
    }

    private static string SafeCombine(string basePath, params string[] segments)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return string.Empty;
        }

        try
        {
            return Path.Combine(new[] { basePath }.Concat(segments).ToArray());
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static (bool Success, string? Error) TryDeleteItem(DeepScanItemViewModel item)
    {
        try
        {
            var targetPath = item.Path;
            if (item.IsDirectory)
            {
                if (!Directory.Exists(targetPath))
                {
                    return (true, null);
                }

                ClearDirectoryReadOnlyFlags(targetPath);
                Directory.Delete(targetPath, recursive: true);
            }
            else
            {
                if (!File.Exists(targetPath))
                {
                    return (true, null);
                }

                ClearFileReadOnlyFlag(targetPath);
                File.Delete(targetPath);
            }

            return (true, null);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or DirectoryNotFoundException
            or FileNotFoundException
            or NotSupportedException
            or System.Security.SecurityException)
        {
            return (false, ex.Message);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static void ClearFileReadOnlyFlag(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return;
            }

            var attributes = File.GetAttributes(filePath);
            if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
            {
                File.SetAttributes(filePath, attributes & ~FileAttributes.ReadOnly);
            }
        }
        catch (Exception)
        {
        }
    }

    private static void ClearDirectoryReadOnlyFlags(string directoryPath)
    {
        try
        {
            var root = new DirectoryInfo(directoryPath);
            if (!root.Exists)
            {
                return;
            }

            var stack = new Stack<DirectoryInfo>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                try
                {
                    current.Attributes &= ~FileAttributes.ReadOnly;
                }
                catch (Exception)
                {
                }

                FileSystemInfo[] entries;
                try
                {
                    entries = current.GetFileSystemInfos();
                }
                catch (Exception)
                {
                    continue;
                }

                for (var index = 0; index < entries.Length; index++)
                {
                    var entry = entries[index];
                    try
                    {
                        entry.Attributes &= ~FileAttributes.ReadOnly;
                    }
                    catch (Exception)
                    {
                    }

                    if (entry is DirectoryInfo directory)
                    {
                        stack.Push(directory);
                    }
                }
            }
        }
        catch (Exception)
        {
        }
    }
}

public sealed class DeepScanItemViewModel
{
    public DeepScanItemViewModel(DeepScanFinding finding)
    {
        Finding = finding ?? throw new ArgumentNullException(nameof(finding));
    }

    public DeepScanFinding Finding { get; }

    public string Name => Finding.Name;

    public string Directory => Finding.Directory;

    public string Path => Finding.Path;

    public string Extension => Finding.Extension;

    public string SizeDisplay => Finding.SizeDisplay;

    public string ModifiedDisplay => Finding.ModifiedDisplay;

    public bool IsDirectory => Finding.IsDirectory;

    public string Category => Finding.Category;

    public string KindDisplay => Finding.KindDisplay;
}
