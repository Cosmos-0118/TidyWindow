using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.Core.Diagnostics;

namespace TidyWindow.App.ViewModels;

public sealed record DeepScanLocationOption(string Label, string Path, string Description);

public sealed partial class DeepScanViewModel : ViewModelBase
{
    private readonly DeepScanService _deepScanService;
    private readonly MainViewModel _mainViewModel;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _targetPath;

    [ObservableProperty]
    private int _minimumSizeMb = 200;

    [ObservableProperty]
    private int _maxItems = 25;

    [ObservableProperty]
    private bool _includeHidden;

    [ObservableProperty]
    private DateTimeOffset? _lastScanned;

    [ObservableProperty]
    private string _summary = "Run a scan to surface large files and folders.";

    [ObservableProperty]
    private string _nameFilter = string.Empty;

    [ObservableProperty]
    private DeepScanNameMatchMode _selectedMatchMode = DeepScanNameMatchMode.Contains;

    [ObservableProperty]
    private bool _isCaseSensitiveMatch;

    [ObservableProperty]
    private bool _includeDirectories;

    [ObservableProperty]
    private DeepScanLocationOption? _selectedPreset;

    public DeepScanViewModel(DeepScanService deepScanService, MainViewModel mainViewModel)
    {
        _deepScanService = deepScanService;
        _mainViewModel = mainViewModel;

        _targetPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        PresetLocations = BuildPresetLocations(_targetPath);
        SelectedPreset = PresetLocations.FirstOrDefault(option => string.Equals(option.Path, _targetPath, StringComparison.OrdinalIgnoreCase));

        Findings.CollectionChanged += OnFindingsChanged;
    }

    public ObservableCollection<DeepScanItemViewModel> Findings { get; } = new();

    public IReadOnlyList<DeepScanNameMatchMode> NameMatchModes { get; } = Enum.GetValues<DeepScanNameMatchMode>();

    public IReadOnlyList<DeepScanLocationOption> PresetLocations { get; }

    public bool HasResults => Findings.Count > 0;

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

            Findings.Clear();
            foreach (var finding in result.Findings)
            {
                Findings.Add(new DeepScanItemViewModel(finding));
            }

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

    partial void OnLastScannedChanged(DateTimeOffset? oldValue, DateTimeOffset? newValue)
    {
        OnPropertyChanged(nameof(LastScannedDisplay));
    }

    partial void OnSummaryChanged(string value)
    {
        OnPropertyChanged(nameof(HasResults));
    }

    private void OnFindingsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasResults));
    }

    partial void OnSelectedPresetChanged(DeepScanLocationOption? value)
    {
        if (value is null)
        {
            return;
        }

        if (!string.Equals(TargetPath, value.Path, StringComparison.OrdinalIgnoreCase))
        {
            TargetPath = value.Path;
        }
    }

    partial void OnTargetPathChanged(string value)
    {
        var match = PresetLocations.FirstOrDefault(option => string.Equals(option.Path, value, StringComparison.OrdinalIgnoreCase));
        if (!EqualityComparer<DeepScanLocationOption?>.Default.Equals(match, SelectedPreset))
        {
            SelectedPreset = match;
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
