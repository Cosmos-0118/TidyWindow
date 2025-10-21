using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.Core.Diagnostics;

namespace TidyWindow.App.ViewModels;

public sealed record DeepScanLocationOption(string Label, string Path, string Description);

public sealed partial class DeepScanViewModel : ViewModelBase
{
    private readonly DeepScanService _deepScanService;
    private readonly MainViewModel _mainViewModel;
    private readonly List<DeepScanItemViewModel> _allFindings = new();
    private readonly int _pageSize = 100;

    private bool _isBusy;
    private string _targetPath = string.Empty;
    private int _minimumSizeMb = 200;
    private int _maxItems = 500;
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

        var defaultPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? string.Empty;
        PresetLocations = BuildPresetLocations(defaultPath);
        TargetPath = defaultPath;

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
        private set
        {
            var clamped = value < 1 ? 1 : value > TotalPages ? TotalPages : value;
            if (SetProperty(ref _currentPage, clamped))
            {
                OnPropertyChanged(nameof(PageDisplay));
                OnPropertyChanged(nameof(CanGoToPreviousPage));
                OnPropertyChanged(nameof(CanGoToNextPage));
                RefreshVisibleFindings();
            }
        }
    }

    public int TotalFindings
    {
        get => _totalFindings;
        private set
        {
            if (SetProperty(ref _totalFindings, value))
            {
                OnPropertyChanged(nameof(HasResults));
                OnPropertyChanged(nameof(TotalPages));
                OnPropertyChanged(nameof(PageDisplay));
                OnPropertyChanged(nameof(CanGoToPreviousPage));
                OnPropertyChanged(nameof(CanGoToNextPage));
                RefreshVisibleFindings();
            }
        }
    }

    public int TotalPages => TotalFindings == 0 ? 1 : (int)Math.Ceiling(TotalFindings / (double)PageSize);

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

            var result = await _deepScanService.RunScanAsync(request);

            _allFindings.Clear();
            _allFindings.AddRange(result.Findings.Select(static finding => new DeepScanItemViewModel(finding)));

            TotalFindings = _allFindings.Count;
            CurrentPage = 1;

            LastScanned = result.GeneratedAt;
            Summary = result.TotalCandidates > 0
                ? $"{result.TotalCandidates} item(s) • {result.TotalSizeDisplay} across findings"
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
        VisibleFindings.Clear();

        if (!HasResults)
        {
            return;
        }

        var skip = (CurrentPage - 1) * PageSize;
        foreach (var item in _allFindings.Skip(skip).Take(PageSize))
        {
            VisibleFindings.Add(item);
        }
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

    public string KindDisplay => Finding.KindDisplay;
}
