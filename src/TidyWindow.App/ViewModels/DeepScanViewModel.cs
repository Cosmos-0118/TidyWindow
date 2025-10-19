using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.Core.Diagnostics;

namespace TidyWindow.App.ViewModels;

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

    public DeepScanViewModel(DeepScanService deepScanService, MainViewModel mainViewModel)
    {
        _deepScanService = deepScanService;
        _mainViewModel = mainViewModel;

        _targetPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Findings.CollectionChanged += OnFindingsChanged;
    }

    public ObservableCollection<DeepScanItemViewModel> Findings { get; } = new();

    public IReadOnlyList<DeepScanNameMatchMode> NameMatchModes { get; } = Enum.GetValues<DeepScanNameMatchMode>();

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
        // Update binding dependents when summary text shifts.
        OnPropertyChanged(nameof(HasResults));
    }

    private void OnFindingsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasResults));
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
