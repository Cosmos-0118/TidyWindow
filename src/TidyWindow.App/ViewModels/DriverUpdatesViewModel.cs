using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.Services;
using TidyWindow.Core.Updates;

namespace TidyWindow.App.ViewModels;

public sealed partial class DriverUpdatesViewModel : ViewModelBase
{
    private readonly DriverUpdateService _driverUpdateService;
    private readonly MainViewModel _mainViewModel;
    private DateTimeOffset? _lastChecked;

    public DriverUpdatesViewModel(DriverUpdateService driverUpdateService, MainViewModel mainViewModel)
    {
        _driverUpdateService = driverUpdateService ?? throw new ArgumentNullException(nameof(driverUpdateService));
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));

        Updates = new ObservableCollection<DriverUpdateItemViewModel>();
        Updates.CollectionChanged += OnUpdatesCollectionChanged;

        Warnings = new ObservableCollection<string>();
        Warnings.CollectionChanged += OnWarningsCollectionChanged;

        Summary = "Scan Windows Update for driver version differences.";
    }

    public ObservableCollection<DriverUpdateItemViewModel> Updates { get; }

    public ObservableCollection<string> Warnings { get; }

    public bool HasResults => Updates.Count > 0;

    public bool HasWarnings => Warnings.Count > 0;

    public DateTimeOffset? LastChecked
    {
        get => _lastChecked;
        private set
        {
            if (SetProperty(ref _lastChecked, value))
            {
                OnPropertyChanged(nameof(LastCheckedDisplay));
            }
        }
    }

    public string LastCheckedDisplay => LastChecked is DateTimeOffset timestamp
        ? $"Last checked {timestamp.LocalDateTime:G}"
        : "No scans yet.";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _includeOptional;

    [ObservableProperty]
    private string _summary;

    [ObservableProperty]
    private DriverUpdateItemViewModel? _selectedUpdate;

    public bool HasScanned { get; private set; }

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
            HasScanned = true;
            _mainViewModel.SetStatusMessage("Checking for driver updates...");

            Warnings.Clear();

            var result = await _driverUpdateService.DetectAsync(IncludeOptional);

            Updates.Clear();
            foreach (var info in result.Updates)
            {
                Updates.Add(new DriverUpdateItemViewModel(info));
            }

            LastChecked = result.GeneratedAt;
            Summary = BuildSummary(result.Updates);

            foreach (var warning in result.Warnings)
            {
                if (!string.IsNullOrWhiteSpace(warning))
                {
                    Warnings.Add(warning);
                }
            }

            var actionable = result.Updates.Count(update => update.Status == DriverUpdateStatus.UpdateAvailable);
            if (result.Updates.Count == 0)
            {
                _mainViewModel.SetStatusMessage("No driver updates were offered.");
            }
            else if (actionable == 0)
            {
                _mainViewModel.SetStatusMessage($"Checked {result.Updates.Count} driver update(s); all current.");
            }
            else
            {
                _mainViewModel.SetStatusMessage($"Detected {actionable} driver update(s) requiring attention.");
            }
        }
        catch (Exception ex)
        {
            Warnings.Add(ex.Message);
            _mainViewModel.SetStatusMessage($"Driver scan failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string BuildSummary(IReadOnlyCollection<DriverUpdateInfo> updates)
    {
        if (updates.Count == 0)
        {
            return "No driver updates detected.";
        }

        var actionable = updates.Count(update => update.Status == DriverUpdateStatus.UpdateAvailable);
        if (actionable == 0)
        {
            return updates.Count == 1
                ? "1 driver update is already at the latest version."
                : $"{updates.Count} driver updates are already at the latest version.";
        }

        return actionable == 1
            ? "1 driver requires an update."
            : $"{actionable} drivers require updates.";
    }

    private void OnUpdatesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasResults));
    }

    private void OnWarningsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasWarnings));
    }
}

public sealed partial class DriverUpdateItemViewModel : ObservableObject
{
    private readonly DriverUpdateInfo _info;

    public DriverUpdateItemViewModel(DriverUpdateInfo info)
    {
        _info = info ?? throw new ArgumentNullException(nameof(info));
    }

    public string Title => _info.Title;

    public string DeviceName => _info.DeviceName;

    public string? Manufacturer => _info.Manufacturer;

    public bool HasManufacturer => !string.IsNullOrWhiteSpace(Manufacturer);

    public string CurrentVersion => string.IsNullOrWhiteSpace(_info.CurrentVersion) ? "Unknown" : _info.CurrentVersion;

    public string AvailableVersion => string.IsNullOrWhiteSpace(_info.AvailableVersion) ? "Unknown" : _info.AvailableVersion;

    public string VersionSummary => $"{CurrentVersion} → {AvailableVersion}";

    public string? CurrentVersionDateDisplay => _info.CurrentVersionDate?.ToLocalTime().ToString("yyyy-MM-dd");

    public string? AvailableVersionDateDisplay => _info.AvailableVersionDate?.ToLocalTime().ToString("yyyy-MM-dd");

    public bool HasCurrentVersionTimestamp => _info.CurrentVersionDate.HasValue;

    public bool HasAvailableVersionTimestamp => _info.AvailableVersionDate.HasValue;

    public DriverUpdateStatus Status => _info.Status;

    public string StatusLabel => Status switch
    {
        DriverUpdateStatus.UpdateAvailable => "Update available",
        DriverUpdateStatus.UpToDate => "Latest installed",
        _ => "Version unknown"
    };

    public bool IsOptional => _info.IsOptional;

    public bool ShowOptionalBadge => IsOptional && Status == DriverUpdateStatus.UpdateAvailable;

    public string HardwareIdsDisplay => _info.HardwareIds.Count == 0
        ? "No hardware identifiers detected."
        : string.Join(Environment.NewLine, _info.HardwareIds);

    public IReadOnlyList<string> Categories => _info.Categories;

    public string CategoryDisplay => _info.Categories.Count == 0
        ? "Uncategorized"
        : string.Join(", ", _info.Categories);

    public IReadOnlyList<Uri> InformationLinks => _info.InformationLinks;

    public bool HasInformationLinks => _info.InformationLinks.Count > 0;

    public Uri? PrimaryInformationLink => HasInformationLinks ? _info.InformationLinks[0] : null;

    public string? Description => _info.Description;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
}
